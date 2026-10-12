using AppForge.Api.Features.Designer;
using AppForge.Api.Features.Menus.Dtos;
using AppForge.Api.Features.Tenancy;
using AppForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AppForge.Api.Features.Menus;

// Anonymous read of a tenant's active + public menu items. The tenant comes from the URL
// segment (its schema_name), never from a JWT — TenantContextMiddleware leaves the context
// unset for anonymous requests, so this handler resolves and sets it itself before touching
// the tenant's `menus` table. Unknown, malformed and non-Active tenants all yield the same 404.
internal static class PublicMenuEndpoints
{
    internal static RouteGroupBuilder MapPublicMenuEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/{tenant}/menus", GetPublicMenusHandler)
             .AllowAnonymous()
             .WithSummary("Anonymous list of a tenant's active public menu items (nested tree).")
             .Produces<IReadOnlyList<PublicMenuItem>>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<IResult> GetPublicMenusHandler(
        string tenant,
        AppForgeDbContext db,
        ITenantContext tenantContext,
        IMenuService menuService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(menuService);

        // Shape check before any DB lookup; also guarantees the value is safe as a search_path.
        if (!SafeIdentifier.TryCreate(tenant, out var schema, out _) || schema is null)
        {
            return NotFound();
        }

        var row = await db.Tenants
            .AsNoTracking()
            .Where(t => t.SchemaName == schema.Value)
            .Select(t => new { t.Id, t.SchemaName, t.Status })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (row is null || !string.Equals(row.Status, "Active", StringComparison.Ordinal))
        {
            return NotFound();
        }

        tenantContext.Set(row.Id, row.SchemaName);

        var tree = await menuService.GetPublicMenusAsync(ct).ConfigureAwait(false);
        return Results.Ok(tree);
    }

    private static IResult NotFound() => Results.Problem(
        detail: "The requested tenant was not found.",
        title: "Tenant not found",
        statusCode: StatusCodes.Status404NotFound,
        extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["code"] = "TENANT_NOT_FOUND",
            ["messageKey"] = "public.tenantNotFound",
        });
}
