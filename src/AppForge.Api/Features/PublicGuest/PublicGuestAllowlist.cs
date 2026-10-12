using AppForge.Api.Features.Designer;
using AppForge.Api.Features.Tenancy;
using AppForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AppForge.Api.Features.PublicGuest;

// The resources a guest session may touch, derived from the PUBLISHED layouts of the tenant's
// active public menus (and, transitively, the designers/datasets/files those layouts reference).
internal sealed class PublicAccessSet(
    IReadOnlySet<string> designerIds,
    IReadOnlySet<Guid> datasetIds,
    IReadOnlySet<string> datasetNames,
    IReadOnlySet<string> fileKeys)
{
    public static PublicAccessSet Empty { get; } = new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<Guid>(),
        new HashSet<string>(StringComparer.Ordinal),
        new HashSet<string>(StringComparer.Ordinal));

    public IReadOnlySet<string> DesignerIds { get; } = designerIds;
    public IReadOnlySet<Guid> DatasetIds { get; } = datasetIds;
    public IReadOnlySet<string> DatasetNames { get; } = datasetNames;
    public IReadOnlySet<string> FileKeys { get; } = fileKeys;

    public bool AllowsDesigner(string designerId) => DesignerIds.Contains(designerId);

    public bool AllowsDataset(Guid datasetId) => DatasetIds.Contains(datasetId);

    public bool AllowsDatasetName(string name) => DatasetNames.Contains(name);

    // Exact key referenced by a published layout, or an object that lives under an allowlisted
    // designer's key prefix ("{designerId}/{fieldKey}_...": the upload layout), i.e. a file
    // attached to a record the guest can already read through that designer.
    public bool AllowsFileKey(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Contains("..", StringComparison.Ordinal)) return false;
        if (FileKeys.Contains(key)) return true;
        var slash = key.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 && DesignerIds.Contains(key[..slash]);
    }
}

internal interface IPublicGuestAllowlist
{
    Task<PublicAccessSet> GetAsync(CancellationToken ct);
}

// Computes the per-tenant PublicAccessSet and caches it briefly. Scoped: it reads the request's
// ITenantContext + AppForgeDbContext (so it must run after the tenant is resolved).
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1812",
    Justification = "Registered via DI.")]
internal sealed class PublicGuestAllowlist(
    AppForgeDbContext db,
    ITenantContext tenantContext,
    IMemoryCache cache) : IPublicGuestAllowlist
{
    // Staleness bound for a menu/layout change to reach guest sessions.
    internal static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private const int MaxDesigners = 200;

    private static string CacheKey(Guid tenantId) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"public-guest-allowlist:{tenantId:N}");

    // Drops the cached set for a tenant (call after menu/designer publication changes if wanted).
    public static void Invalidate(IMemoryCache cache, Guid tenantId)
    {
        ArgumentNullException.ThrowIfNull(cache);
        cache.Remove(CacheKey(tenantId));
    }

    public async Task<PublicAccessSet> GetAsync(CancellationToken ct)
    {
        if (tenantContext.TenantId is not { } tenantId)
        {
            return PublicAccessSet.Empty;
        }

        var key = CacheKey(tenantId);
        if (cache.TryGetValue<PublicAccessSet>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        var computed = await ComputeAsync(ct).ConfigureAwait(false);
        cache.Set(key, computed, CacheTtl);
        return computed;
    }

    private async Task<PublicAccessSet> ComputeAsync(CancellationToken ct)
    {
        var roots = await db.Menus
            .AsNoTracking()
            .Where(m => m.IsActive
                     && m.IsPublic
                     && m.DesignerId != null
                     && m.BoundVersion != null
                     && (m.ProvisioningStatus == "Success" || m.ProvisioningStatus == "NotApplicable"))
            .Select(m => new { DesignerId = m.DesignerId!, Version = m.BoundVersion!.Value })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var designers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var datasetIds = new HashSet<Guid>();
        var datasetNames = new HashSet<string>(StringComparer.Ordinal);
        var fileKeys = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // (designerId, explicit bound version or null = latest Published)
        var queue = new Queue<(string DesignerId, int? Version)>();
        foreach (var r in roots)
        {
            queue.Enqueue((r.DesignerId, r.Version));
        }

        while (queue.Count > 0 && visited.Count < MaxDesigners)
        {
            var (designerId, version) = queue.Dequeue();
            if (!SafeIdentifier.TryCreate(designerId, out _, out _) || !visited.Add(designerId))
            {
                continue;
            }

            var query = db.ComponentSchemaVersions
                .AsNoTracking()
                .Where(v => v.DesignerId == designerId && v.Status == "Published");
            query = version is { } pinned
                ? query.Where(v => v.Version == pinned)
                : query.OrderByDescending(v => v.Version);

            var row = await query
                .Select(v => new { v.RootElement, v.AuthFilterFieldKey, v.DatasetId })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            // Unpublished/unknown, or user-scoped (auth filter): never anonymously readable.
            if (row is null || !string.IsNullOrWhiteSpace(row.AuthFilterFieldKey))
            {
                continue;
            }

            designers.Add(designerId);
            if (row.DatasetId is { } boundDataset)
            {
                datasetIds.Add(boundDataset);
            }

            var refs = PublicLayoutRefExtractor.Extract(row.RootElement);
            datasetIds.UnionWith(refs.DatasetIds);
            datasetNames.UnionWith(refs.DatasetNames);
            fileKeys.UnionWith(refs.FileKeys);
            foreach (var child in refs.DesignerIds)
            {
                queue.Enqueue((child, null));
            }
        }

        // Resolve names <-> ids against the tenant's datasets so both addressing forms are
        // covered, and drop references to datasets that do not exist.
        if (datasetIds.Count > 0 || datasetNames.Count > 0)
        {
            var idList = datasetIds.ToList();
            var nameList = datasetNames.ToList();
            var known = await db.CustomDatasets
                .AsNoTracking()
                .Where(d => idList.Contains(d.Id) || nameList.Contains(d.DatasetName))
                .Select(d => new { d.Id, d.DatasetName })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            datasetIds = [.. known.Select(d => d.Id)];
            datasetNames = new HashSet<string>(known.Select(d => d.DatasetName), StringComparer.Ordinal);
        }

        return new PublicAccessSet(designers, datasetIds, datasetNames, fileKeys);
    }
}
