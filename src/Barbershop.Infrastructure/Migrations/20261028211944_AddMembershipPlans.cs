using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMembershipPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "membership_plans",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Price = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PoolQuantity = table.Column<int>(type: "integer", nullable: true),
                    DefaultDurationDays = table.Column<int>(type: "integer", nullable: false),
                    Terms = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    SpecificConditions = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_membership_plans", x => x.Id);
                    table.CheckConstraint("ck_membership_plans_duration_range", "\"DefaultDurationDays\" >= 1 AND \"DefaultDurationDays\" <= 3650");
                    table.CheckConstraint("ck_membership_plans_pool_quantity_by_mode", "(\"Mode\" = 'Pool' AND \"PoolQuantity\" IS NOT NULL AND \"PoolQuantity\" BETWEEN 1 AND 1000) OR (\"Mode\" = 'PerService' AND \"PoolQuantity\" IS NULL)");
                    table.CheckConstraint("ck_membership_plans_price_non_negative", "\"Price\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "membership_plan_gift_rules",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MembershipPlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ServiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Condition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_membership_plan_gift_rules", x => x.Id);
                    table.CheckConstraint("ck_membership_plan_gift_rules_kind_shape", "(\"Kind\" = 'Service' AND \"ServiceId\" IS NOT NULL AND \"Description\" IS NULL) OR (\"Kind\" = 'Physical' AND \"ServiceId\" IS NULL AND \"Description\" IS NOT NULL)");
                    table.CheckConstraint("ck_membership_plan_gift_rules_quantity_range", "\"Quantity\" >= 1 AND \"Quantity\" <= 1000");
                    table.ForeignKey(
                        name: "FK_membership_plan_gift_rules_membership_plans_MembershipPlanId",
                        column: x => x.MembershipPlanId,
                        principalSchema: "public",
                        principalTable: "membership_plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membership_plan_gift_rules_services_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "public",
                        principalTable: "services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "membership_plan_services",
                schema: "public",
                columns: table => new
                {
                    MembershipPlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_membership_plan_services", x => new { x.MembershipPlanId, x.ServiceId });
                    table.CheckConstraint("ck_membership_plan_services_quantity_range", "\"Quantity\" IS NULL OR (\"Quantity\" >= 1 AND \"Quantity\" <= 1000)");
                    table.ForeignKey(
                        name: "FK_membership_plan_services_membership_plans_MembershipPlanId",
                        column: x => x.MembershipPlanId,
                        principalSchema: "public",
                        principalTable: "membership_plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_membership_plan_services_services_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "public",
                        principalTable: "services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_membership_plan_gift_rules_MembershipPlanId",
                schema: "public",
                table: "membership_plan_gift_rules",
                column: "MembershipPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_membership_plan_gift_rules_ServiceId",
                schema: "public",
                table: "membership_plan_gift_rules",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_membership_plan_services_ServiceId",
                schema: "public",
                table: "membership_plan_services",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_membership_plans_Name",
                schema: "public",
                table: "membership_plans",
                column: "Name",
                unique: true,
                filter: "\"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "membership_plan_gift_rules",
                schema: "public");

            migrationBuilder.DropTable(
                name: "membership_plan_services",
                schema: "public");

            migrationBuilder.DropTable(
                name: "membership_plans",
                schema: "public");
        }
    }
}
