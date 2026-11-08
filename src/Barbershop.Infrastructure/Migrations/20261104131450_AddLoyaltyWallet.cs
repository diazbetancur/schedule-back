using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLoyaltyWallet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LoyaltyDecidedAt",
                schema: "public",
                table: "customer_memberships",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LoyaltyDecision",
                schema: "public",
                table: "customer_memberships",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LoyaltyPointsSnapshot",
                schema: "public",
                table: "customer_memberships",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "loyalty_adjustments",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Points = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uuid", nullable: false),
                    PerformedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PerformedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_adjustments", x => x.Id);
                    table.CheckConstraint("ck_loyalty_adjustments_points_positive", "\"Points\" > 0");
                    table.ForeignKey(
                        name: "FK_loyalty_adjustments_customers_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "public",
                        principalTable: "customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loyalty_adjustments_users_PerformedByUserId",
                        column: x => x.PerformedByUserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "loyalty_debts",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Origin = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Amount = table.Column<int>(type: "integer", nullable: false),
                    OutstandingPoints = table.Column<int>(type: "integer", nullable: false),
                    OriginDebtId = table.Column<Guid>(type: "uuid", nullable: true),
                    OriginCustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IncurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_debts", x => x.Id);
                    table.CheckConstraint("ck_loyalty_debts_amount_positive", "\"Amount\" > 0");
                    table.CheckConstraint("ck_loyalty_debts_incurred_not_after_created", "\"IncurredAt\" <= \"CreatedAt\"");
                    table.CheckConstraint("ck_loyalty_debts_origin_shape", "(\"Origin\" = 'MergeTransfer' AND \"OriginDebtId\" IS NOT NULL AND \"OriginCustomerId\" IS NOT NULL) OR (\"Origin\" <> 'MergeTransfer' AND \"OriginDebtId\" IS NULL)");
                    table.CheckConstraint("ck_loyalty_debts_outstanding_range", "\"OutstandingPoints\" >= 0 AND \"OutstandingPoints\" <= \"Amount\"");
                    table.ForeignKey(
                        name: "FK_loyalty_debts_customers_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "public",
                        principalTable: "customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loyalty_debts_customers_OriginCustomerId",
                        column: x => x.OriginCustomerId,
                        principalSchema: "public",
                        principalTable: "customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loyalty_debts_loyalty_debts_OriginDebtId",
                        column: x => x.OriginDebtId,
                        principalSchema: "public",
                        principalTable: "loyalty_debts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "loyalty_lots",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    InitialPoints = table.Column<int>(type: "integer", nullable: false),
                    RemainingPoints = table.Column<int>(type: "integer", nullable: false),
                    AccruedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AccruedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ValidityDays = table.Column<int>(type: "integer", nullable: false),
                    ExpiresOn = table.Column<DateOnly>(type: "date", nullable: false),
                    OriginLotId = table.Column<Guid>(type: "uuid", nullable: true),
                    OriginCustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_lots", x => x.Id);
                    table.CheckConstraint("ck_loyalty_lots_expiry_after_accrual", "\"ExpiresOn\" >= \"AccruedOn\"");
                    table.CheckConstraint("ck_loyalty_lots_initial_positive", "\"InitialPoints\" > 0");
                    table.CheckConstraint("ck_loyalty_lots_origin_shape", "(\"OriginLotId\" IS NULL AND \"OriginCustomerId\" IS NULL) OR (\"OriginLotId\" IS NOT NULL AND \"OriginCustomerId\" IS NOT NULL)");
                    table.CheckConstraint("ck_loyalty_lots_remaining_range", "\"RemainingPoints\" >= 0 AND \"RemainingPoints\" <= \"InitialPoints\"");
                    table.ForeignKey(
                        name: "FK_loyalty_lots_customers_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "public",
                        principalTable: "customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loyalty_lots_customers_OriginCustomerId",
                        column: x => x.OriginCustomerId,
                        principalSchema: "public",
                        principalTable: "customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loyalty_lots_loyalty_lots_OriginLotId",
                        column: x => x.OriginLotId,
                        principalSchema: "public",
                        principalTable: "loyalty_lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "loyalty_movements",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Points = table.Column<int>(type: "integer", nullable: false),
                    LotId = table.Column<Guid>(type: "uuid", nullable: true),
                    DebtId = table.Column<Guid>(type: "uuid", nullable: true),
                    PerformedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AdjustmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    RelatedCustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PerformedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_movements", x => x.Id);
                    table.CheckConstraint("ck_loyalty_movements_points_positive", "\"Points\" > 0");
                    table.CheckConstraint("ck_loyalty_movements_target", "\"LotId\" IS NOT NULL OR \"DebtId\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_loyalty_movements_customers_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "public",
                        principalTable: "customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loyalty_movements_loyalty_adjustments_AdjustmentId",
                        column: x => x.AdjustmentId,
                        principalSchema: "public",
                        principalTable: "loyalty_adjustments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loyalty_movements_loyalty_debts_DebtId",
                        column: x => x.DebtId,
                        principalSchema: "public",
                        principalTable: "loyalty_debts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loyalty_movements_loyalty_lots_LotId",
                        column: x => x.LotId,
                        principalSchema: "public",
                        principalTable: "loyalty_lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loyalty_movements_users_PerformedByUserId",
                        column: x => x.PerformedByUserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_customer_memberships_loyalty_decision_shape",
                schema: "public",
                table: "customer_memberships",
                sql: "(\"LoyaltyDecision\" IS NULL AND \"LoyaltyDecidedAt\" IS NULL) OR (\"LoyaltyDecision\" IS NOT NULL AND \"LoyaltyDecidedAt\" IS NOT NULL AND (\"LoyaltyDecision\" <> 'Accrued' OR (\"LoyaltyPointsSnapshot\" IS NOT NULL AND \"LoyaltyPointsSnapshot\" > 0)))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_customer_memberships_loyalty_snapshot_range",
                schema: "public",
                table: "customer_memberships",
                sql: "\"LoyaltyPointsSnapshot\" IS NULL OR \"LoyaltyPointsSnapshot\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_adjustments_CustomerId_PerformedAt",
                schema: "public",
                table: "loyalty_adjustments",
                columns: new[] { "CustomerId", "PerformedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_adjustments_IdempotencyKey",
                schema: "public",
                table: "loyalty_adjustments",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_adjustments_PerformedByUserId",
                schema: "public",
                table: "loyalty_adjustments",
                column: "PerformedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_debts_CustomerId_IncurredAt",
                schema: "public",
                table: "loyalty_debts",
                columns: new[] { "CustomerId", "IncurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_debts_OriginCustomerId",
                schema: "public",
                table: "loyalty_debts",
                column: "OriginCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_debts_OriginDebtId",
                schema: "public",
                table: "loyalty_debts",
                column: "OriginDebtId",
                unique: true,
                filter: "\"OriginDebtId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_lots_CustomerId_ExpiresOn",
                schema: "public",
                table: "loyalty_lots",
                columns: new[] { "CustomerId", "ExpiresOn" });

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_lots_original_source",
                schema: "public",
                table: "loyalty_lots",
                columns: new[] { "SourceType", "SourceId" },
                unique: true,
                filter: "\"OriginLotId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_lots_OriginCustomerId",
                schema: "public",
                table: "loyalty_lots",
                column: "OriginCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_lots_OriginLotId",
                schema: "public",
                table: "loyalty_lots",
                column: "OriginLotId",
                unique: true,
                filter: "\"OriginLotId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_movements_AdjustmentId",
                schema: "public",
                table: "loyalty_movements",
                column: "AdjustmentId");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_movements_CustomerId_PerformedAt",
                schema: "public",
                table: "loyalty_movements",
                columns: new[] { "CustomerId", "PerformedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_movements_LotId",
                schema: "public",
                table: "loyalty_movements",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_movements_once_per_debt",
                schema: "public",
                table: "loyalty_movements",
                columns: new[] { "DebtId", "Type" },
                unique: true,
                filter: "\"Type\" IN ('DebtMergeTransferIn', 'DebtMergeTransferOut', 'DebtOpened')");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_movements_once_per_lot",
                schema: "public",
                table: "loyalty_movements",
                columns: new[] { "LotId", "Type" },
                unique: true,
                filter: "\"Type\" IN ('Accrual', 'MergeTransferIn', 'AdjustmentCredit', 'Expiration', 'MembershipAnnulmentWithdrawal', 'MergeTransferOut')");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_movements_PerformedByUserId",
                schema: "public",
                table: "loyalty_movements",
                column: "PerformedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_movements_settlement_once",
                schema: "public",
                table: "loyalty_movements",
                columns: new[] { "DebtId", "LotId" },
                unique: true,
                filter: "\"Type\" = 'DebtSettlement'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "loyalty_movements",
                schema: "public");

            migrationBuilder.DropTable(
                name: "loyalty_adjustments",
                schema: "public");

            migrationBuilder.DropTable(
                name: "loyalty_debts",
                schema: "public");

            migrationBuilder.DropTable(
                name: "loyalty_lots",
                schema: "public");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customer_memberships_loyalty_decision_shape",
                schema: "public",
                table: "customer_memberships");

            migrationBuilder.DropCheckConstraint(
                name: "ck_customer_memberships_loyalty_snapshot_range",
                schema: "public",
                table: "customer_memberships");

            migrationBuilder.DropColumn(
                name: "LoyaltyDecidedAt",
                schema: "public",
                table: "customer_memberships");

            migrationBuilder.DropColumn(
                name: "LoyaltyDecision",
                schema: "public",
                table: "customer_memberships");

            migrationBuilder.DropColumn(
                name: "LoyaltyPointsSnapshot",
                schema: "public",
                table: "customer_memberships");
        }
    }
}
