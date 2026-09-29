using AppForge.Api.Common;
using AppForge.Api.Features.Audit;
using AppForge.Api.Features.Audit.Dtos;

namespace AppForge.Api.Features.DynamicCrud;

// Story 6.8 — admin read-only endpoints for dynamic data tables.
// Mounted under /api/admin/data (RequirePlatformAdmin + "admin" rate limit
// inherited from the parent /api/admin group in AdminEndpoints).
// Only GET is mapped on /audit → ASP.NET returns 405 for DELETE/PUT/POST (AC-2).
internal static class DataAdminEndpoints
{
    internal static RouteGroupBuilder MapDataAdminEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        group.MapGet("/{designerId}/audit", AuditEndpoints.GetMutationAuditLogHandler)
             .WithSummary("Paginated CRUD mutation audit log for a dynamic table")
             .Produces<PagedResult<MutationAuditEntryDto>>(StatusCodes.Status200OK)
             .Produces(StatusCodes.Status404NotFound);

        return group;
    }
}
