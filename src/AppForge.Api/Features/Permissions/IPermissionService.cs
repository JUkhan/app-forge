namespace AppForge.Api.Features.Permissions;

internal interface IPermissionService
{
    Task<EffectivePermissions> GetEffectivePermissionsAsync(Guid userId, CancellationToken ct);
    Task<CrudFlags> GetCrudFlagsAsync(Guid userId, string resourceId, CancellationToken ct);
}
