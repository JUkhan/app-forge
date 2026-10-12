using AppForge.Api.Features.Datasets;
using AppForge.Api.Features.Datasets.Dtos;
using AppForge.Api.Features.Designer;
using AppForge.Api.Features.Designer.Dtos;
using AppForge.Api.Features.DynamicCrud;
using AppForge.Api.Features.Menus.Dtos;
using AppForge.Api.Features.SchemaRegistry;
using AppForge.Api.Features.Tenancy;
using AppForge.Api.Infrastructure.Persistence;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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

        group.MapGet("/{tenant}/menus/{menuId}/records", GetPublicMenuRecordsHandler)
             .AllowAnonymous()
             .WithSummary("Anonymous, read-only, paged records of a public designer-bound menu item.")
             .Produces<PublicMenuRecordsResult>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{tenant}/menus/{menuId}/schema", GetPublicMenuSchemaHandler)
             .AllowAnonymous()
             .WithSummary("Anonymous, read-only designer schema (Published only) of a public bound menu item.")
             .Produces<DesignerResponse>(StatusCodes.Status200OK)
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

        if (!await TryResolveTenantAsync(tenant, db, tenantContext, ct).ConfigureAwait(false))
        {
            return NotFound();
        }

        var tree = await menuService.GetPublicMenusAsync(ct).ConfigureAwait(false);
        return Results.Ok(tree);
    }

    // Shape check before any DB lookup (also guarantees the value is safe as a search_path),
    // then resolves an Active tenant by schema_name and sets the ambient tenant context.
    private static async Task<bool> TryResolveTenantAsync(
        string tenant, AppForgeDbContext db, ITenantContext tenantContext, CancellationToken ct)
    {
        if (!SafeIdentifier.TryCreate(tenant, out var schema, out _) || schema is null)
        {
            return false;
        }

        var row = await db.Tenants
            .AsNoTracking()
            .Where(t => t.SchemaName == schema.Value)
            .Select(t => new { t.Id, t.SchemaName, t.Status })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (row is null || !string.Equals(row.Status, "Active", StringComparison.Ordinal))
        {
            return false;
        }

        tenantContext.Set(row.Id, row.SchemaName);
        return true;
    }

    // Anonymous read of the Published schema (component layout) behind a public designer-bound
    // menu item. Designer id and version come only from the menu row; every "not servable"
    // case returns the same 404. Per-version auth-filter / dataset ids are not disclosed.
    private static async Task<IResult> GetPublicMenuSchemaHandler(
        string tenant,
        string menuId,
        AppForgeDbContext db,
        ITenantContext tenantContext,
        IDesignerService designerService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(designerService);

        if (!Guid.TryParse(menuId, out var menuGuid)
            || !await TryResolveTenantAsync(tenant, db, tenantContext, ct).ConfigureAwait(false))
        {
            return NotFound();
        }

        var menu = await db.Menus
            .AsNoTracking()
            .Where(m => m.Id == menuGuid
                     && m.IsActive
                     && m.IsPublic
                     && m.DesignerId != null
                     && m.BoundVersion != null
                     && (m.ProvisioningStatus == "Success" || m.ProvisioningStatus == "NotApplicable"))
            .Select(m => new { m.DesignerId, m.BoundVersion })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (menu is null)
        {
            return NotFound();
        }

        var schema = await designerService
            .GetVersionAsync(menu.DesignerId!, menu.BoundVersion!.Value, ct).ConfigureAwait(false);
        if (schema is null || !string.Equals(schema.Status, "Published", StringComparison.Ordinal))
        {
            return NotFound();
        }

        return Results.Ok(schema with { AuthFilterFieldKey = null, DatasetId = null, Versions = null });
    }

    // Anonymous read-only list of a public, designer-bound menu's records. The designer id and
    // bound version come ONLY from the menu row; no client filter/sort/designer input is read.
    // Every "not servable" case (unknown/suspended tenant, missing/non-public/inactive/unbound
    // menu, unprovisioned table, auth-filtered or dataset-bound designer) returns the same 404.
    private static async Task<IResult> GetPublicMenuRecordsHandler(
        string tenant,
        string menuId,
        AppForgeDbContext db,
        ITenantContext tenantContext,
        ISchemaRegistry schemaRegistry,
        DbConnectionFactory connectionFactory,
        IDatasetRowQueryService datasetRowQueryService,
        CancellationToken ct,
        int page = 1,
        int pageSize = 25)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(schemaRegistry);
        ArgumentNullException.ThrowIfNull(connectionFactory);
        ArgumentNullException.ThrowIfNull(datasetRowQueryService);

        if (!Guid.TryParse(menuId, out var menuGuid)
            || !await TryResolveTenantAsync(tenant, db, tenantContext, ct).ConfigureAwait(false))
        {
            return NotFound();
        }

        var menu = await db.Menus
            .AsNoTracking()
            .Where(m => m.Id == menuGuid
                     && m.IsActive
                     && m.IsPublic
                     && m.DesignerId != null
                     && (m.ProvisioningStatus == "Success" || m.ProvisioningStatus == "NotApplicable")
                     && m.BoundVersion != null)
            .Select(m => new { m.DesignerId, m.BoundVersion })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (menu is null
            || !SafeIdentifier.TryCreate(menu.DesignerId, out var safeId, out _)
            || safeId is null)
        {
            return NotFound();
        }

        var version = menu.BoundVersion!.Value;

        // User-scoped (auth filter) designers are never served anonymously. A dataset-bound
        // version (typical for VIEW-mode designers, which are "NotApplicable" for provisioning) is
        // read from its VIEW with no filter/sort/auth scope; table-backed versions fall through.
        var authFilterFieldKey = await DynamicDataEndpoints
            .GetAuthFilterFieldKeyAsync(db, safeId.Value, version, ct).ConfigureAwait(false);
        var datasetId = await DynamicDataEndpoints
            .GetDatasetIdAsync(db, safeId.Value, version, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(authFilterFieldKey))
        {
            return NotFound();
        }

        if (page < 1) page = 1;
        if (pageSize is < 1 or > MaxPublicPageSize) pageSize = DefaultPublicPageSize;

        if (datasetId is { } boundDataset)
        {
            var request = new DatasetRowsRequest(
                Filters: null, Sort: null, Columns: null,
                Page: page, PageSize: pageSize, AuthFilterColumn: null);
            var result = await datasetRowQueryService
                .GetRowsAsync(boundDataset, request, ct, authUserId: null).ConfigureAwait(false);
            if (result.Outcome != DatasetRowsOutcome.Success || result.Page is null)
            {
                return NotFound();
            }

            var dataPage = result.Page;
            var datasetRecords = dataPage.Data
                .Select(row => new DynamicRecord(
                    row.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal)))
                .ToList();
            return Results.Ok(new PublicMenuRecordsResult(
                dataPage.Columns, datasetRecords, dataPage.Total, dataPage.Page, dataPage.PageSize));
        }

        var entry = await DynamicDataEndpoints
            .GetOrPopulateEntryAsync(db, schemaRegistry, safeId.Value, version, ct).ConfigureAwait(false);

        // No client filters/sorts; soft-deleted rows are always excluded (non-admin semantics).
        var filters = DynamicDataEndpoints.ApplyDefaultSoftDeleteFilter(
            new Dictionary<string, string>(StringComparer.Ordinal), includeDeleted: false, isAdmin: false);

        // Derived (subquery) columns are intentionally not selected.
        var (selectSql, selectParams) = DynamicQueryBuilder.BuildSelectQuery(
            safeId, entry.Columns, [], filters, page, pageSize);
        var (countSql, countParams) = DynamicQueryBuilder.BuildCountQuery(safeId, entry.Columns, filters);

        // Only `id` plus user-defined columns may leave the server (no audit/system columns).
        var allowed = new HashSet<string>(entry.Columns.Select(c => c.ColumnName), StringComparer.Ordinal)
        {
            "id",
        };

        var conn = await connectionFactory.CreateOpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var total = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
                countSql, countParams, commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

            var rawRows = await conn.QueryAsync(new CommandDefinition(
                selectSql, selectParams, commandTimeout: 5, cancellationToken: ct)).ConfigureAwait(false);

            var records = rawRows
                .Cast<IDictionary<string, object>>()
                .Select(row => new DynamicRecord(
                    row.Where(kvp => allowed.Contains(kvp.Key))
                       .ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.Ordinal)))
                .ToList();

            return Results.Ok(new PublicMenuRecordsResult(
                entry.Columns.Select(c => c.ColumnName).ToList(), records, total, page, pageSize));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return NotFound();
        }
        finally
        {
            await conn.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal const int DefaultPublicPageSize = 25;
    internal const int MaxPublicPageSize = 100;

    internal sealed record PublicMenuRecordsResult(
        IReadOnlyList<string> Columns,
        IReadOnlyList<DynamicRecord> Data,
        long Total,
        int Page,
        int PageSize)
    {
        public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling((double)Total / PageSize);
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
