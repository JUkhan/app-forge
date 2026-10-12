using System.Text.Json;
using System.Text.RegularExpressions;
using AppForge.Api.Features.Designer;

namespace AppForge.Api.Features.PublicGuest;

// What one published layout (a designer version's root element) references.
internal sealed record LayoutRefs(
    IReadOnlySet<string> DesignerIds,
    IReadOnlySet<Guid> DatasetIds,
    IReadOnlySet<string> DatasetNames,
    IReadOnlySet<string> FileKeys);

// Pure derivation of resource references from a root-element JSON tree. Walks EVERY node
// (including Repeater/TreeView/Tabs children) and inspects each element's `properties`:
//   designers : rowDesignerId, optionsDesignerId, designerId (SafeIdentifier-valid strings),
//               plus any `/api/data/{id}` occurring in a string value (e.g. DynamicDropdown apiPath)
//   datasets  : optionsDatasetId / datasetId - a GUID (id) or a name (VIEW name, as stored by
//               Dataset-backed Dropdown/DatasetComponent/TreeView), plus `/api/datasets/{guid}`
//               and `/api/datasets/by-name/{name}` occurring in string values
//   file keys : the Image component's `src` when it is not an absolute/rooted URL
// Not covered: file keys stored only in record data (handled separately by the
// "{allowlisted designerId}/..." prefix rule in PublicAccessSet), and any future component that
// keeps a resource reference under a property name not listed above.
internal static class PublicLayoutRefExtractor
{
    private const int MaxDepth = 64;

    private static readonly HashSet<string> DesignerKeys =
        new(["rowDesignerId", "optionsDesignerId", "designerId"], StringComparer.Ordinal);

    private static readonly HashSet<string> DatasetKeys =
        new(["optionsDatasetId", "datasetId"], StringComparer.Ordinal);

    private static readonly Regex ApiDataRe =
        new(@"/api/data/([A-Za-z0-9_]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ApiDatasetGuidRe =
        new(@"/api/datasets/([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ApiDatasetNameRe =
        new(@"/api/datasets/by-name/([A-Za-z0-9_]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AbsoluteOrRootedRe =
        new(@"^([a-zA-Z][a-zA-Z0-9+.\-]*:|/|\\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static LayoutRefs Extract(string? rootElementJson)
    {
        var acc = new Accumulator();

        if (!string.IsNullOrWhiteSpace(rootElementJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(rootElementJson);
                Visit(doc.RootElement, inProperties: false, 0, acc);
            }
            catch (JsonException)
            {
                // Malformed layout contributes nothing.
            }
        }

        return new LayoutRefs(acc.Designers, acc.DatasetIds, acc.DatasetNames, acc.FileKeys);
    }

    private sealed class Accumulator
    {
        public HashSet<string> Designers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<Guid> DatasetIds { get; } = [];
        public HashSet<string> DatasetNames { get; } = new(StringComparer.Ordinal);
        public HashSet<string> FileKeys { get; } = new(StringComparer.Ordinal);
    }

    private static void Visit(JsonElement el, bool inProperties, int depth, Accumulator acc)
    {
        if (depth > MaxDepth) return;

        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        HandleString(prop.Name, prop.Value.GetString(), inProperties, acc);
                    }
                    else
                    {
                        var nextInProps = inProperties || string.Equals(prop.Name, "properties", StringComparison.Ordinal);
                        Visit(prop.Value, nextInProps, depth + 1, acc);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        HandleString(string.Empty, item.GetString(), inProperties, acc);
                    }
                    else
                    {
                        Visit(item, inProperties, depth + 1, acc);
                    }
                }
                break;
            default:
                break;
        }
    }

    private static void HandleString(string key, string? value, bool inProperties, Accumulator acc)
    {
        if (!inProperties || string.IsNullOrWhiteSpace(value)) return;
        var v = value.Trim();

        if (DesignerKeys.Contains(key) && SafeIdentifier.TryCreate(v, out _, out _))
        {
            acc.Designers.Add(v);
        }

        if (DatasetKeys.Contains(key))
        {
            if (Guid.TryParse(v, out var g)) acc.DatasetIds.Add(g);
            else if (SafeIdentifier.TryCreate(v, out _, out _)) acc.DatasetNames.Add(v);
        }

        if (string.Equals(key, "src", StringComparison.Ordinal)
            && v.Length <= 512
            && !v.Contains("..", StringComparison.Ordinal)
            && !AbsoluteOrRootedRe.IsMatch(v))
        {
            acc.FileKeys.Add(v);
        }

        if (v.Length > 2048 || !v.Contains("/api/", StringComparison.OrdinalIgnoreCase)) return;

        foreach (Match m in ApiDataRe.Matches(v)) acc.Designers.Add(m.Groups[1].Value);
        foreach (Match m in ApiDatasetGuidRe.Matches(v))
        {
            if (Guid.TryParse(m.Groups[1].Value, out var g)) acc.DatasetIds.Add(g);
        }
        foreach (Match m in ApiDatasetNameRe.Matches(v)) acc.DatasetNames.Add(m.Groups[1].Value);
    }
}
