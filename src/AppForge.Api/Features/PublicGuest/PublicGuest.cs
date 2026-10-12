using System.Security.Claims;
using System.Security.Cryptography;
using AppForge.Api.Domain.Entities;
using AppForge.Api.Features.Auth;
using AppForge.Api.Features.Permissions;
using AppForge.Api.Features.Tenancy;
using AppForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AppForge.Api.Features.PublicGuest;

// Hidden per-tenant "guest" identity behind the anonymous /{tenant}/public pages. Widgets on a
// public layout call auth-only APIs (files/presign, datasets/rows, ...), so the public shell
// obtains a short-lived access-only JWT for this user (POST /api/public/{tenant}/session).
// The token is confined to an explicit route + resource allowlist by PublicGuestAccessMiddleware.
internal static class PublicGuestIdentity
{
    // JWT claim marking a guest session token (value "true").
    public const string ClaimType = "guest";

    // Lifetime of a guest access token. Short on purpose; the SPA re-fetches on 401.
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(15);

    public static bool IsGuest(this ClaimsPrincipal? principal) =>
        principal?.FindFirst(ClaimType)?.Value is { } v
        && string.Equals(v, "true", StringComparison.Ordinal);

    // Same derivation family as admin@/dev@ (...@{label}.tenant.local). Deliberately NOT written
    // to public.tenant_user_index, so login can never route this address to the tenant schema.
    public static string EmailFor(string schemaName) =>
        $"public-guest@{TenantEndpoints.ToEmailDomainLabel(schemaName)}.tenant.local";

    // Idempotently ensures the hidden role and the hidden guest user (+ its single user_role)
    // exist in the schema `db` is bound to, and returns the guest user's id. Safe to call
    // concurrently: a unique-email race is resolved by re-reading the winner's row.
    public static async Task<Guid> EnsureGuestUserAsync(
        AppForgeDbContext db, string schemaName, IPasswordHasher passwordHasher, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrEmpty(schemaName);
        ArgumentNullException.ThrowIfNull(passwordHasher);

        // Existing tenant schemas predate the SeedPublicGuestRole migration.
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO roles (id, name, description, is_system, can_manage_datasets, created_at) " +
            "VALUES ('00000000-0000-0000-0000-000000000004', 'public-guest', NULL, true, false, " +
            "TIMESTAMPTZ '2026-01-01 00:00:00+00') ON CONFLICT DO NOTHING;",
            ct).ConfigureAwait(false);

        var email = EmailFor(schemaName);

        var existing = await FindAsync(db, email, ct).ConfigureAwait(false);
        if (existing is { } id)
        {
            await EnsureRoleLinkAsync(db, id, ct).ConfigureAwait(false);
            return id;
        }

        // Unusable credential: the hash of a random secret nobody is ever given.
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        var user = new User
        {
            Email = email,
            DisplayName = "Public Guest",
            PasswordHash = passwordHasher.Hash(secret),
            IsActive = true,
            MfaEnabled = false,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        db.UserRoles.Add(new UserRole
        {
            User = user,
            RoleId = WellKnownRoles.PublicGuestId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return user.Id;
        }
        catch (DbUpdateException)
        {
            // Lost the creation race (uq_users_email) - adopt the winner's row.
            db.ChangeTracker.Clear();
            var winner = await FindAsync(db, email, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Could not create the public guest user.");
            await EnsureRoleLinkAsync(db, winner, ct).ConfigureAwait(false);
            return winner;
        }
    }

    private static async Task<Guid?> FindAsync(AppForgeDbContext db, string email, CancellationToken ct) =>
        await db.Users
            .AsNoTracking()
            .Where(u => u.Email == email)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    private static async Task EnsureRoleLinkAsync(AppForgeDbContext db, Guid userId, CancellationToken ct)
    {
        var linked = await db.UserRoles
            .AnyAsync(ur => ur.UserId == userId && ur.RoleId == WellKnownRoles.PublicGuestId, ct)
            .ConfigureAwait(false);
        if (linked) return;

        db.UserRoles.Add(new UserRole
        {
            UserId = userId,
            RoleId = WellKnownRoles.PublicGuestId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
        }
    }
}
