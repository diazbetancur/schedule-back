using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerMemberships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_memberships",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginalCustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    MembershipPlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    PlanDescription = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Price = table.Column<int>(type: "integer", nullable: false),
                    Mode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Terms = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    SpecificConditions = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    SoldOn = table.Column<DateOnly>(type: "date", nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    EndsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ActivatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ActivatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AnnulledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AnnulledByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AnnulmentReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IncomeEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_memberships", x => x.Id);
                    table.CheckConstraint("ck_customer_memberships_active_has_activation", "\"Status\" <> 'Active' OR \"ActivatedAt\" IS NOT NULL");
                    table.CheckConstraint("ck_customer_memberships_annulled_has_audit", "\"Status\" <> 'Annulled' OR (\"AnnulledAt\" IS NOT NULL AND \"AnnulledByUserId\" IS NOT NULL AND \"AnnulmentReason\" IS NOT NULL)");
                    table.CheckConstraint("ck_customer_memberships_date_range", "\"EndsOn\" >= \"StartsOn\"");
                    table.CheckConstraint("ck_customer_memberships_price_non_negative", "\"Price\" >= 0");
                    table.ForeignKey(
                        name: "FK_customer_memberships_customers_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "public",
                        principalTable: "customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_memberships_customers_OriginalCustomerId",
                        column: x => x.OriginalCustomerId,
                        principalSchema: "public",
                        principalTable: "customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_memberships_income_entries_IncomeEntryId",
                        column: x => x.IncomeEntryId,
                        principalSchema: "public",
                        principalTable: "income_entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_memberships_membership_plans_MembershipPlanId",
                        column: x => x.MembershipPlanId,
                        principalSchema: "public",
                        principalTable: "membership_plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "membership_events",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IncomeEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    RelatedCustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    PerformedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PerformedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_membership_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_membership_events_customer_memberships_CustomerMembershipId",
                        column: x => x.CustomerMembershipId,
                        principalSchema: "public",
                        principalTable: "customer_memberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "membership_gifts",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    MembershipPlanGiftRuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Condition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ServiceNameSnapshot = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    DescriptionSnapshot = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_membership_gifts", x => x.Id);
                    table.CheckConstraint("ck_membership_gifts_kind_shape", "(\"Kind\" = 'Service' AND \"ServiceId\" IS NOT NULL AND \"ServiceNameSnapshot\" IS NOT NULL AND \"DescriptionSnapshot\" IS NULL) OR (\"Kind\" = 'Physical' AND \"ServiceId\" IS NULL AND \"ServiceNameSnapshot\" IS NULL AND \"DescriptionSnapshot\" IS NOT NULL)");
                    table.CheckConstraint("ck_membership_gifts_quantity_range", "\"Quantity\" >= 1 AND \"Quantity\" <= 1000");
                    table.ForeignKey(
                        name: "FK_membership_gifts_customer_memberships_CustomerMembershipId",
                        column: x => x.CustomerMembershipId,
                        principalSchema: "public",
                        principalTable: "customer_memberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membership_gifts_membership_plan_gift_rules_MembershipPlanG~",
                        column: x => x.MembershipPlanGiftRuleId,
                        principalSchema: "public",
                        principalTable: "membership_plan_gift_rules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membership_gifts_services_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "public",
                        principalTable: "services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "membership_allowances",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    Origin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MembershipGiftId = table.Column<Guid>(type: "uuid", nullable: true),
                    GrantedQuantity = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_membership_allowances", x => x.Id);
                    table.CheckConstraint("ck_membership_allowances_granted_range", "\"GrantedQuantity\" >= 1 AND \"GrantedQuantity\" <= 1000");
                    table.CheckConstraint("ck_membership_allowances_origin_shape", "(\"Origin\" = 'Purchased' AND \"MembershipGiftId\" IS NULL) OR (\"Origin\" = 'Gift' AND \"MembershipGiftId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_membership_allowances_customer_memberships_CustomerMembersh~",
                        column: x => x.CustomerMembershipId,
                        principalSchema: "public",
                        principalTable: "customer_memberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membership_allowances_membership_gifts_MembershipGiftId",
                        column: x => x.MembershipGiftId,
                        principalSchema: "public",
                        principalTable: "membership_gifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "membership_allowance_services",
                schema: "public",
                columns: table => new
                {
                    MembershipAllowanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceNameSnapshot = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_membership_allowance_services", x => new { x.MembershipAllowanceId, x.ServiceId });
                    table.ForeignKey(
                        name: "FK_membership_allowance_services_membership_allowances_Members~",
                        column: x => x.MembershipAllowanceId,
                        principalSchema: "public",
                        principalTable: "membership_allowances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membership_allowance_services_services_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "public",
                        principalTable: "services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_memberships_CustomerId_MembershipPlanId",
                schema: "public",
                table: "customer_memberships",
                columns: new[] { "CustomerId", "MembershipPlanId" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_memberships_IncomeEntryId",
                schema: "public",
                table: "customer_memberships",
                column: "IncomeEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_customer_memberships_MembershipPlanId",
                schema: "public",
                table: "customer_memberships",
                column: "MembershipPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_customer_memberships_OriginalCustomerId",
                schema: "public",
                table: "customer_memberships",
                column: "OriginalCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_membership_allowance_services_ServiceId",
                schema: "public",
                table: "membership_allowance_services",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_membership_allowances_CustomerMembershipId",
                schema: "public",
                table: "membership_allowances",
                column: "CustomerMembershipId");

            migrationBuilder.CreateIndex(
                name: "IX_membership_allowances_MembershipGiftId",
                schema: "public",
                table: "membership_allowances",
                column: "MembershipGiftId",
                unique: true,
                filter: "\"MembershipGiftId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_membership_events_CustomerMembershipId_PerformedAt",
                schema: "public",
                table: "membership_events",
                columns: new[] { "CustomerMembershipId", "PerformedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_membership_events_IncomeEntryId",
                schema: "public",
                table: "membership_events",
                column: "IncomeEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_membership_gifts_CustomerMembershipId_MembershipPlanGiftRul~",
                schema: "public",
                table: "membership_gifts",
                columns: new[] { "CustomerMembershipId", "MembershipPlanGiftRuleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_membership_gifts_MembershipPlanGiftRuleId",
                schema: "public",
                table: "membership_gifts",
                column: "MembershipPlanGiftRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_membership_gifts_ServiceId",
                schema: "public",
                table: "membership_gifts",
                column: "ServiceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "membership_allowance_services",
                schema: "public");

            migrationBuilder.DropTable(
                name: "membership_events",
                schema: "public");

            migrationBuilder.DropTable(
                name: "membership_allowances",
                schema: "public");

            migrationBuilder.DropTable(
                name: "membership_gifts",
                schema: "public");

            migrationBuilder.DropTable(
                name: "customer_memberships",
                schema: "public");
        }
    }
}
