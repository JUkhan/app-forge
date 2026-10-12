using System.Text.RegularExpressions;
using AppForge.Api.Features.Tenancy;

namespace AppForge.Api.Features.PublicGuest;

// THE single enforcement point for guest (public-session) tokens. Runs after authentication and
// tenant resolution, before authorization: a request carrying a `guest=true` token may only reach
// the explicit route allowlist below, and only for resources in the tenant's PublicAccessSet.
// Everything else (every write, /api/admin/*, /api/auth/*, /api/users, /api/menus, ...) is a 403
// with the normal FORBIDDEN envelope. Handlers/policies behind it are unchanged; they remain a
// second layer (RequirePermission("read") only passes for allowlisted designers, see
// PermissionService).
internal static class PublicGuestRoutePolicy
{
    private const RegexOptions Opts =
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private const string GuidRe = "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}";

    // Layout reads: the latest/Published schema of a designer (Repeater children, nested layouts).
    private static readonly Regex DesignerRead =
        new(@"^/api/designers/([^/]+)(/versions/\d+)?$", Opts);

    // Read-only data access for an allowlisted designer: list, options, single record, tree reads.
    private static readonly Regex DataRead =
        new($@"^/api/data/([^/]+)(/options|/tree|/tree/descendants|/tree/ancestors|/{GuidRe})?$", Opts);

    // The only POST under /api/data a guest may use: tree search is a read with a request body.
    private static readonly Regex DataTreeSearch =
        new(@"^/api/data/([^/]+)/tree/search$", Opts);

    private static readonly Regex DatasetRead =
        new($@"^/api/datasets/({GuidRe})(/columns|/options)?$", Opts);

    private static readonly Regex DatasetByNameRead =
        new(@"^/api/datasets/by-name/([^/]+)/(columns|options)$", Opts);

    // Read-only POSTs (filter/sort tree in the body): rows, export, chart.
    private static readonly Regex DatasetQueryPost =
        new($@"^/api/datasets/({GuidRe})/(rows|rows/export|chart)$", Opts);

    private static readonly Regex PublicApi = new(@"^/api/public(/|$)", Opts);

    // Returns true when a guest token may make this request. `presignKey` is the `key` query
    // value for GET /api/files/presign.
    public static bool IsAllowed(string method, string path, string? presignKey, PublicAccessSet access)
    {
        ArgumentNullException.ThrowIfNull(access);

        // Non-API paths (SPA shell, static assets) carry no guest-relevant capability.
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(path, "/api", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var p = path.Length > 1 ? path.TrimEnd('/') : path;
        var isGet = string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase);
        var isPost = string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase);

        if (PublicApi.IsMatch(p))
        {
            return true;
        }

        if (isGet)
        {
            if (DesignerRead.Match(p) is { Success: true } d)
                return access.AllowsDesigner(d.Groups[1].Value);

            if (DataRead.Match(p) is { Success: true } r)
                return access.AllowsDesigner(r.Groups[1].Value);

            if (DatasetRead.Match(p) is { Success: true } ds
                && System.Guid.TryParse(ds.Groups[1].Value, out var dsId))
                return access.AllowsDataset(dsId);

            if (DatasetByNameRead.Match(p) is { Success: true } bn)
                return access.AllowsDatasetName(bn.Groups[1].Value);

            if (string.Equals(p, "/api/files/presign", StringComparison.OrdinalIgnoreCase))
                return presignKey is not null && access.AllowsFileKey(presignKey);

            return false;
        }

        if (isPost)
        {
            if (DataTreeSearch.Match(p) is { Success: true } ts)
                return access.AllowsDesigner(ts.Groups[1].Value);

            if (DatasetQueryPost.Match(p) is { Success: true } q
                && System.Guid.TryParse(q.Groups[1].Value, out var qId))
                return access.AllowsDataset(qId);
        }

        return false;
    }
}

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Instantiated at runtime by app.UseMiddleware<PublicGuestAccessMiddleware>().")]
internal sealed class PublicGuestAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IPublicGuestAllowlist allowlist,
        ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(allowlist);
        ArgumentNullException.ThrowIfNull(tenantContext);

        if (!context.User.IsGuest())
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var path = context.Request.Path.Value ?? "/";

        // A guest token is only meaningful inside one resolved tenant.
        var allowed = false;
        if (tenantContext.TenantId is not null)
        {
            var presignKey = context.Request.Query["key"].ToString();
            var access = await allowlist.GetAsync(context.RequestAborted).ConfigureAwait(false);
            allowed = PublicGuestRoutePolicy.IsAllowed(
                context.Request.Method, path, string.IsNullOrEmpty(presignKey) ? null : presignKey, access);
        }

        if (allowed)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        await Results.Problem(
            detail: "Public guest sessions cannot access this resource.",
            title: "Permission denied",
            statusCode: StatusCodes.Status403Forbidden,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = "FORBIDDEN",
                ["messageKey"] = "errors.forbidden",
            }).ExecuteAsync(context).ConfigureAwait(false);
    }
}
