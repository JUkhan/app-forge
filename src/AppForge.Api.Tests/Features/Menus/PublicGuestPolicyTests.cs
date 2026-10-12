using AppForge.Api.Features.PublicGuest;

namespace AppForge.Api.Tests.Features.Menus;

// Pure unit tests: layout reference derivation and the guest route/resource policy.
public sealed class PublicGuestPolicyTests
{
    private static readonly Guid DatasetA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Extractor_FindsDesignersDatasetsAndFileKeys_IncludingNestedChildren()
    {
        const string json = """
        {
          "id": "root", "type": "Stack", "properties": {},
          "children": [
            { "id": "t", "type": "Tabs", "properties": {}, "children": [
                { "id": "r", "type": "Repeater", "properties": { "rowDesignerId": "child_one" },
                  "children": [
                    { "id": "i", "type": "Image", "properties": { "src": "assets/logo.png" }, "children": [] }
                  ] } ] },
            { "id": "d", "type": "Dropdown",
              "properties": { "optionsDesignerId": "lookup_two", "optionsDatasetId": "ds_by_name" }, "children": [] },
            { "id": "g", "type": "DatasetComponent",
              "properties": { "optionsDatasetId": "11111111-1111-1111-1111-111111111111" }, "children": [] },
            { "id": "x", "type": "DynamicDropdown",
              "properties": { "apiPath": "/api/data/api_designer/options?labelField=a&valueField=b" }, "children": [] },
            { "id": "e", "type": "Image", "properties": { "src": "https://cdn.example.com/a.png" }, "children": [] },
            { "id": "f", "type": "Image", "properties": { "src": "/rooted.png" }, "children": [] },
            { "id": "b", "type": "Image", "properties": { "src": "../escape.png" }, "children": [] },
            { "id": "bad", "type": "Dropdown", "properties": { "optionsDesignerId": "x; DROP TABLE users" }, "children": [] }
          ]
        }
        """;

        var refs = PublicLayoutRefExtractor.Extract(json);

        Assert.Equal(
            ["api_designer", "child_one", "lookup_two"],
            refs.DesignerIds.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal([DatasetA], refs.DatasetIds.ToArray());
        Assert.Equal(["ds_by_name"], refs.DatasetNames.ToArray());
        Assert.Equal(["assets/logo.png"], refs.FileKeys.ToArray());
    }

    [Fact]
    public void Extractor_IgnoresResourceKeysOutsideProperties_AndMalformedJson()
    {
        Assert.Empty(PublicLayoutRefExtractor.Extract("""{"type":"Stack","rowDesignerId":"nope","children":[]}""").DesignerIds);
        Assert.Empty(PublicLayoutRefExtractor.Extract("not json").DesignerIds);
        Assert.Empty(PublicLayoutRefExtractor.Extract(null).DesignerIds);
    }

    private static PublicAccessSet Access() => new(
        new HashSet<string>(["pub_designer"], StringComparer.OrdinalIgnoreCase),
        new HashSet<Guid> { DatasetA },
        new HashSet<string>(["ds_by_name"], StringComparer.Ordinal),
        new HashSet<string>(["assets/logo.png"], StringComparer.Ordinal));

    [Theory]
    [InlineData("GET", "/api/data/pub_designer", true)]
    [InlineData("GET", "/api/data/PUB_DESIGNER/options", true)]
    [InlineData("GET", "/api/data/pub_designer/3f2504e0-4f89-11d3-9a0c-0305e82c3301", true)]
    [InlineData("GET", "/api/data/pub_designer/tree", true)]
    [InlineData("POST", "/api/data/pub_designer/tree/search", true)]
    [InlineData("GET", "/api/data/other", false)]
    [InlineData("POST", "/api/data/pub_designer", false)]
    [InlineData("PUT", "/api/data/pub_designer/3f2504e0-4f89-11d3-9a0c-0305e82c3301", false)]
    [InlineData("DELETE", "/api/data/pub_designer/3f2504e0-4f89-11d3-9a0c-0305e82c3301", false)]
    [InlineData("GET", "/api/data/pub_designer/export", false)]
    [InlineData("GET", "/api/data/pub_designer/single", false)]
    [InlineData("GET", "/api/designers/pub_designer", true)]
    [InlineData("GET", "/api/designers/pub_designer/versions/2", true)]
    [InlineData("GET", "/api/designers/other/versions/2", false)]
    [InlineData("GET", "/api/designers", false)]
    [InlineData("PUT", "/api/designers/pub_designer/versions/1", false)]
    [InlineData("POST", "/api/datasets/11111111-1111-1111-1111-111111111111/rows", true)]
    [InlineData("POST", "/api/datasets/11111111-1111-1111-1111-111111111111/rows/export", true)]
    [InlineData("POST", "/api/datasets/11111111-1111-1111-1111-111111111111/chart", true)]
    [InlineData("GET", "/api/datasets/11111111-1111-1111-1111-111111111111/options", true)]
    [InlineData("POST", "/api/datasets/22222222-2222-2222-2222-222222222222/rows", false)]
    [InlineData("PUT", "/api/datasets/11111111-1111-1111-1111-111111111111", false)]
    [InlineData("DELETE", "/api/datasets/11111111-1111-1111-1111-111111111111", false)]
    [InlineData("POST", "/api/datasets/preview", false)]
    [InlineData("GET", "/api/datasets", false)]
    [InlineData("GET", "/api/datasets/by-name/ds_by_name/options", true)]
    [InlineData("GET", "/api/datasets/by-name/nope/options", false)]
    [InlineData("POST", "/api/public/acme/session", true)]
    [InlineData("GET", "/api/admin/users", false)]
    [InlineData("GET", "/api/users/active", false)]
    [InlineData("GET", "/api/menus", false)]
    [InlineData("POST", "/api/auth/logout", false)]
    [InlineData("POST", "/api/files/upload", false)]
    [InlineData("DELETE", "/api/files", false)]
    [InlineData("GET", "/", true)]
    public void RoutePolicy_AllowsOnlyTheExplicitAllowlist(string method, string path, bool expected)
    {
        Assert.Equal(expected, PublicGuestRoutePolicy.IsAllowed(method, path, null, Access()));
    }

    [Theory]
    [InlineData("assets/logo.png", true)]
    [InlineData("pub_designer/photo_9.png", true)]
    [InlineData("other_designer/photo_9.png", false)]
    [InlineData("assets/other.png", false)]
    [InlineData("pub_designer/../other/x.png", false)]
    [InlineData("", false)]
    public void RoutePolicy_PresignIsScopedToAllowlistedKeys(string key, bool expected)
    {
        Assert.Equal(
            expected,
            PublicGuestRoutePolicy.IsAllowed("GET", "/api/files/presign", string.IsNullOrEmpty(key) ? null : key, Access()));
    }
}
