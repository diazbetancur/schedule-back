using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLoyaltyRedemptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_service_records_operation_shape",
                schema: "public",
                table: "service_records");

            migrationBuilder.DropIndex(
                name: "IX_loyalty_movements_once_per_lot",
                schema: "public",
                table: "loyalty_movements");

            migrationBuilder.AddColumn<int>(
                name: "RewardBasePriceSnapshot",
                schema: "public",
                table: "service_records",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RewardBusinessPercentageSnapshot",
                schema: "public",
                table: "service_records",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RewardProfessionalCommission",
                schema: "public",
                table: "service_records",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NewRedemptionApprovalDays",
                schema: "public",
                table: "loyalty_settings_changes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NewRedemptionDeliveryDays",
                schema: "public",
                table: "loyalty_settings_changes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PreviousRedemptionApprovalDays",
                schema: "public",
                table: "loyalty_settings_changes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PreviousRedemptionDeliveryDays",
                schema: "public",
                table: "loyalty_settings_changes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RedemptionApprovalDays",
                schema: "public",
                table: "loyalty_settings",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "RedemptionDeliveryDays",
                schema: "public",
                table: "loyalty_settings",
                type: "integer",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<Guid>(
                name: "RedemptionId",
                schema: "public",
                table: "loyalty_movements",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReservedPoints",
                schema: "public",
                table: "loyalty_lots",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "loyalty_rewards",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ServiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    PointsCost = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_rewards", x => x.Id);
                    table.CheckConstraint("ck_loyalty_rewards_cost_range", "\"PointsCost\" >= 1 AND \"PointsCost\" <= 100000");
                    table.CheckConstraint("ck_loyalty_rewards_kind_shape", "(\"Kind\" = 'Service' AND \"ServiceId\" IS NOT NULL) OR (\"Kind\" = 'Other' AND \"ServiceId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_loyalty_rewards_services_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "public",
                        principalTable: "services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loyalty_rewards_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loyalty_rewards_users_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "loyalty_redemptions",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    RewardId = table.Column<Guid>(type: "uuid", nullable: false),
                    RewardNameSnapshot = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    RewardKindSnapshot = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ServiceIdSnapshot = table.Column<Guid>(type: "uuid", nullable: true),
                    ServiceNameSnapshot = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    PointsCost = table.Column<int>(type: "integer", nullable: false),
                    Channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    InPersonVerified = table.Column<bool>(type: "boolean", nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RequestedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    ApprovalDays = table.Column<int>(type: "integer", nullable: false),
                    DeliveryDays = table.Column<int>(type: "integer", nullable: false),
                    ApproveBy = table.Column<DateOnly>(type: "date", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    DeliverBy = table.Column<DateOnly>(type: "date", nullable: true),
                    DeliveredByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ServiceRecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    RejectedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RejectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CancelledByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CancelledAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CancelledByCustomer = table.Column<bool>(type: "boolean", nullable: false),
                    ExpiredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_redemptions", x => x.Id);
                    table.CheckConstraint("ck_loyalty_redemptions_cost_positive", "\"PointsCost\" > 0");
                    table.CheckConstraint("ck_loyalty_redemptions_counter_verified", "\"Channel\" <> 'Counter' OR \"InPersonVerified\" = true");
                    table.CheckConstraint("ck_loyalty_redemptions_days_range", "\"ApprovalDays\" >= 1 AND \"ApprovalDays\" <= 365 AND \"DeliveryDays\" >= 1 AND \"DeliveryDays\" <= 365");
                    table.CheckConstraint("ck_loyalty_redemptions_kind_shape", "(\"RewardKindSnapshot\" = 'Service' AND \"ServiceIdSnapshot\" IS NOT NULL AND \"ServiceNameSnapshot\" IS NOT NULL) OR (\"RewardKindSnapshot\" = 'Other' AND \"ServiceIdSnapshot\" IS NULL AND \"ServiceNameSnapshot\" IS NULL)");
                    table.CheckConstraint("ck_loyalty_redemptions_service_record_shape", "\"ServiceRecordId\" IS NULL OR (\"Status\" = 'Delivered' AND \"RewardKindSnapshot\" = 'Service')");
                    table.CheckConstraint("ck_loyalty_redemptions_status_shape", "(\"Status\" = 'Requested' AND \"ApprovedAt\" IS NULL AND \"DeliveredAt\" IS NULL AND \"RejectedAt\" IS NULL AND \"CancelledAt\" IS NULL AND \"ExpiredAt\" IS NULL) OR (\"Status\" = 'Approved' AND \"ApprovedAt\" IS NOT NULL AND \"ApprovedByUserId\" IS NOT NULL AND \"ApprovedOn\" IS NOT NULL AND \"DeliverBy\" IS NOT NULL AND \"DeliveredAt\" IS NULL AND \"RejectedAt\" IS NULL AND \"CancelledAt\" IS NULL AND \"ExpiredAt\" IS NULL) OR (\"Status\" = 'Delivered' AND \"ApprovedAt\" IS NOT NULL AND \"DeliveredAt\" IS NOT NULL AND \"DeliveredByUserId\" IS NOT NULL AND \"RejectedAt\" IS NULL AND \"CancelledAt\" IS NULL AND \"ExpiredAt\" IS NULL AND ((\"RewardKindSnapshot\" = 'Service' AND \"ServiceRecordId\" IS NOT NULL) OR (\"RewardKindSnapshot\" = 'Other' AND \"ServiceRecordId\" IS NULL))) OR (\"Status\" = 'Rejected' AND \"RejectedAt\" IS NOT NULL AND \"RejectedByUserId\" IS NOT NULL AND \"RejectionReason\" IS NOT NULL AND \"ApprovedAt\" IS NULL AND \"DeliveredAt\" IS NULL AND \"CancelledAt\" IS NULL AND \"ExpiredAt\" IS NULL) OR (\"Status\" = 'Cancelled' AND \"CancelledAt\" IS NOT NULL AND \"CancelledByUserId\" IS NOT NULL AND \"DeliveredAt\" IS NULL AND \"RejectedAt\" IS NULL AND \"ExpiredAt\" IS NULL) OR (\"Status\" = 'Expired' AND \"ExpiredAt\" IS NOT NULL AND \"DeliveredAt\" IS NULL AND \"RejectedAt\" IS NULL AND \"CancelledAt\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_loyalty_redemptions_customers_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "public",
                        principalTable: "customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loyalty_redemptions_loyalty_rewards_RewardId",
                        column: x => x.RewardId,
                        principalSchema: "public",
                        principalTable: "loyalty_rewards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loyalty_redemptions_users_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loyalty_redemptions_users_CancelledByUserId",
                        column: x => x.CancelledByUserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loyalty_redemptions_users_DeliveredByUserId",
                        column: x => x.DeliveredByUserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loyalty_redemptions_users_RejectedByUserId",
                        column: x => x.RejectedByUserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loyalty_redemptions_users_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "loyalty_reward_changes",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RewardId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangeType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PreviousName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    PreviousDescription = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PreviousKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    PreviousServiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    PreviousPointsCost = table.Column<int>(type: "integer", nullable: true),
                    PreviousIsActive = table.Column<bool>(type: "boolean", nullable: true),
                    NewName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    NewDescription = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    NewKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    NewServiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    NewPointsCost = table.Column<int>(type: "integer", nullable: false),
                    NewIsActive = table.Column<bool>(type: "boolean", nullable: false),
                    ChangedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_reward_changes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_loyalty_reward_changes_loyalty_rewards_RewardId",
                        column: x => x.RewardId,
                        principalSchema: "public",
                        principalTable: "loyalty_rewards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loyalty_reward_changes_users_ChangedByUserId",
                        column: x => x.ChangedByUserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "loyalty_redemption_allocations",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RedemptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    LotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Points = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loyalty_redemption_allocations", x => x.Id);
                    table.CheckConstraint("ck_loyalty_redemption_allocations_points_positive", "\"Points\" > 0");
                    table.ForeignKey(
                        name: "FK_loyalty_redemption_allocations_loyalty_lots_LotId",
                        column: x => x.LotId,
                        principalSchema: "public",
                        principalTable: "loyalty_lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_loyalty_redemption_allocations_loyalty_redemptions_Redempti~",
                        column: x => x.RedemptionId,
                        principalSchema: "public",
                        principalTable: "loyalty_redemptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.UpdateData(
                schema: "public",
                table: "loyalty_settings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "RedemptionApprovalDays", "RedemptionDeliveryDays" },
                values: new object[] { 30, 30 });

            migrationBuilder.CreateIndex(
                name: "IX_service_records_LoyaltyRedemptionId",
                schema: "public",
                table: "service_records",
                column: "LoyaltyRedemptionId",
                unique: true,
                filter: "\"LoyaltyRedemptionId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_records_operation_shape",
                schema: "public",
                table: "service_records",
                sql: "\"OperationType\" IS NULL OR (\"OperationType\" = 'Paid' AND \"ChargedAmount\" IS NOT NULL AND \"ChargedAmount\" > 0 AND \"IncomeEntryId\" IS NOT NULL AND \"CustomerMembershipId\" IS NULL AND \"MembershipConsumptionId\" IS NULL) OR (\"OperationType\" = 'MembershipConsumption' AND \"ChargedAmount\" IS NULL AND \"IncomeEntryId\" IS NULL AND \"ApprovedCustomerId\" IS NOT NULL AND \"CustomerMembershipId\" IS NOT NULL AND \"MembershipConsumptionId\" IS NOT NULL) OR (\"OperationType\" = 'Courtesy' AND \"ChargedAmount\" IS NULL AND \"IncomeEntryId\" IS NULL AND \"CustomerMembershipId\" IS NULL AND \"MembershipConsumptionId\" IS NULL) OR (\"OperationType\" = 'LoyaltyReward' AND \"ChargedAmount\" IS NULL AND \"IncomeEntryId\" IS NULL AND \"ApprovedCustomerId\" IS NOT NULL AND \"CustomerMembershipId\" IS NULL AND \"MembershipConsumptionId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_records_reward_shape",
                schema: "public",
                table: "service_records",
                sql: "(\"OperationType\" IS NOT NULL AND \"OperationType\" = 'LoyaltyReward' AND \"LoyaltyRedemptionId\" IS NOT NULL AND \"RewardBasePriceSnapshot\" IS NOT NULL AND \"RewardBasePriceSnapshot\" >= 0 AND \"RewardBusinessPercentageSnapshot\" IS NOT NULL AND \"RewardBusinessPercentageSnapshot\" >= 0 AND \"RewardBusinessPercentageSnapshot\" <= 100 AND \"RewardProfessionalCommission\" IS NOT NULL AND \"RewardProfessionalCommission\" >= 0) OR ((\"OperationType\" IS NULL OR \"OperationType\" <> 'LoyaltyReward') AND \"LoyaltyRedemptionId\" IS NULL AND \"RewardBasePriceSnapshot\" IS NULL AND \"RewardBusinessPercentageSnapshot\" IS NULL AND \"RewardProfessionalCommission\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loyalty_settings_redemption_days_range",
                schema: "public",
                table: "loyalty_settings",
                sql: "\"RedemptionApprovalDays\" >= 1 AND \"RedemptionApprovalDays\" <= 365 AND \"RedemptionDeliveryDays\" >= 1 AND \"RedemptionDeliveryDays\" <= 365");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_movements_once_per_lot",
                schema: "public",
                table: "loyalty_movements",
                columns: new[] { "LotId", "Type" },
                unique: true,
                filter: "\"Type\" IN ('Accrual', 'MergeTransferIn', 'AdjustmentCredit', 'Expiration', 'MembershipAnnulmentWithdrawal', 'MergeTransferOut') AND \"RedemptionId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_movements_once_per_lot_and_redemption",
                schema: "public",
                table: "loyalty_movements",
                columns: new[] { "LotId", "RedemptionId", "Type" },
                unique: true,
                filter: "\"RedemptionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_movements_RedemptionId",
                schema: "public",
                table: "loyalty_movements",
                column: "RedemptionId");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loyalty_movements_redemption_shape",
                schema: "public",
                table: "loyalty_movements",
                sql: "\"Type\" NOT IN ('RedemptionHold', 'RedemptionRelease', 'RedemptionConsumed') OR (\"RedemptionId\" IS NOT NULL AND \"LotId\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loyalty_lots_reserved_range",
                schema: "public",
                table: "loyalty_lots",
                sql: "\"ReservedPoints\" >= 0 AND \"RemainingPoints\" + \"ReservedPoints\" <= \"InitialPoints\"");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_redemption_allocations_LotId",
                schema: "public",
                table: "loyalty_redemption_allocations",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_redemption_allocations_RedemptionId_LotId",
                schema: "public",
                table: "loyalty_redemption_allocations",
                columns: new[] { "RedemptionId", "LotId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_redemptions_ApprovedByUserId",
                schema: "public",
                table: "loyalty_redemptions",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_redemptions_CancelledByUserId",
                schema: "public",
                table: "loyalty_redemptions",
                column: "CancelledByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_redemptions_CustomerId_Status",
                schema: "public",
                table: "loyalty_redemptions",
                columns: new[] { "CustomerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_redemptions_DeliveredByUserId",
                schema: "public",
                table: "loyalty_redemptions",
                column: "DeliveredByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_redemptions_RejectedByUserId",
                schema: "public",
                table: "loyalty_redemptions",
                column: "RejectedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_redemptions_RequestedByUserId_IdempotencyKey",
                schema: "public",
                table: "loyalty_redemptions",
                columns: new[] { "RequestedByUserId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_redemptions_RewardId",
                schema: "public",
                table: "loyalty_redemptions",
                column: "RewardId");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_redemptions_ServiceRecordId",
                schema: "public",
                table: "loyalty_redemptions",
                column: "ServiceRecordId",
                unique: true,
                filter: "\"ServiceRecordId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_redemptions_Status_RequestedAt",
                schema: "public",
                table: "loyalty_redemptions",
                columns: new[] { "Status", "RequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_reward_changes_ChangedByUserId",
                schema: "public",
                table: "loyalty_reward_changes",
                column: "ChangedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_reward_changes_RewardId_ChangedAt",
                schema: "public",
                table: "loyalty_reward_changes",
                columns: new[] { "RewardId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_rewards_CreatedByUserId",
                schema: "public",
                table: "loyalty_rewards",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_rewards_IsActive_Name",
                schema: "public",
                table: "loyalty_rewards",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_rewards_ServiceId",
                schema: "public",
                table: "loyalty_rewards",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_rewards_UpdatedByUserId",
                schema: "public",
                table: "loyalty_rewards",
                column: "UpdatedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_loyalty_movements_loyalty_redemptions_RedemptionId",
                schema: "public",
                table: "loyalty_movements",
                column: "RedemptionId",
                principalSchema: "public",
                principalTable: "loyalty_redemptions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_service_records_loyalty_redemptions_LoyaltyRedemptionId",
                schema: "public",
                table: "service_records",
                column: "LoyaltyRedemptionId",
                principalSchema: "public",
                principalTable: "loyalty_redemptions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_loyalty_movements_loyalty_redemptions_RedemptionId",
                schema: "public",
                table: "loyalty_movements");

            migrationBuilder.DropForeignKey(
                name: "FK_service_records_loyalty_redemptions_LoyaltyRedemptionId",
                schema: "public",
                table: "service_records");

            migrationBuilder.DropTable(
                name: "loyalty_redemption_allocations",
                schema: "public");

            migrationBuilder.DropTable(
                name: "loyalty_reward_changes",
                schema: "public");

            migrationBuilder.DropTable(
                name: "loyalty_redemptions",
                schema: "public");

            migrationBuilder.DropTable(
                name: "loyalty_rewards",
                schema: "public");

            migrationBuilder.DropIndex(
                name: "IX_service_records_LoyaltyRedemptionId",
                schema: "public",
                table: "service_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_records_operation_shape",
                schema: "public",
                table: "service_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_records_reward_shape",
                schema: "public",
                table: "service_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loyalty_settings_redemption_days_range",
                schema: "public",
                table: "loyalty_settings");

            migrationBuilder.DropIndex(
                name: "IX_loyalty_movements_once_per_lot",
                schema: "public",
                table: "loyalty_movements");

            migrationBuilder.DropIndex(
                name: "IX_loyalty_movements_once_per_lot_and_redemption",
                schema: "public",
                table: "loyalty_movements");

            migrationBuilder.DropIndex(
                name: "IX_loyalty_movements_RedemptionId",
                schema: "public",
                table: "loyalty_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loyalty_movements_redemption_shape",
                schema: "public",
                table: "loyalty_movements");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loyalty_lots_reserved_range",
                schema: "public",
                table: "loyalty_lots");

            migrationBuilder.DropColumn(
                name: "RewardBasePriceSnapshot",
                schema: "public",
                table: "service_records");

            migrationBuilder.DropColumn(
                name: "RewardBusinessPercentageSnapshot",
                schema: "public",
                table: "service_records");

            migrationBuilder.DropColumn(
                name: "RewardProfessionalCommission",
                schema: "public",
                table: "service_records");

            migrationBuilder.DropColumn(
                name: "NewRedemptionApprovalDays",
                schema: "public",
                table: "loyalty_settings_changes");

            migrationBuilder.DropColumn(
                name: "NewRedemptionDeliveryDays",
                schema: "public",
                table: "loyalty_settings_changes");

            migrationBuilder.DropColumn(
                name: "PreviousRedemptionApprovalDays",
                schema: "public",
                table: "loyalty_settings_changes");

            migrationBuilder.DropColumn(
                name: "PreviousRedemptionDeliveryDays",
                schema: "public",
                table: "loyalty_settings_changes");

            migrationBuilder.DropColumn(
                name: "RedemptionApprovalDays",
                schema: "public",
                table: "loyalty_settings");

            migrationBuilder.DropColumn(
                name: "RedemptionDeliveryDays",
                schema: "public",
                table: "loyalty_settings");

            migrationBuilder.DropColumn(
                name: "RedemptionId",
                schema: "public",
                table: "loyalty_movements");

            migrationBuilder.DropColumn(
                name: "ReservedPoints",
                schema: "public",
                table: "loyalty_lots");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_records_operation_shape",
                schema: "public",
                table: "service_records",
                sql: "\"OperationType\" IS NULL OR (\"OperationType\" = 'Paid' AND \"ChargedAmount\" IS NOT NULL AND \"ChargedAmount\" > 0 AND \"IncomeEntryId\" IS NOT NULL AND \"CustomerMembershipId\" IS NULL AND \"MembershipConsumptionId\" IS NULL) OR (\"OperationType\" = 'MembershipConsumption' AND \"ChargedAmount\" IS NULL AND \"IncomeEntryId\" IS NULL AND \"ApprovedCustomerId\" IS NOT NULL AND \"CustomerMembershipId\" IS NOT NULL AND \"MembershipConsumptionId\" IS NOT NULL) OR (\"OperationType\" = 'Courtesy' AND \"ChargedAmount\" IS NULL AND \"IncomeEntryId\" IS NULL AND \"CustomerMembershipId\" IS NULL AND \"MembershipConsumptionId\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_movements_once_per_lot",
                schema: "public",
                table: "loyalty_movements",
                columns: new[] { "LotId", "Type" },
                unique: true,
                filter: "\"Type\" IN ('Accrual', 'MergeTransferIn', 'AdjustmentCredit', 'Expiration', 'MembershipAnnulmentWithdrawal', 'MergeTransferOut')");
        }
    }
}
