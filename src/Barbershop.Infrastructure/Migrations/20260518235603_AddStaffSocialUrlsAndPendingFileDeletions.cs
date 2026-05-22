using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffSocialUrlsAndPendingFileDeletions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FacebookUrl",
                schema: "public",
                table: "staff_profiles",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InstagramUrl",
                schema: "public",
                table: "staff_profiles",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TikTokUrl",
                schema: "public",
                table: "staff_profiles",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "XUrl",
                schema: "public",
                table: "staff_profiles",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "YoutubeUrl",
                schema: "public",
                table: "staff_profiles",
                type: "character varying(2048)",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "pending_file_deletions",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_file_deletions", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_file_deletions",
                schema: "public");

            migrationBuilder.DropColumn(
                name: "FacebookUrl",
                schema: "public",
                table: "staff_profiles");

            migrationBuilder.DropColumn(
                name: "InstagramUrl",
                schema: "public",
                table: "staff_profiles");

            migrationBuilder.DropColumn(
                name: "TikTokUrl",
                schema: "public",
                table: "staff_profiles");

            migrationBuilder.DropColumn(
                name: "XUrl",
                schema: "public",
                table: "staff_profiles");

            migrationBuilder.DropColumn(
                name: "YoutubeUrl",
                schema: "public",
                table: "staff_profiles");
        }
    }
}
