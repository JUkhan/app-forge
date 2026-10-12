using AppForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppForge.Api.Infrastructure.Persistence.Migrations
{
    // Seeds the hidden `public-guest` system role (deterministic id ...04) into every schema
    // this migration set is replayed into (public and each tenant schema provisioned from now
    // on). Existing tenant schemas are NOT migrated automatically (startup fan-out is a known
    // gap), so PublicGuest.EnsureGuestUserAsync also inserts the role idempotently at runtime.
    // can_manage_datasets is false and the role never receives role_permissions rows: its read
    // access is computed from the published layouts of active public menus.
    [DbContext(typeof(AppForgeDbContext))]
    [Migration("20261013000000_SeedPublicGuestRole")]
    public partial class SeedPublicGuestRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(
                "INSERT INTO roles (id, name, description, is_system, can_manage_datasets, created_at) " +
                "VALUES ('00000000-0000-0000-0000-000000000004', 'public-guest', NULL, true, false, " +
                "TIMESTAMPTZ '2026-01-01 00:00:00+00') " +
                "ON CONFLICT DO NOTHING;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.Sql(
                "DELETE FROM roles WHERE id = '00000000-0000-0000-0000-000000000004';");
        }
    }
}
