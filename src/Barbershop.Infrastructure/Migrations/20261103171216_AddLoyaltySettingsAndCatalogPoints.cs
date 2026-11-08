using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLoyaltySettingsAndCatalogPoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LoyaltyPoints",
                schema: "public",
                table: "services",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LoyaltyPoints",
                schema: "public",
                table: "membership_plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "loyalty_points_changes",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousPoints = table.Column<int>(type: "integer", nullable: false),
                    NewPoints = table.Column<int>(type: "integer", nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_points_changes", x => x.Id);
                    table.CheckConstraint("ck_loyalty_points_changes_points_non_negative", "\"PreviousPoints\" >= 0 AND \"NewPoints\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "loyalty_settings",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    PointsValidityDays = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_settings", x => x.Id);
                    table.CheckConstraint("ck_loyalty_settings_singleton", "\"Id\" = 1");
                    table.CheckConstraint("ck_loyalty_settings_validity_range", "\"PointsValidityDays\" >= 1 AND \"PointsValidityDays\" <= 3650");
                });

            migrationBuilder.CreateTable(
                name: "loyalty_settings_changes",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PreviousIsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    NewIsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    PreviousPointsValidityDays = table.Column<int>(type: "integer", nullable: false),
                    NewPointsValidityDays = table.Column<int>(type: "integer", nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_settings_changes", x => x.Id);
                });

            migrationBuilder.InsertData(
                schema: "public",
                table: "loyalty_settings",
                columns: new[] { "Id", "CreatedAt", "IsEnabled", "PointsValidityDays", "UpdatedAt", "UpdatedByUserId" },
                values: new object[] { 1, new DateTime(2026, 11, 3, 0, 0, 0, 0, DateTimeKind.Utc), false, 365, null, null });

            migrationBuilder.AddCheckConstraint(
                name: "ck_services_loyalty_points_non_negative",
                schema: "public",
                table: "services",
                sql: "\"LoyaltyPoints\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_membership_plans_loyalty_points_non_negative",
                schema: "public",
                table: "membership_plans",
                sql: "\"LoyaltyPoints\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_points_changes_TargetType_TargetId_ChangedAt",
                schema: "public",
                table: "loyalty_points_changes",
                columns: new[] { "TargetType", "TargetId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_settings_changes_ChangedAt",
                schema: "public",
                table: "loyalty_settings_changes",
                column: "ChangedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "loyalty_points_changes",
                schema: "public");

            migrationBuilder.DropTable(
                name: "loyalty_settings",
                schema: "public");

            migrationBuilder.DropTable(
                name: "loyalty_settings_changes",
                schema: "public");

            migrationBuilder.DropCheckConstraint(
                name: "ck_services_loyalty_points_non_negative",
                schema: "public",
                table: "services");

            migrationBuilder.DropCheckConstraint(
                name: "ck_membership_plans_loyalty_points_non_negative",
                schema: "public",
                table: "membership_plans");

            migrationBuilder.DropColumn(
                name: "LoyaltyPoints",
                schema: "public",
                table: "services");

            migrationBuilder.DropColumn(
                name: "LoyaltyPoints",
                schema: "public",
                table: "membership_plans");
        }
    }
}
