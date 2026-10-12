namespace AppForge.Api.Features.Permissions;

// Story 4.7 — shared canonical role GUIDs. PermissionService keeps its own private
// copy to preserve the historic encapsulation; other features (MenuService, etc.)
// reference these. Tests use the same literal "00000000-0000-0000-0000-000000000001".
internal static class WellKnownRoles
{
    public static readonly Guid PlatformAdminId = new("00000000-0000-0000-0000-000000000001");

    // Hidden cross-tenant developer role, seeded by the SeedPlatformDevRole migration.
    // Every tenant-facing Users/Roles/Menus surface filters this id (and its holders) out;
    // it can never be assigned through the self-service role/menu assignment endpoints.
    public static readonly Guid PlatformDevId = new("00000000-0000-0000-0000-000000000003");

    public const string PlatformDevName = "platform-dev";

    // Hidden per-tenant anonymous-visitor role, seeded by the SeedPublicGuestRole migration and
    // lazily ensured for existing tenants. Its single hidden user backs the `/{tenant}/public`
    // guest session. Hidden from every Users/Roles/Menus surface exactly like platform-dev, and
    // never granted real role_permissions rows: PermissionService derives its read access from
    // the published layouts of active public menus (see PublicGuestAllowlist).
    public static readonly Guid PublicGuestId = new("00000000-0000-0000-0000-000000000004");

    public const string PublicGuestName = "public-guest";

    // Every role id that tenant-facing Users/Roles/Menu-role surfaces must treat as nonexistent.
    public static readonly Guid[] HiddenIds = [PlatformDevId, PublicGuestId];

    public static bool IsHiddenName(string normalizedLowerName) =>
        string.Equals(normalizedLowerName, PlatformDevName, StringComparison.Ordinal)
        || string.Equals(normalizedLowerName, PublicGuestName, StringComparison.Ordinal);
}
