using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMembershipGiftDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "membership_gift_deliveries",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MembershipGiftId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerMembershipId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeliveryNumber = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    DeliveredOn = table.Column<DateOnly>(type: "date", nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uuid", nullable: false),
                    RegisteredByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegisteredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_membership_gift_deliveries", x => x.Id);
                    table.CheckConstraint("ck_membership_gift_deliveries_number_positive", "\"DeliveryNumber\" >= 1");
                    table.CheckConstraint("ck_membership_gift_deliveries_quantity_range", "\"Quantity\" >= 1 AND \"Quantity\" <= 1000");
                    table.ForeignKey(
                        name: "FK_membership_gift_deliveries_customer_memberships_CustomerMem~",
                        column: x => x.CustomerMembershipId,
                        principalSchema: "public",
                        principalTable: "customer_memberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membership_gift_deliveries_membership_gifts_MembershipGiftId",
                        column: x => x.MembershipGiftId,
                        principalSchema: "public",
                        principalTable: "membership_gifts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "membership_gift_delivery_reversals",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MembershipGiftDeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PerformedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PerformedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_membership_gift_delivery_reversals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_membership_gift_delivery_reversals_membership_gift_deliveri~",
                        column: x => x.MembershipGiftDeliveryId,
                        principalSchema: "public",
                        principalTable: "membership_gift_deliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_membership_gift_deliveries_CustomerMembershipId",
                schema: "public",
                table: "membership_gift_deliveries",
                column: "CustomerMembershipId");

            migrationBuilder.CreateIndex(
                name: "IX_membership_gift_deliveries_IdempotencyKey",
                schema: "public",
                table: "membership_gift_deliveries",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_membership_gift_deliveries_MembershipGiftId_DeliveryNumber",
                schema: "public",
                table: "membership_gift_deliveries",
                columns: new[] { "MembershipGiftId", "DeliveryNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_membership_gift_delivery_reversals_MembershipGiftDeliveryId",
                schema: "public",
                table: "membership_gift_delivery_reversals",
                column: "MembershipGiftDeliveryId",
                unique: true);

            // Deliveries and their reversals are append-only, like the consumption ledger (same function).
            migrationBuilder.Sql("""
                CREATE TRIGGER trg_membership_gift_deliveries_append_only
                    BEFORE UPDATE OR DELETE ON public.membership_gift_deliveries
                    FOR EACH ROW EXECUTE FUNCTION public.membership_ledger_is_append_only();
                CREATE TRIGGER trg_membership_gift_delivery_reversals_append_only
                    BEFORE UPDATE OR DELETE ON public.membership_gift_delivery_reversals
                    FOR EACH ROW EXECUTE FUNCTION public.membership_ledger_is_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "membership_gift_delivery_reversals",
                schema: "public");

            migrationBuilder.DropTable(
                name: "membership_gift_deliveries",
                schema: "public");
        }
    }
}
