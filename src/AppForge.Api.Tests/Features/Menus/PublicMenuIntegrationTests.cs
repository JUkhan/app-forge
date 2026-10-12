using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using AppForge.Api.Domain.Entities;
using AppForge.Api.Features.Tenancy;
using AppForge.Api.Infrastructure.Persistence;
using AppForge.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AppForge.Api.Tests.Features.Menus;

[SuppressMessage("Reliability", "CA2000",
    Justification = "WebApplicationFactory is disposed via DisposeAsync in IAsyncLifetime.")]
public sealed class PublicMenuIntegrationTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _postgres;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public PublicMenuIntegrationTests(PostgresFixture postgres) => _postgres = postgres;

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

    private async Task SeedMenusAsync(Tenant tenant, params Menu[] menus)
    {
        using var scope = _factory!.Services.CreateScope();
        var ctx = (TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>();
        ctx.Set(tenant.Id, tenant.SchemaName);
        var db = scope.ServiceProvider.GetRequiredService<AppForgeDbContext>();
        db.Menus.AddRange(menus);
        await db.SaveChangesAsync();
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string path)
    {
        using var response = await _client!.GetAsync(new Uri(path, UriKind.Relative));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static Menu NewMenu(string name, bool isPublic, bool isActive = true, Guid? parentId = null, int order = 0) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            IsPublic = isPublic,
            IsActive = isActive,
            ParentId = parentId,
            Order = order,
            CreatedAt = DateTimeOffset.UtcNow,
        };

    [Fact]
    public async Task PublicMenu_ListsOnlyActivePublicItems_WithoutAuth_AndNoSensitiveFields()
    {
        var tenant = await ProvisionTenantAsync("pubmenu_list");
        var hiddenParent = NewMenu("Hidden parent", isPublic: false);
        await SeedMenusAsync(
            tenant,
            NewMenu("Public A", isPublic: true, order: 1),
            NewMenu("Private", isPublic: false, order: 2),
            NewMenu("Public inactive", isPublic: true, isActive: false, order: 3),
            hiddenParent,
            NewMenu("Public child of private", isPublic: true, parentId: hiddenParent.Id, order: 4));

        var (status, body) = await GetAsync("/api/public/pubmenu_list/menus");

        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(body);
        var names = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        Assert.Equal(["Public A", "Public child of private"], names);
        Assert.DoesNotContain("allowedRoleIds", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("createdAt", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublicMenu_UnknownInvalidAndSuspendedTenants_Return404WithSameCode()
    {
        await ProvisionTenantAsync("pubmenu_suspended", status: "Suspended");

        foreach (var slug in new[] { "pubmenu_missing", "Bad-Slug", "pubmenu_suspended" })
        {
            var (status, body) = await GetAsync($"/api/public/{slug}/menus");
            Assert.Equal(HttpStatusCode.NotFound, status);
            Assert.Contains("TENANT_NOT_FOUND", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PublicMenu_PublicParentAndChild_NestsChildUnderParent_AndExposesRoutePath()
    {
        var tenant = await ProvisionTenantAsync("pubmenu_nesting");
        var parent = NewMenu("Parent", isPublic: true, order: 1);
        var child = NewMenu("Child", isPublic: true, parentId: parent.Id, order: 1);
        child.RoutePath = "/audit";
        var other = NewMenu("Other", isPublic: true, order: 2);
        await SeedMenusAsync(tenant, parent, child, other);

        var (status, body) = await GetAsync("/api/public/pubmenu_nesting/menus");

        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(body);
        var roots = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(["Parent", "Other"], roots.Select(r => r.GetProperty("name").GetString()).ToList());
        var kid = Assert.Single(roots[0].GetProperty("children").EnumerateArray().ToList());
        Assert.Equal("Child", kid.GetProperty("name").GetString());
        Assert.Equal("/audit", kid.GetProperty("routePath").GetString());
        Assert.Equal(parent.Id, kid.GetProperty("parentId").GetGuid());
    }

    [Fact]
    public async Task PublicMenu_DesignerBoundItem_ExposesDesignerId()
    {
        var tenant = await ProvisionTenantAsync("pubmenu_designer");
        using (var scope = _factory!.Services.CreateScope())
        {
            var ctx = (TenantContext)scope.ServiceProvider.GetRequiredService<ITenantContext>();
            ctx.Set(tenant.Id, tenant.SchemaName);
            var db = scope.ServiceProvider.GetRequiredService<AppForgeDbContext>();
            db.ComponentSchemas.Add(new ComponentSchema
            {
                DesignerId = "orders",
                DisplayName = "Orders",
                Mode = "VIEW",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            var menu = NewMenu("Orders", isPublic: true);
            menu.DesignerId = "orders";
            db.Menus.Add(menu);
            await db.SaveChangesAsync();
        }

        var (status, body) = await GetAsync("/api/public/pubmenu_designer/menus");

        Assert.Equal(HttpStatusCode.OK, status);
        using var doc = JsonDocument.Parse(body);
        var item = Assert.Single(doc.RootElement.EnumerateArray().ToList());
        Assert.Equal("orders", item.GetProperty("designerId").GetString());
    }

    [Fact]
    public async Task PublicMenu_TwoTenants_EachSlugReturnsOnlyItsOwnItems()
    {
        var tenantA = await ProvisionTenantAsync("pubmenu_iso_a");
        var tenantB = await ProvisionTenantAsync("pubmenu_iso_b");
        await SeedMenusAsync(tenantA, NewMenu("Only A", isPublic: true));
        await SeedMenusAsync(tenantB, NewMenu("Only B", isPublic: true));

        var (statusA, bodyA) = await GetAsync("/api/public/pubmenu_iso_a/menus");
        var (statusB, bodyB) = await GetAsync("/api/public/pubmenu_iso_b/menus");

        Assert.Equal(HttpStatusCode.OK, statusA);
        Assert.Equal(HttpStatusCode.OK, statusB);
        Assert.Contains("Only A", bodyA, StringComparison.Ordinal);
        Assert.DoesNotContain("Only B", bodyA, StringComparison.Ordinal);
        Assert.Contains("Only B", bodyB, StringComparison.Ordinal);
        Assert.DoesNotContain("Only A", bodyB, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PublicFlag_DoesNotOpenAuthenticatedEndpoints()
    {
        var (menus, _) = await GetAsync("/api/menus");
        var (data, _) = await GetAsync("/api/data/anything");
        Assert.Equal(HttpStatusCode.Unauthorized, menus);
        Assert.Equal(HttpStatusCode.Unauthorized, data);
    }
}
