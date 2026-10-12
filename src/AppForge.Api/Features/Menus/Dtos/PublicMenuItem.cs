using System.Text.Json;

namespace AppForge.Api.Features.Menus.Dtos;

// Anonymous /api/public/{tenant}/menus tree node. Deliberately carries no role ids,
// isActive, or audit fields — only what a visitor needs to render and link the item.
internal sealed record PublicMenuItem(
    Guid Id,
    string Name,
    int Order,
    JsonElement? Icon,
    Guid? ParentId,
    string? DesignerId,
    string? RoutePath,
    IReadOnlyList<PublicMenuItem> Children);
