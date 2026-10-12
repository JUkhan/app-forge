using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using AppForge.Api.Domain.Entities;
using AppForge.Api.Domain.ValueTypes;
using AppForge.Api.Features.Datasets;
using AppForge.Api.Features.Provisioning;
using AppForge.Api.Features.Tenancy;
using AppForge.Api.Infrastructure.Persistence;
using AppForge.Api.Tests.Infrastructure;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AppForge.Api.Tests.Features.Menus;

[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class PublicMenuRecordsIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public PublicMenuRecordsIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:appforge", _postgres.ConnectionString);
                builder.UseSetting("Jwt:SigningKey", "test-signing-key-minimum-32-characters!!");
                builder.UseSetting("Cors:AllowedOrigins:0", "http://localhost:5173");
            });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppForgeDbContext>();
        await db.Database.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE tenants, tenant_user_index RESTART IDENTITY CASCADE;");

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
    }

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

    // Seeds a one-field ("title") CRUD designer, provisions its table in the tenant schema, inserts
    // live + soft-deleted rows, and creates one menu (bound to it unless bound=false). Returns the menu id.
    private async Task<Guid> SeedBoundMenuAsync(
        Tenant tenant,
        string designerId,
        bool isPublic = true,
        bool isActive = true,
        bool bound = true,
        string? authFilterFieldKey = null,
        IReadOnlyList<string>? liveTitles = null,
        IReadOnlyList<string>? deletedTitles = null,
        string provisioningStatus = "Success",
        string mode = "CRUD",
        string? datasetQuery = null)
    {
        using var scope = _factory!.Services.CreateScope();
        var ctx = (TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>();
        ctx.Set(tenant.Id, tenant.SchemaName);
        var db = scope.ServiceProvider.GetRequiredService<AppForgeDbContext>();

        Guid? datasetId = null;
        if (datasetQuery is not null)
        {
            // Tenant-local `{schema}_datasets` namespace + VIEW + custom_dataset row (same steps
            // TenantOnboardingService / DatasetViewManager perform in production).
            var csb = new NpgsqlConnectionStringBuilder(_postgres.ConnectionString)
            {
                SearchPath = tenant.SchemaName,
            };
            await using (var setup = new NpgsqlConnection(csb.ConnectionString))
            {
                await setup.OpenAsync();
                await setup.ExecuteAsync($"CREATE SCHEMA IF NOT EXISTS \"{tenant.SchemaName}_datasets\"");
            }

            Assert.True(DatasetName.TryCreate($"{designerId}_ds", out var dsName, out _));
            var dsFactory = scope.ServiceProvider.GetRequiredService<DbConnectionFactory>();
            var viewManager = scope.ServiceProvider.GetRequiredService<DatasetViewManager>();
            await using (var dsConn = await dsFactory.CreateOpenConnectionAsync())
            {
                var tx = await dsConn.BeginTransactionAsync();
                await viewManager.CreateAsync(dsConn, tx, dsName!, datasetQuery, CancellationToken.None);
                await tx.CommitAsync();
                await tx.DisposeAsync();
            }

            var dataset = new CustomDataset
            {
                DatasetName = dsName!.Value,
                IsCustomQuery = true,
                QueryType = DatasetQueryTypes.View,
                Query = datasetQuery,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.CustomDatasets.Add(dataset);
            await db.SaveChangesAsync();
            datasetId = dataset.Id;
        }

        db.ComponentSchemas.Add(new ComponentSchema
        {
            DesignerId = designerId,
            DisplayName = designerId,
            Mode = mode,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var rootElement = JsonSerializer.Serialize(new
        {
            id = "root",
            type = "Stack",
            properties = new { },
            children = new object[]
            {
                new
                {
                    id = "f1",
                    type = "TextInput",
                    properties = new { fieldKey = "title" },
                    children = Array.Empty<object>(),
                },
            },
        });
        db.ComponentSchemaVersions.Add(new ComponentSchemaVersion
        {
            DesignerId = designerId,
            Version = 1,
            Status = "Published",
            RootElement = rootElement,
            AuthFilterFieldKey = authFilterFieldKey,
            DatasetId = datasetId,
            CreatedAt = DateTimeOffset.UtcNow,
            PublishedAt = DateTimeOffset.UtcNow,
        });
        var menu = new Menu
        {
            Id = Guid.NewGuid(),
            Name = "Records",
            IsPublic = isPublic,
            IsActive = isActive,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        if (bound)
        {
            menu.DesignerId = designerId;
            menu.BoundVersion = 1;
            menu.ProvisioningStatus = provisioningStatus;
        }
        db.Menus.Add(menu);
        await db.SaveChangesAsync();

        if (mode == "VIEW")
        {
            return menu.Id;
        }

        var ddl = scope.ServiceProvider.GetRequiredService<DdlEmitter>();
        await ddl.EmitAsync(
            new ProvisioningJob(MenuId: null, designerId, Version: 1, ActorId: null),
            CancellationToken.None);

        var factory = scope.ServiceProvider.GetRequiredService<DbConnectionFactory>();
        await using var conn = await factory.CreateOpenConnectionAsync();
        foreach (var t in liveTitles ?? [])
        {
            await conn.ExecuteAsync(
                $"INSERT INTO \"{designerId}\" (title, is_deleted) VALUES (@t, false)", new { t });
        }
        foreach (var t in deletedTitles ?? [])
        {
            await conn.ExecuteAsync(
                $"INSERT INTO \"{designerId}\" (title, is_deleted) VALUES (@t, true)", new { t });
        }

        return menu.Id;
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string path)
    {
        using var response = await _client!.GetAsync(new Uri(path, UriKind.Relative));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string Url(string tenant, Guid menuId, string query = "") =>
        $"/api/public/{tenant}/menus/{menuId}/records{query}";

    // 404 bodies may carry a per-request traceId, so compare the stable, meaningful members.
    private static string Fingerprint(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var r = doc.RootElement;
        return string.Join(
            "|",
            r.GetProperty("status").GetInt32(),
            r.GetProperty("title").GetString(),
            r.GetProperty("detail").GetString(),
            r.GetProperty("code").GetString(),
            r.GetProperty("messageKey").GetString());
    }

    private async Task AssertNotServableAsync(string path)
    {
        var (status, body) = await GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Contains("TENANT_NOT_FOUND", body, StringComparison.Ordinal);
        var (_, baseline) = await GetAsync("/api/public/pmr_no_such_tenant/menus");
        Assert.Equal(Fingerprint(baseline), Fingerprint(body));
    }

    [Fact]
    public async Task Records_ServablePublicBoundMenu_ReturnsLiveRowsWithOnlyIdAndUserColumns()
    {
        var tenant = await ProvisionTenantAsync("pmr_ok");
        var menuId = await SeedBoundMenuAsync(
            tenant, "pmr_ok_items", liveTitles: ["alpha", "beta"], deletedTitles: ["gone"]);

        // Client-supplied filter/sort/designer params must be ignored.
        var (status, body) = await GetAsync(
            Url("pmr_ok", menuId, "?filter[isDeleted]=true&sort=created_at:asc&designerId=users"));

        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal(2, root.GetProperty("total").GetInt64());
        Assert.Equal(["title"], root.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList());
        var rows = root.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(
            ["alpha", "beta"],
            rows.Select(r => r.GetProperty("title").GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToList());
        Assert.DoesNotContain("gone", body, StringComparison.Ordinal);
        foreach (var row in rows)
        {
            var keys = row.EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToList();
            Assert.Equal(["id", "title"], keys);
        }
    }

    [Fact]
    public async Task Records_NotServableCases_AllReturnTheSame404()
    {
        var tenant = await ProvisionTenantAsync("pmr_deny");
        var privateMenu = await SeedBoundMenuAsync(tenant, "pmr_deny_private", isPublic: false, liveTitles: ["x"]);
        var inactiveMenu = await SeedBoundMenuAsync(tenant, "pmr_deny_inactive", isActive: false, liveTitles: ["x"]);
        var unboundMenu = await SeedBoundMenuAsync(tenant, "pmr_deny_unbound", bound: false, liveTitles: ["x"]);
        var authFilterMenu = await SeedBoundMenuAsync(
            tenant, "pmr_deny_authfilter", authFilterFieldKey: "title", liveTitles: ["x"]);
        var okMenu = await SeedBoundMenuAsync(tenant, "pmr_deny_ok", liveTitles: ["x"]);
        await ProvisionTenantAsync("pmr_deny_suspended", status: "Suspended");

        await AssertNotServableAsync(Url("pmr_deny", privateMenu));
        await AssertNotServableAsync(Url("pmr_deny", inactiveMenu));
        await AssertNotServableAsync(Url("pmr_deny", unboundMenu));
        await AssertNotServableAsync(Url("pmr_deny", authFilterMenu));
        await AssertNotServableAsync(Url("pmr_deny", Guid.NewGuid()));
        await AssertNotServableAsync("/api/public/pmr_deny/menus/not-a-guid/records");
        await AssertNotServableAsync(Url("pmr_missing", okMenu));
        await AssertNotServableAsync(Url("pmr_deny_suspended", okMenu));
        await AssertNotServableAsync(Url("Bad-Slug", okMenu));

        // Sanity: the same tenant's servable menu still works.
        var (status, _) = await GetAsync(Url("pmr_deny", okMenu));
        Assert.Equal(HttpStatusCode.OK, status);
    }

    private const string DatasetQuery =
        "SELECT * FROM (VALUES (1, 'Alice'), (2, 'Bob'), (3, 'Carol')) AS t(emp_id, emp_name)";

    [Fact]
    public async Task Records_NotApplicableViewMenuBoundToDataset_ReturnsDatasetRows()
    {
        var tenant = await ProvisionTenantAsync("pmr_ds");
        var menuId = await SeedBoundMenuAsync(
            tenant, "pmr_ds_view", provisioningStatus: "NotApplicable", mode: "VIEW",
            datasetQuery: DatasetQuery);

        var (status, body) = await GetAsync(Url("pmr_ds", menuId, "?page=1&pageSize=2"));

        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal(["emp_id", "emp_name"],
            root.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList());
        Assert.Equal(3, root.GetProperty("total").GetInt64());
        Assert.Equal(2, root.GetProperty("pageSize").GetInt32());
        var rows = root.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(2, rows.Count);
        Assert.Equal(
            ["emp_id", "emp_name"],
            rows[0].EnumerateObject().Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal).ToList());
    }

    [Fact]
    public async Task Records_NotApplicableViewMenuWithAuthFilterDataset_Returns404()
    {
        var tenant = await ProvisionTenantAsync("pmr_dsauth");
        var menuId = await SeedBoundMenuAsync(
            tenant, "pmr_dsauth_view", provisioningStatus: "NotApplicable", mode: "VIEW",
            authFilterFieldKey: "emp_id", datasetQuery: DatasetQuery);

        await AssertNotServableAsync(Url("pmr_dsauth", menuId));
    }

    [Fact]
    public async Task Records_NotApplicableViewMenuWithoutDatasetOrTable_Returns404()
    {
        var tenant = await ProvisionTenantAsync("pmr_viewnone");
        var menuId = await SeedBoundMenuAsync(
            tenant, "pmr_viewnone_view", provisioningStatus: "NotApplicable", mode: "VIEW");

        await AssertNotServableAsync(Url("pmr_viewnone", menuId));
    }

    private static string SchemaUrl(string tenant, Guid menuId) =>
        $"/api/public/{tenant}/menus/{menuId}/schema";

    [Fact]
    public async Task Schema_ServablePublicViewMenu_ReturnsPublishedRootElement()
    {
        var tenant = await ProvisionTenantAsync("pms_ok");
        var menuId = await SeedBoundMenuAsync(
            tenant, "pms_ok_view", provisioningStatus: "NotApplicable", mode: "VIEW");

        var (status, body) = await GetAsync(SchemaUrl("pms_ok", menuId));

        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("pms_ok_view", root.GetProperty("designerId").GetString());
        Assert.Equal("VIEW", root.GetProperty("mode").GetString());
        Assert.Equal("Published", root.GetProperty("status").GetString());
        Assert.Equal(1, root.GetProperty("latestVersion").GetInt32());
        Assert.Equal("Stack", root.GetProperty("rootElement").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Schema_NotServableCases_AllReturnTheSame404()
    {
        var tenant = await ProvisionTenantAsync("pms_deny");
        var privateMenu = await SeedBoundMenuAsync(
            tenant, "pms_deny_private", isPublic: false, provisioningStatus: "NotApplicable", mode: "VIEW");
        var inactiveMenu = await SeedBoundMenuAsync(
            tenant, "pms_deny_inactive", isActive: false, provisioningStatus: "NotApplicable", mode: "VIEW");
        var unboundMenu = await SeedBoundMenuAsync(
            tenant, "pms_deny_unbound", bound: false, provisioningStatus: "NotApplicable", mode: "VIEW");
        var okMenu = await SeedBoundMenuAsync(
            tenant, "pms_deny_ok", provisioningStatus: "NotApplicable", mode: "VIEW");
        var otherTenant = await ProvisionTenantAsync("pms_deny_other");
        await ProvisionTenantAsync("pms_deny_suspended", status: "Suspended");

        await AssertNotServableAsync(SchemaUrl("pms_deny", privateMenu));
        await AssertNotServableAsync(SchemaUrl("pms_deny", inactiveMenu));
        await AssertNotServableAsync(SchemaUrl("pms_deny", unboundMenu));
        await AssertNotServableAsync(SchemaUrl("pms_deny", Guid.NewGuid()));
        await AssertNotServableAsync("/api/public/pms_deny/menus/not-a-guid/schema");
        await AssertNotServableAsync(SchemaUrl("pms_deny_other", okMenu));
        await AssertNotServableAsync(SchemaUrl("pms_missing", okMenu));
        await AssertNotServableAsync(SchemaUrl("pms_deny_suspended", okMenu));
        Assert.NotNull(otherTenant);

        var (status, _) = await GetAsync(SchemaUrl("pms_deny", okMenu));
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task Records_MenuFromAnotherTenant_Returns404()
    {
        var tenantA = await ProvisionTenantAsync("pmr_iso_a");
        await ProvisionTenantAsync("pmr_iso_b");
        var menuA = await SeedBoundMenuAsync(tenantA, "pmr_iso_items", liveTitles: ["secret"]);

        var (status, body) = await GetAsync(Url("pmr_iso_b", menuA));

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.DoesNotContain("secret", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Records_PageSizeIsClamped_AndPagingWorks()
    {
        var tenant = await ProvisionTenantAsync("pmr_page");
        var menuId = await SeedBoundMenuAsync(tenant, "pmr_page_items", liveTitles: ["a", "b", "c"]);

        var (_, huge) = await GetAsync(Url("pmr_page", menuId, "?pageSize=100000"));
        var (_, zeroPage) = await GetAsync(Url("pmr_page", menuId, "?page=0&pageSize=0"));
        var (_, paged) = await GetAsync(Url("pmr_page", menuId, "?page=2&pageSize=2"));

        using var hugeDoc = JsonDocument.Parse(huge);
        Assert.Equal(25, hugeDoc.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, hugeDoc.RootElement.GetProperty("data").GetArrayLength());

        using var zeroDoc = JsonDocument.Parse(zeroPage);
        Assert.Equal(1, zeroDoc.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(25, zeroDoc.RootElement.GetProperty("pageSize").GetInt32());

        using var pagedDoc = JsonDocument.Parse(paged);
        Assert.Equal(2, pagedDoc.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(2, pagedDoc.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(3, pagedDoc.RootElement.GetProperty("total").GetInt64());
        Assert.Equal(1, pagedDoc.RootElement.GetProperty("data").GetArrayLength());
    }
}
