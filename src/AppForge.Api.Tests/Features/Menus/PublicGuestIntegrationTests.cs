using System.Diagnostics.CodeAnalysis;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AppForge.Api.Domain.Entities;
using AppForge.Api.Domain.ValueTypes;
using AppForge.Api.Features.Auth;
using AppForge.Api.Features.Datasets;
using AppForge.Api.Features.Provisioning;
using AppForge.Api.Features.PublicGuest;
using AppForge.Api.Features.Tenancy;
using AppForge.Api.Infrastructure.Persistence;
using AppForge.Api.Tests.Infrastructure;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AppForge.Api.Tests.Features.Menus;

// Public guest session: issuance, claims, the central allowlist (route + resource), hidden
// role/user surfaces, and password-login denial.
[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class PublicGuestIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private static readonly Guid TenantAdminRoleId = new("00000000-0000-0000-0000-000000000001");
    private const string Schema = "pgs_main";
    private const string ReferencedDatasetQuery =
        "SELECT * FROM (VALUES (1, 'Alice'), (2, 'Bob')) AS t(emp_id, emp_name)";
    private const string SecretDatasetQuery =
        "SELECT * FROM (VALUES (1, 'classified')) AS t(secret_id, secret_value)";

    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    private Tenant _tenant = null!;
    private Guid _referencedDatasetId;
    private Guid _secretDatasetId;
    private Guid _publicMenuId;

    public PublicGuestIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:appforge", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
            });

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppForgeDbContext>();
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlRawAsync(
                "TRUNCATE TABLE tenants, tenant_user_index RESTART IDENTITY CASCADE;");
            // Fresh schemas per test (each test re-provisions the same tenant).
            await db.Database.ExecuteSqlRawAsync(
                $"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE; DROP SCHEMA IF EXISTS \"{Schema}_datasets\" CASCADE; " +
                "DROP SCHEMA IF EXISTS \"pgs_suspended\" CASCADE;");
        }

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        await SeedTenantAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

    // ── seeding ────────────────────────────────────────────────────────────

    private async Task<Tenant> ProvisionTenantAsync(string schemaName, string status = "Active")
    {
        using var scope = _factory!.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppForgeDbContext>();
        var provisioning = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();

        var tenant = new Tenant { Name = $"Tenant {schemaName}", SchemaName = schemaName, Status = "Provisioning" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        await provisioning.ProvisionSchemaAsync(tenant, CancellationToken.None);
        tenant.Status = status;
        await db.SaveChangesAsync();
        return tenant;
    }

    private static string Layout(string datasetName, Guid datasetId) => JsonSerializer.Serialize(new
    {
        id = "root",
        type = "Stack",
        properties = new { },
        children = new object[]
        {
            new { id = "img", type = "Image", properties = new { src = "pgs/hero.png" }, children = Array.Empty<object>() },
            new
            {
                id = "dd",
                type = "Dropdown",
                properties = new { fieldKey = "x", optionsDatasetId = datasetName },
                children = Array.Empty<object>(),
            },
            new
            {
                id = "ds",
                type = "DatasetComponent",
                properties = new { optionsDatasetId = datasetId.ToString() },
                children = Array.Empty<object>(),
            },
            new
            {
                id = "rep",
                type = "Repeater",
                properties = new { rowDesignerId = "pgs_child" },
                children = Array.Empty<object>(),
            },
        },
    });

    private static string TextForm(string fieldKey) => JsonSerializer.Serialize(new
    {
        id = "root",
        type = "Stack",
        properties = new { },
        children = new object[]
        {
            new { id = "f1", type = "TextInput", properties = new { fieldKey }, children = Array.Empty<object>() },
        },
    });

    private async Task SeedTenantAsync()
    {
        _tenant = await ProvisionTenantAsync(Schema);

        using var scope = _factory!.Services.CreateScope();
        var ctx = (TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>();
        ctx.Set(_tenant.Id, _tenant.SchemaName);
        var db = scope.ServiceProvider.GetRequiredService<AppForgeDbContext>();

        // Datasets (VIEWs in the tenant's own datasets namespace).
        var csb = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString) { SearchPath = Schema };
        await using (var setup = new NpgsqlConnection(csb.ConnectionString))
        {
            await setup.OpenAsync();
            await setup.ExecuteAsync($"CREATE SCHEMA IF NOT EXISTS \"{Schema}_datasets\"");
        }

        var viewManager = scope.ServiceProvider.GetRequiredService<DatasetViewManager>();
        var factory = scope.ServiceProvider.GetRequiredService<DbConnectionFactory>();

        async Task<Guid> AddDatasetAsync(string name, string query)
        {
            Assert.True(DatasetName.TryCreate(name, out var dsName, out _));
            await using (var conn = await factory.CreateOpenConnectionAsync())
            {
                var tx = await conn.BeginTransactionAsync();
                await viewManager.CreateAsync(conn, tx, dsName!, query, CancellationToken.None);
                await tx.CommitAsync();
                await tx.DisposeAsync();
            }

            var ds = new CustomDataset
            {
                DatasetName = dsName!.Value,
                IsCustomQuery = true,
                QueryType = DatasetQueryTypes.View,
                Query = query,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.CustomDatasets.Add(ds);
            await db.SaveChangesAsync();
            return ds.Id;
        }

        _referencedDatasetId = await AddDatasetAsync("pgs_ref_ds", ReferencedDatasetQuery);
        _secretDatasetId = await AddDatasetAsync("pgs_secret_ds", SecretDatasetQuery);

        // Designers: a public VIEW layout (references dataset by name + id, Image src, and the
        // Repeater child designer), the Repeater child, and a private CRUD designer.
        void AddDesigner(string id, string mode, string root)
        {
            db.ComponentSchemas.Add(new ComponentSchema
            {
                DesignerId = id, DisplayName = id, Mode = mode, CreatedAt = DateTimeOffset.UtcNow,
            });
            db.ComponentSchemaVersions.Add(new ComponentSchemaVersion
            {
                DesignerId = id,
                Version = 1,
                Status = "Published",
                RootElement = root,
                CreatedAt = DateTimeOffset.UtcNow,
                PublishedAt = DateTimeOffset.UtcNow,
            });
        }

        AddDesigner("pgs_layout", "VIEW", Layout("pgs_ref_ds", _referencedDatasetId));
        AddDesigner("pgs_child", "CRUD", TextForm("title"));
        AddDesigner("pgs_private", "CRUD", TextForm("title"));

        var layoutMenu = new Menu
        {
            Id = Guid.NewGuid(), Name = "Public layout", IsPublic = true, IsActive = true,
            DesignerId = "pgs_layout", BoundVersion = 1, ProvisioningStatus = "NotApplicable",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var privateMenu = new Menu
        {
            Id = Guid.NewGuid(), Name = "Private", IsPublic = false, IsActive = true,
            DesignerId = "pgs_private", BoundVersion = 1, ProvisioningStatus = "Success",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        // Repeater child table is provisioned via a NON-public menu: only the layout reference
        // (not menu publicity) may make it reachable for the guest.
        var childMenu = new Menu
        {
            Id = Guid.NewGuid(), Name = "Child", IsPublic = false, IsActive = true,
            DesignerId = "pgs_child", BoundVersion = 1, ProvisioningStatus = "Success",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Menus.AddRange(layoutMenu, privateMenu, childMenu);
        _publicMenuId = layoutMenu.Id;

        // A tenant admin (for the hidden-surface assertions).
        var admin = new User
        {
            Email = "admin@pgs.tenant.local",
            DisplayName = "Admin",
            PasswordHash = "x",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(admin);
        db.UserRoles.Add(new UserRole { User = admin, RoleId = TenantAdminRoleId, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        // Physical tables for the CRUD designers.
        var ddl = scope.ServiceProvider.GetRequiredService<DdlEmitter>();
        foreach (var id in new[] { "pgs_child", "pgs_private" })
        {
            await ddl.EmitAsync(new ProvisioningJob(MenuId: null, id, Version: 1, ActorId: null), CancellationToken.None);
            await using var conn = await factory.CreateOpenConnectionAsync();
            await conn.ExecuteAsync($"INSERT INTO \"{id}\" (title, is_deleted) VALUES ('row-of-{id}', false)");
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private async Task<string> GuestTokenAsync()
    {
        using var response = await _client!.PostAsync(new Uri($"/api/public/{Schema}/session", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("accessToken").GetString()!;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? token, HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = content };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _client!.SendAsync(request);
    }

    private string AdminToken()
    {
        using var scope = _factory!.Services.CreateScope();
        var jwt = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();
        var user = new User { Id = Guid.NewGuid(), Email = "admin@pgs.tenant.local" };
        return jwt.CreateAccessToken(user, ["platform-admin"], _tenant.Id);
    }

    private static string Fingerprint(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        return string.Join("|", r.GetProperty("status").GetInt32(), r.GetProperty("code").GetString(),
            r.GetProperty("messageKey").GetString());
    }

    // ── session issuance ───────────────────────────────────────────────────

    [Fact]
    public async Task Session_IssuesShortLivedGuestToken_WithClaims_AndNoRefreshCookie()
    {
        using var response = await _client!.PostAsync(new Uri($"/api/public/{Schema}/session", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("expiresIn").GetInt32() is > 0 and <= 900);
        Assert.False(body.TryGetProperty("refreshToken", out _));

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body.GetProperty("accessToken").GetString());
        Assert.Equal("true", jwt.Claims.Single(c => c.Type == "guest").Value);
        Assert.Equal("public-guest", jwt.Claims.Single(c => c.Type == "roles").Value);
        Assert.Equal(_tenant.Id.ToString(), jwt.Claims.Single(c => c.Type == "tenantId").Value);
        Assert.True(Guid.TryParse(jwt.Claims.Single(c => c.Type == "userId").Value, out _));
        Assert.True(jwt.ValidTo - jwt.ValidFrom <= TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task Session_UnknownInvalidAndSuspendedTenants_Return404WithSameBodyAsMenus()
    {
        await ProvisionTenantAsync("pgs_suspended", status: "Suspended");
        using var baseline = await _client!.GetAsync(new Uri("/api/public/pgs_missing/menus", UriKind.Relative));
        var baselineFp = Fingerprint(await baseline.Content.ReadAsStringAsync());

        foreach (var slug in new[] { "pgs_missing", "Bad-Slug", "pgs_suspended" })
        {
            using var response = await _client.PostAsync(new Uri($"/api/public/{slug}/session", UriKind.Relative), null);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(baselineFp, Fingerprint(await response.Content.ReadAsStringAsync()));
        }
    }

    // ── guest allowed ──────────────────────────────────────────────────────

    [Fact]
    public async Task Guest_CanReadAllowlistedDatasetRows_AndDesignerData()
    {
        var token = await GuestTokenAsync();
        var rowsBody = JsonContent.Create(new { page = 1, pageSize = 10 });

        using var rows = await SendAsync(HttpMethod.Post, $"/api/datasets/{_referencedDatasetId}/rows", token, rowsBody);
        Assert.Equal(HttpStatusCode.OK, rows.StatusCode);

        // Repeater child designer is reachable through the layout (nested) ...
        using var child = await SendAsync(HttpMethod.Get, "/api/data/pgs_child", token);
        Assert.Equal(HttpStatusCode.OK, child.StatusCode);
        Assert.Contains("row-of-pgs_child", await child.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // ... and so is its layout schema.
        using var schema = await SendAsync(HttpMethod.Get, "/api/designers/pgs_child", token);
        Assert.Equal(HttpStatusCode.OK, schema.StatusCode);

        // The dataset detail never leaks SQL to a guest.
        using var detail = await SendAsync(HttpMethod.Get, $"/api/datasets/{_referencedDatasetId}", token);
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var detailJson = await detail.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, detailJson.GetProperty("query").ValueKind);
    }

    [Fact]
    public async Task Guest_PresignAllowedForLayoutKey_DeniedForOthers()
    {
        var token = await GuestTokenAsync();

        using var allowed = await SendAsync(HttpMethod.Get, "/api/files/presign?key=pgs/hero.png", token);
        // The allowlist let it through (storage itself may be unavailable in tests).
        Assert.NotEqual(HttpStatusCode.Forbidden, allowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, allowed.StatusCode);

        using var other = await SendAsync(HttpMethod.Get, "/api/files/presign?key=pgs/other.png", token);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);

        using var forPrivateDesigner = await SendAsync(HttpMethod.Get, "/api/files/presign?key=pgs_private/f_1.png", token);
        Assert.Equal(HttpStatusCode.Forbidden, forPrivateDesigner.StatusCode);
    }

    // ── guest denied ───────────────────────────────────────────────────────

    [Fact]
    public async Task Guest_Denied_NonAllowlistedResources_Writes_AndAdminSurfaces()
    {
        var token = await GuestTokenAsync();
        var json = () => JsonContent.Create(new { page = 1, pageSize = 10 });

        // Non-allowlisted dataset / designer data.
        using var secretRows = await SendAsync(HttpMethod.Post, $"/api/datasets/{_secretDatasetId}/rows", token, json());
        Assert.Equal(HttpStatusCode.Forbidden, secretRows.StatusCode);
        using var privateData = await SendAsync(HttpMethod.Get, "/api/data/pgs_private", token);
        Assert.Equal(HttpStatusCode.Forbidden, privateData.StatusCode);
        using var privateSchema = await SendAsync(HttpMethod.Get, "/api/designers/pgs_private", token);
        Assert.Equal(HttpStatusCode.Forbidden, privateSchema.StatusCode);

        // Writes, even on an allowlisted designer.
        using var write = await SendAsync(HttpMethod.Post, "/api/data/pgs_child", token,
            JsonContent.Create(new { title = "hacked" }));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        using var del = await SendAsync(HttpMethod.Delete, $"/api/data/pgs_child/{Guid.NewGuid()}", token);
        Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);

        // Admin / users / menus / auth / dataset catalogue.
        foreach (var path in new[]
                 {
                     "/api/admin/users", "/api/admin/roles", "/api/users/active", "/api/menus",
                     "/api/datasets", "/api/designers", "/api/auth/me",
                 })
        {
            using var response = await SendAsync(HttpMethod.Get, path, token);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.Contains("FORBIDDEN", body, StringComparison.Ordinal);
        }
    }

    // ── hidden role / user, password login ─────────────────────────────────

    [Fact]
    public async Task GuestUserAndRole_AreHiddenFromUsersAndRolesLists()
    {
        await GuestTokenAsync(); // ensures the guest user exists
        var admin = AdminToken();

        using var users = await SendAsync(HttpMethod.Get, "/api/admin/users?pageSize=100", admin);
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        var usersBody = await users.Content.ReadAsStringAsync();
        Assert.DoesNotContain("public-guest@", usersBody, StringComparison.Ordinal);
        Assert.Contains("admin@pgs.tenant.local", usersBody, StringComparison.Ordinal);

        using var roles = await SendAsync(HttpMethod.Get, "/api/admin/roles?pageSize=100", admin);
        Assert.Equal(HttpStatusCode.OK, roles.StatusCode);
        Assert.DoesNotContain("public-guest", await roles.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var active = await SendAsync(HttpMethod.Get, "/api/users/active", admin);
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        Assert.DoesNotContain("public-guest@", await active.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GuestUser_CannotLogInWithAPassword_EvenWhenRoutedAndKnown()
    {
        await GuestTokenAsync();
        const string password = "Guest-Pass-123!";
        var email = PublicGuestIdentity.EmailFor(Schema);

        using (var scope = _factory!.Services.CreateScope())
        {
            var ctx = (TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>();
            ctx.Set(_tenant.Id, _tenant.SchemaName);
            var db = scope.ServiceProvider.GetRequiredService<AppForgeDbContext>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var guest = await db.Users.SingleAsync(u => u.Email == email);
            guest.PasswordHash = hasher.Hash(password);
            await db.SaveChangesAsync();

            // Worst case: the address IS routed to the tenant schema.
            var publicDb = scope.ServiceProvider.GetRequiredService<AppForgeDbContext>();
            publicDb.TenantUserIndex.Add(new TenantUserIndexEntry { Email = email, TenantId = _tenant.Id });
            await publicDb.SaveChangesAsync();
        }

        using var login = await _client!.PostAsJsonAsync("/api/auth/login", new { email, password });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }
}
