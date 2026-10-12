using AppForge.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AppForge.Api.Infrastructure.Persistence.Migrations
{
    // Adds menus.is_public (default false) — items flagged public and active are listed on the
    // anonymous /{tenant}/public_menu page. Relative table name so the per-tenant replay lands
    // in each tenant's schema. No Designer partial: the up-to-date model is the snapshot.
    [DbContext(typeof(AppForgeDbContext))]
    [Migration("20261012000000_AddMenuIsPublic")]
    public partial class AddMenuIsPublic : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<bool>(
                name: "is_public",
                table: "menus",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropColumn(
                name: "is_public",
                table: "menus");
        }
    }
}
