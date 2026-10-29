using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMembershipConsumptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "membership_consumptions",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceNameSnapshot = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    ServiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    StaffProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "uuid", nullable: false),
                    RegisteredByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegisteredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_membership_consumptions", x => x.Id);
                    table.CheckConstraint("ck_membership_consumptions_quantity_range", "\"Quantity\" >= 1 AND \"Quantity\" <= 1000");
                    table.ForeignKey(
                        name: "FK_membership_consumptions_customer_memberships_CustomerMember~",
                        column: x => x.CustomerMembershipId,
                        principalSchema: "public",
                        principalTable: "customer_memberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membership_consumptions_services_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "public",
                        principalTable: "services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membership_consumptions_staff_profiles_StaffProfileId",
                        column: x => x.StaffProfileId,
                        principalSchema: "public",
                        principalTable: "staff_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "membership_consumption_reversals",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MembershipConsumptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PerformedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PerformedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_membership_consumption_reversals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_membership_consumption_reversals_membership_consumptions_Me~",
                        column: x => x.MembershipConsumptionId,
                        principalSchema: "public",
                        principalTable: "membership_consumptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "membership_movements",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    MembershipAllowanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    MembershipConsumptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    PerformedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_membership_movements", x => x.Id);
                    table.CheckConstraint("ck_membership_movements_quantity_range", "\"Quantity\" >= 1 AND \"Quantity\" <= 1000");
                    table.ForeignKey(
                        name: "FK_membership_movements_customer_memberships_CustomerMembershi~",
                        column: x => x.CustomerMembershipId,
                        principalSchema: "public",
                        principalTable: "customer_memberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membership_movements_membership_allowances_MembershipAllowa~",
                        column: x => x.MembershipAllowanceId,
                        principalSchema: "public",
                        principalTable: "membership_allowances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membership_movements_membership_consumptions_MembershipCons~",
                        column: x => x.MembershipConsumptionId,
                        principalSchema: "public",
                        principalTable: "membership_consumptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_membership_consumption_reversals_MembershipConsumptionId",
                schema: "public",
                table: "membership_consumption_reversals",
                column: "MembershipConsumptionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_membership_consumptions_CustomerMembershipId_ServiceDate",
                schema: "public",
                table: "membership_consumptions",
                columns: new[] { "CustomerMembershipId", "ServiceDate" });

            migrationBuilder.CreateIndex(
                name: "IX_membership_consumptions_IdempotencyKey",
                schema: "public",
                table: "membership_consumptions",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_membership_consumptions_ServiceId",
                schema: "public",
                table: "membership_consumptions",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_membership_consumptions_StaffProfileId",
                schema: "public",
                table: "membership_consumptions",
                column: "StaffProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_membership_movements_CustomerMembershipId",
                schema: "public",
                table: "membership_movements",
                column: "CustomerMembershipId");

            migrationBuilder.CreateIndex(
                name: "IX_membership_movements_MembershipAllowanceId",
                schema: "public",
                table: "membership_movements",
                column: "MembershipAllowanceId");

            migrationBuilder.CreateIndex(
                name: "IX_membership_movements_MembershipConsumptionId_MembershipAllo~",
                schema: "public",
                table: "membership_movements",
                columns: new[] { "MembershipConsumptionId", "MembershipAllowanceId", "Type" },
                unique: true);

            // Consumptions, movements and reversals are an append-only ledger: corrections are new rows, never edits.
            migrationBuilder.Sql("""
                CREATE FUNCTION public.membership_ledger_is_append_only() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'membership ledger rows (%) cannot be updated or deleted', TG_TABLE_NAME
                        USING ERRCODE = 'restrict_violation';
                END;
                $$;

                CREATE TRIGGER trg_membership_consumptions_append_only
                    BEFORE UPDATE OR DELETE ON public.membership_consumptions
                    FOR EACH ROW EXECUTE FUNCTION public.membership_ledger_is_append_only();
                CREATE TRIGGER trg_membership_movements_append_only
                    BEFORE UPDATE OR DELETE ON public.membership_movements
                    FOR EACH ROW EXECUTE FUNCTION public.membership_ledger_is_append_only();
                CREATE TRIGGER trg_membership_consumption_reversals_append_only
                    BEFORE UPDATE OR DELETE ON public.membership_consumption_reversals
                    FOR EACH ROW EXECUTE FUNCTION public.membership_ledger_is_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "membership_consumption_reversals",
                schema: "public");

            migrationBuilder.DropTable(
                name: "membership_movements",
                schema: "public");

            migrationBuilder.DropTable(
                name: "membership_consumptions",
                schema: "public");

            migrationBuilder.Sql("DROP FUNCTION public.membership_ledger_is_append_only();");
        }
    }
}
