using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIncomeEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "income_entries",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceNameSnapshot = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    BasePriceSnapshot = table.Column<int>(type: "integer", nullable: false),
                    StaffProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<int>(type: "integer", nullable: false),
                    IsPromo = table.Column<bool>(type: "boolean", nullable: false),
                    OccurredOn = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_income_entries", x => x.Id);
                    table.CheckConstraint("ck_income_entries_amount_non_negative", "\"Amount\" >= 0");
                    table.CheckConstraint("ck_income_entries_base_price_non_negative", "\"BasePriceSnapshot\" >= 0");
                    table.ForeignKey(
                        name: "FK_income_entries_services_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "public",
                        principalTable: "services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_income_entries_staff_profiles_StaffProfileId",
                        column: x => x.StaffProfileId,
                        principalSchema: "public",
                        principalTable: "staff_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_income_entries_OccurredOn",
                schema: "public",
                table: "income_entries",
                column: "OccurredOn");

            migrationBuilder.CreateIndex(
                name: "IX_income_entries_ServiceId",
                schema: "public",
                table: "income_entries",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_income_entries_StaffProfileId",
                schema: "public",
                table: "income_entries",
                column: "StaffProfileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "income_entries",
                schema: "public");
        }
    }
}
