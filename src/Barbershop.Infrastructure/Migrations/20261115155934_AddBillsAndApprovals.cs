using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBillsAndApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADR-0020 stages 2 and 3. Additive: the tables of the bills, their approvals and postings, the payment methods
            // (Q1) and the number series (Q7), with their seed rows in those new tables; nullable columns in income_entries
            // and loyalty_redemptions (every existing row keeps NULL). NOT additive, authorized by the user: the CHECK
            // ck_income_entries_kind_consistency is replaced to admit ProductSale and Tip, and ck_loyalty_redemptions_status_shape
            // to let an approved bill deliver a service reward (BillLineId instead of ServiceRecordId). Both keep every
            // existing row valid: no data is deleted or modified. Down restores the old CHECKs and fails if rows of the
            // new kinds exist (restore a backup instead of going down).
            migrationBuilder.DropCheckConstraint(
                name: "ck_loyalty_redemptions_status_shape",
                schema: "public",
                table: "loyalty_redemptions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_income_entries_kind_consistency",
                schema: "public",
                table: "income_entries");

            migrationBuilder.AddColumn<Guid>(
                name: "BillLineId",
                schema: "public",
                table: "loyalty_redemptions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BillId",
                schema: "public",
                table: "income_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BillLineId",
                schema: "public",
                table: "income_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CommissionBaseAmount",
                schema: "public",
                table: "income_entries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ProductId",
                schema: "public",
                table: "income_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProductNameSnapshot",
                schema: "public",
                table: "income_entries",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Quantity",
                schema: "public",
                table: "income_entries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitCostSnapshot",
                schema: "public",
                table: "income_entries",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "bill_number_series",
                schema: "public",
                columns: table => new
                {
                    Prefix = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    NextNumber = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bill_number_series", x => x.Prefix);
                    table.CheckConstraint("ck_bill_number_series_next_positive", "\"NextNumber\" >= 1");
                });

            migrationBuilder.CreateTable(
                name: "payment_methods",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_methods", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "bills",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Series = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    Number = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    CustomerNameSnapshot = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    StaffProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    ServiceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    OpenedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpenedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClosedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    PaymentMethodId = table.Column<Guid>(type: "uuid", nullable: true),
                    PaymentMethodNameSnapshot = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    Subtotal = table.Column<int>(type: "integer", nullable: false),
                    DiscountTotal = table.Column<int>(type: "integer", nullable: false),
                    TipTotal = table.Column<int>(type: "integer", nullable: false),
                    DirectTipTotal = table.Column<int>(type: "integer", nullable: false),
                    Total = table.Column<int>(type: "integer", nullable: false),
                    CurrentApprovalId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsSelfApproved = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    VoidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    VoidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    VoidReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bills", x => x.Id);
                    table.CheckConstraint("ck_bills_amounts_non_negative", "\"Subtotal\" >= 0 AND \"DiscountTotal\" >= 0 AND \"TipTotal\" >= 0 AND \"DirectTipTotal\" >= 0 AND \"Total\" >= 0");
                    table.CheckConstraint("ck_bills_approved_shape", "\"Status\" <> 'Approved' OR (\"CurrentApprovalId\" IS NOT NULL AND \"ApprovedAt\" IS NOT NULL AND \"ApprovedByUserId\" IS NOT NULL)");
                    table.CheckConstraint("ck_bills_closed_shape", "\"Status\" = 'Open' OR (\"Status\" = 'Voided' AND \"ClosedAt\" IS NULL) OR (\"Number\" IS NOT NULL AND \"ClosedAt\" IS NOT NULL AND \"ClosedByUserId\" IS NOT NULL)");
                    table.CheckConstraint("ck_bills_number_shape", "(\"Number\" IS NULL AND \"Series\" IS NULL) OR (\"Number\" IS NOT NULL AND \"Series\" IS NOT NULL AND \"Number\" > 0)");
                    table.CheckConstraint("ck_bills_payment_method_shape", "\"ClosedAt\" IS NULL OR (\"Total\" > 0) = (\"PaymentMethodId\" IS NOT NULL)");
                    table.CheckConstraint("ck_bills_total_consistent", "\"Total\" = \"Subtotal\" - \"DiscountTotal\" + \"TipTotal\"");
                    table.CheckConstraint("ck_bills_voided_shape", "(\"Status\" = 'Voided') = (\"VoidedAt\" IS NOT NULL AND \"VoidReason\" IS NOT NULL AND \"VoidedByUserId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_bills_appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalSchema: "public",
                        principalTable: "appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bills_customers_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "public",
                        principalTable: "customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bills_payment_methods_PaymentMethodId",
                        column: x => x.PaymentMethodId,
                        principalSchema: "public",
                        principalTable: "payment_methods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bills_staff_profiles_StaffProfileId",
                        column: x => x.StaffProfileId,
                        principalSchema: "public",
                        principalTable: "staff_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bills_users_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bills_users_ClosedByUserId",
                        column: x => x.ClosedByUserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bills_users_OpenedByUserId",
                        column: x => x.OpenedByUserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bills_users_VoidedByUserId",
                        column: x => x.VoidedByUserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bill_approvals",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BillId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsSelfApproved = table.Column<bool>(type: "boolean", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    LinesJson = table.Column<string>(type: "jsonb", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bill_approvals", x => x.Id);
                    table.CheckConstraint("ck_bill_approvals_sequence_positive", "\"Sequence\" >= 1");
                    table.ForeignKey(
                        name: "FK_bill_approvals_bills_BillId",
                        column: x => x.BillId,
                        principalSchema: "public",
                        principalTable: "bills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bill_approvals_customers_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "public",
                        principalTable: "customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bill_approvals_users_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bill_events",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BillId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StatusAtEvent = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    BeforeJson = table.Column<string>(type: "jsonb", nullable: true),
                    AfterJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bill_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_bill_events_bills_BillId",
                        column: x => x.BillId,
                        principalSchema: "public",
                        principalTable: "bills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bill_events_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bill_lines",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BillId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    StaffProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    GeneralServiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ServiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    NameSnapshot = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    UnitPrice = table.Column<int>(type: "integer", nullable: false),
                    BusinessPercentageSnapshot = table.Column<int>(type: "integer", nullable: true),
                    IsCourtesy = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: true),
                    CustomerMembershipId = table.Column<Guid>(type: "uuid", nullable: true),
                    LoyaltyRedemptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    AppliesToLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    TipPaymentMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Amount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bill_lines", x => x.Id);
                    table.CheckConstraint("ck_bill_lines_business_percentage_range", "\"BusinessPercentageSnapshot\" IS NULL OR (\"BusinessPercentageSnapshot\" >= 0 AND \"BusinessPercentageSnapshot\" <= 100)");
                    table.CheckConstraint("ck_bill_lines_quantity_range", "\"Quantity\" >= 1 AND \"Quantity\" <= 1000");
                    table.CheckConstraint("ck_bill_lines_shape", "(\"Type\" = 'Service' AND \"StaffProfileId\" IS NOT NULL AND \"GeneralServiceId\" IS NOT NULL AND \"Quantity\" = 1 AND \"ProductId\" IS NULL AND \"CustomerMembershipId\" IS NULL AND \"LoyaltyRedemptionId\" IS NULL AND \"AppliesToLineId\" IS NULL AND \"TipPaymentMode\" IS NULL AND (\"IsCourtesy\" = false OR \"Reason\" IS NOT NULL) AND \"Amount\" = CASE WHEN \"IsCourtesy\" THEN 0 ELSE \"UnitPrice\" END) OR (\"Type\" = 'Product' AND \"ProductId\" IS NOT NULL AND \"ServiceId\" IS NULL AND \"CustomerMembershipId\" IS NULL AND \"LoyaltyRedemptionId\" IS NULL AND \"AppliesToLineId\" IS NULL AND \"TipPaymentMode\" IS NULL AND \"IsCourtesy\" = false AND \"Amount\" = \"Quantity\" * \"UnitPrice\") OR (\"Type\" = 'MembershipConsumption' AND \"CustomerMembershipId\" IS NOT NULL AND \"ServiceId\" IS NOT NULL AND \"StaffProfileId\" IS NOT NULL AND \"Quantity\" = 1 AND \"UnitPrice\" = 0 AND \"Amount\" = 0 AND \"ProductId\" IS NULL AND \"LoyaltyRedemptionId\" IS NULL AND \"AppliesToLineId\" IS NULL AND \"TipPaymentMode\" IS NULL AND \"IsCourtesy\" = false) OR (\"Type\" = 'Redemption' AND \"LoyaltyRedemptionId\" IS NOT NULL AND \"Quantity\" = 1 AND \"Amount\" = 0 AND \"ProductId\" IS NULL AND \"CustomerMembershipId\" IS NULL AND \"AppliesToLineId\" IS NULL AND \"TipPaymentMode\" IS NULL AND \"IsCourtesy\" = false) OR (\"Type\" = 'Discount' AND \"Reason\" IS NOT NULL AND \"UnitPrice\" > 0 AND \"Amount\" = -\"UnitPrice\" AND \"Quantity\" = 1 AND \"StaffProfileId\" IS NULL AND \"ServiceId\" IS NULL AND \"ProductId\" IS NULL AND \"CustomerMembershipId\" IS NULL AND \"LoyaltyRedemptionId\" IS NULL AND \"TipPaymentMode\" IS NULL AND \"IsCourtesy\" = false) OR (\"Type\" = 'Tip' AND \"StaffProfileId\" IS NOT NULL AND \"TipPaymentMode\" IS NOT NULL AND \"UnitPrice\" > 0 AND \"Quantity\" = 1 AND \"Amount\" = CASE WHEN \"TipPaymentMode\" = 'WithBill' THEN \"UnitPrice\" ELSE 0 END AND \"ServiceId\" IS NULL AND \"ProductId\" IS NULL AND \"CustomerMembershipId\" IS NULL AND \"LoyaltyRedemptionId\" IS NULL AND \"AppliesToLineId\" IS NULL AND \"IsCourtesy\" = false)");
                    table.CheckConstraint("ck_bill_lines_unit_price_non_negative", "\"UnitPrice\" >= 0");
                    table.ForeignKey(
                        name: "FK_bill_lines_bills_BillId",
                        column: x => x.BillId,
                        principalSchema: "public",
                        principalTable: "bills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_bill_lines_customer_memberships_CustomerMembershipId",
                        column: x => x.CustomerMembershipId,
                        principalSchema: "public",
                        principalTable: "customer_memberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bill_lines_general_services_GeneralServiceId",
                        column: x => x.GeneralServiceId,
                        principalSchema: "public",
                        principalTable: "general_services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bill_lines_loyalty_redemptions_LoyaltyRedemptionId",
                        column: x => x.LoyaltyRedemptionId,
                        principalSchema: "public",
                        principalTable: "loyalty_redemptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bill_lines_products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "public",
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bill_lines_services_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "public",
                        principalTable: "services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bill_lines_staff_profiles_StaffProfileId",
                        column: x => x.StaffProfileId,
                        principalSchema: "public",
                        principalTable: "staff_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bill_postings",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovalId = table.Column<Guid>(type: "uuid", nullable: false),
                    BillLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Effect = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IncomeEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    MembershipConsumptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    LoyaltyLotId = table.Column<Guid>(type: "uuid", nullable: true),
                    LoyaltyRedemptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    InventoryMovementId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bill_postings", x => x.Id);
                    table.CheckConstraint("ck_bill_postings_target_shape", "(\"Effect\" = 'Income' AND \"IncomeEntryId\" IS NOT NULL) OR (\"Effect\" = 'MembershipConsumption' AND \"MembershipConsumptionId\" IS NOT NULL) OR (\"Effect\" = 'LoyaltyAccrual' AND \"LoyaltyLotId\" IS NOT NULL) OR (\"Effect\" = 'RedemptionDelivery' AND \"LoyaltyRedemptionId\" IS NOT NULL) OR (\"Effect\" = 'InventorySale' AND \"InventoryMovementId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_bill_postings_bill_approvals_ApprovalId",
                        column: x => x.ApprovalId,
                        principalSchema: "public",
                        principalTable: "bill_approvals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bill_postings_bill_lines_BillLineId",
                        column: x => x.BillLineId,
                        principalSchema: "public",
                        principalTable: "bill_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bill_postings_income_entries_IncomeEntryId",
                        column: x => x.IncomeEntryId,
                        principalSchema: "public",
                        principalTable: "income_entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bill_postings_inventory_movements_InventoryMovementId",
                        column: x => x.InventoryMovementId,
                        principalSchema: "public",
                        principalTable: "inventory_movements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bill_postings_loyalty_lots_LoyaltyLotId",
                        column: x => x.LoyaltyLotId,
                        principalSchema: "public",
                        principalTable: "loyalty_lots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bill_postings_loyalty_redemptions_LoyaltyRedemptionId",
                        column: x => x.LoyaltyRedemptionId,
                        principalSchema: "public",
                        principalTable: "loyalty_redemptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_bill_postings_membership_consumptions_MembershipConsumption~",
                        column: x => x.MembershipConsumptionId,
                        principalSchema: "public",
                        principalTable: "membership_consumptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "public",
                table: "bill_number_series",
                columns: new[] { "Prefix", "NextNumber", "UpdatedAt" },
                values: new object[] { "C", 1, null });

            migrationBuilder.InsertData(
                schema: "public",
                table: "payment_methods",
                columns: new[] { "Id", "CreatedAt", "IsActive", "Name", "SortOrder", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("5d0c3a0e-6a4b-4f5e-9b0a-1c2d3e4f5a01"), new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), true, "Efectivo", 10, null },
                    { new Guid("5d0c3a0e-6a4b-4f5e-9b0a-1c2d3e4f5a02"), new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), true, "Transferencia", 20, null },
                    { new Guid("5d0c3a0e-6a4b-4f5e-9b0a-1c2d3e4f5a03"), new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), true, "Tarjeta", 30, null },
                    { new Guid("5d0c3a0e-6a4b-4f5e-9b0a-1c2d3e4f5a04"), new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), true, "Nequi o Daviplata", 40, null },
                    { new Guid("5d0c3a0e-6a4b-4f5e-9b0a-1c2d3e4f5a05"), new DateTime(2026, 11, 15, 0, 0, 0, 0, DateTimeKind.Utc), true, "Otro", 50, null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_loyalty_redemptions_BillLineId",
                schema: "public",
                table: "loyalty_redemptions",
                column: "BillLineId",
                unique: true,
                filter: "\"BillLineId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loyalty_redemptions_bill_line_shape",
                schema: "public",
                table: "loyalty_redemptions",
                sql: "\"BillLineId\" IS NULL OR (\"Status\" = 'Delivered' AND \"ServiceRecordId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loyalty_redemptions_status_shape",
                schema: "public",
                table: "loyalty_redemptions",
                sql: "(\"Status\" = 'Requested' AND \"ApprovedAt\" IS NULL AND \"DeliveredAt\" IS NULL AND \"RejectedAt\" IS NULL AND \"CancelledAt\" IS NULL AND \"ExpiredAt\" IS NULL) OR (\"Status\" = 'Approved' AND \"ApprovedAt\" IS NOT NULL AND \"ApprovedByUserId\" IS NOT NULL AND \"ApprovedOn\" IS NOT NULL AND \"DeliverBy\" IS NOT NULL AND \"DeliveredAt\" IS NULL AND \"RejectedAt\" IS NULL AND \"CancelledAt\" IS NULL AND \"ExpiredAt\" IS NULL) OR (\"Status\" = 'Delivered' AND \"ApprovedAt\" IS NOT NULL AND \"DeliveredAt\" IS NOT NULL AND \"DeliveredByUserId\" IS NOT NULL AND \"RejectedAt\" IS NULL AND \"CancelledAt\" IS NULL AND \"ExpiredAt\" IS NULL AND ((\"RewardKindSnapshot\" = 'Service' AND (\"ServiceRecordId\" IS NOT NULL OR \"BillLineId\" IS NOT NULL)) OR (\"RewardKindSnapshot\" = 'Other' AND \"ServiceRecordId\" IS NULL))) OR (\"Status\" = 'Rejected' AND \"RejectedAt\" IS NOT NULL AND \"RejectedByUserId\" IS NOT NULL AND \"RejectionReason\" IS NOT NULL AND \"ApprovedAt\" IS NULL AND \"DeliveredAt\" IS NULL AND \"CancelledAt\" IS NULL AND \"ExpiredAt\" IS NULL) OR (\"Status\" = 'Cancelled' AND \"CancelledAt\" IS NOT NULL AND \"CancelledByUserId\" IS NOT NULL AND \"DeliveredAt\" IS NULL AND \"RejectedAt\" IS NULL AND \"ExpiredAt\" IS NULL) OR (\"Status\" = 'Expired' AND \"ExpiredAt\" IS NOT NULL AND \"DeliveredAt\" IS NULL AND \"RejectedAt\" IS NULL AND \"CancelledAt\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_income_entries_BillId",
                schema: "public",
                table: "income_entries",
                column: "BillId");

            migrationBuilder.CreateIndex(
                name: "IX_income_entries_BillLineId",
                schema: "public",
                table: "income_entries",
                column: "BillLineId");

            migrationBuilder.CreateIndex(
                name: "IX_income_entries_ProductId",
                schema: "public",
                table: "income_entries",
                column: "ProductId");

            migrationBuilder.AddCheckConstraint(
                name: "ck_income_entries_bill_shape",
                schema: "public",
                table: "income_entries",
                sql: "(\"BillId\" IS NULL) = (\"BillLineId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_income_entries_commission_base_range",
                schema: "public",
                table: "income_entries",
                sql: "\"CommissionBaseAmount\" IS NULL OR \"CommissionBaseAmount\" >= \"Amount\"");

            migrationBuilder.AddCheckConstraint(
                name: "ck_income_entries_kind_consistency",
                schema: "public",
                table: "income_entries",
                sql: "(\"Kind\" = 'Service' AND \"ServiceId\" IS NOT NULL AND \"StaffProfileId\" IS NOT NULL AND \"ServiceNameSnapshot\" IS NOT NULL) OR (\"Kind\" = 'MembershipSale' AND \"ServiceId\" IS NULL AND \"StaffProfileId\" IS NULL AND \"ServiceNameSnapshot\" IS NULL AND \"BusinessPercentageSnapshot\" = 100 AND \"BasePriceSnapshot\" = 0 AND \"IsPromo\" = false) OR (\"Kind\" = 'ProductSale' AND \"ServiceId\" IS NULL AND \"StaffProfileId\" IS NULL AND \"ServiceNameSnapshot\" IS NULL AND \"ProductId\" IS NOT NULL AND \"ProductNameSnapshot\" IS NOT NULL AND \"Quantity\" IS NOT NULL AND \"Quantity\" > 0 AND \"BillLineId\" IS NOT NULL AND \"BusinessPercentageSnapshot\" = 100 AND \"IsPromo\" = false) OR (\"Kind\" = 'Tip' AND \"ServiceId\" IS NULL AND \"StaffProfileId\" IS NOT NULL AND \"ServiceNameSnapshot\" IS NULL AND \"ProductId\" IS NULL AND \"BillLineId\" IS NOT NULL AND \"BusinessPercentageSnapshot\" = 0 AND \"BasePriceSnapshot\" = 0 AND \"IsPromo\" = false)");

            migrationBuilder.CreateIndex(
                name: "IX_bill_approvals_ApprovedByUserId",
                schema: "public",
                table: "bill_approvals",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_bill_approvals_BillId_Sequence",
                schema: "public",
                table: "bill_approvals",
                columns: new[] { "BillId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bill_approvals_CustomerId",
                schema: "public",
                table: "bill_approvals",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_bill_approvals_IdempotencyKey",
                schema: "public",
                table: "bill_approvals",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bill_events_BillId_CreatedAt",
                schema: "public",
                table: "bill_events",
                columns: new[] { "BillId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_bill_events_UserId",
                schema: "public",
                table: "bill_events",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_bill_lines_BillId_Position",
                schema: "public",
                table: "bill_lines",
                columns: new[] { "BillId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_bill_lines_CustomerMembershipId",
                schema: "public",
                table: "bill_lines",
                column: "CustomerMembershipId");

            migrationBuilder.CreateIndex(
                name: "IX_bill_lines_GeneralServiceId",
                schema: "public",
                table: "bill_lines",
                column: "GeneralServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_bill_lines_LoyaltyRedemptionId",
                schema: "public",
                table: "bill_lines",
                column: "LoyaltyRedemptionId");

            migrationBuilder.CreateIndex(
                name: "IX_bill_lines_ProductId",
                schema: "public",
                table: "bill_lines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_bill_lines_ServiceId",
                schema: "public",
                table: "bill_lines",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_bill_lines_StaffProfileId",
                schema: "public",
                table: "bill_lines",
                column: "StaffProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_bill_postings_ApprovalId_BillLineId_Effect",
                schema: "public",
                table: "bill_postings",
                columns: new[] { "ApprovalId", "BillLineId", "Effect" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bill_postings_BillLineId",
                schema: "public",
                table: "bill_postings",
                column: "BillLineId");

            migrationBuilder.CreateIndex(
                name: "IX_bill_postings_IncomeEntryId",
                schema: "public",
                table: "bill_postings",
                column: "IncomeEntryId",
                unique: true,
                filter: "\"IncomeEntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_bill_postings_InventoryMovementId",
                schema: "public",
                table: "bill_postings",
                column: "InventoryMovementId",
                unique: true,
                filter: "\"InventoryMovementId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_bill_postings_LoyaltyLotId",
                schema: "public",
                table: "bill_postings",
                column: "LoyaltyLotId",
                unique: true,
                filter: "\"LoyaltyLotId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_bill_postings_LoyaltyRedemptionId",
                schema: "public",
                table: "bill_postings",
                column: "LoyaltyRedemptionId");

            migrationBuilder.CreateIndex(
                name: "IX_bill_postings_MembershipConsumptionId",
                schema: "public",
                table: "bill_postings",
                column: "MembershipConsumptionId",
                unique: true,
                filter: "\"MembershipConsumptionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_bills_AppointmentId_not_voided",
                schema: "public",
                table: "bills",
                column: "AppointmentId",
                unique: true,
                filter: "\"AppointmentId\" IS NOT NULL AND \"Status\" <> 'Voided'");

            migrationBuilder.CreateIndex(
                name: "IX_bills_ApprovedByUserId",
                schema: "public",
                table: "bills",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_bills_ClosedByUserId",
                schema: "public",
                table: "bills",
                column: "ClosedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_bills_CustomerId",
                schema: "public",
                table: "bills",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_bills_IdempotencyKey",
                schema: "public",
                table: "bills",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bills_OpenedByUserId",
                schema: "public",
                table: "bills",
                column: "OpenedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_bills_PaymentMethodId",
                schema: "public",
                table: "bills",
                column: "PaymentMethodId");

            migrationBuilder.CreateIndex(
                name: "IX_bills_Series_Number",
                schema: "public",
                table: "bills",
                columns: new[] { "Series", "Number" },
                unique: true,
                filter: "\"Number\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_bills_ServiceDate",
                schema: "public",
                table: "bills",
                column: "ServiceDate");

            migrationBuilder.CreateIndex(
                name: "IX_bills_StaffProfileId_Status",
                schema: "public",
                table: "bills",
                columns: new[] { "StaffProfileId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_bills_Status_OpenedAt",
                schema: "public",
                table: "bills",
                columns: new[] { "Status", "OpenedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_bills_VoidedByUserId",
                schema: "public",
                table: "bills",
                column: "VoidedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_methods_Name",
                schema: "public",
                table: "payment_methods",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_income_entries_bill_lines_BillLineId",
                schema: "public",
                table: "income_entries",
                column: "BillLineId",
                principalSchema: "public",
                principalTable: "bill_lines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_income_entries_bills_BillId",
                schema: "public",
                table: "income_entries",
                column: "BillId",
                principalSchema: "public",
                principalTable: "bills",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_income_entries_products_ProductId",
                schema: "public",
                table: "income_entries",
                column: "ProductId",
                principalSchema: "public",
                principalTable: "products",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_loyalty_redemptions_bill_lines_BillLineId",
                schema: "public",
                table: "loyalty_redemptions",
                column: "BillLineId",
                principalSchema: "public",
                principalTable: "bill_lines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // The audit trail of the bills is append-only, like the stock card and the membership ledger.
            migrationBuilder.Sql("""
                CREATE FUNCTION public.bill_events_are_append_only() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'bill events (%) cannot be updated or deleted', TG_TABLE_NAME
                        USING ERRCODE = 'restrict_violation';
                END;
                $$;

                CREATE TRIGGER trg_bill_events_append_only
                    BEFORE UPDATE OR DELETE ON public.bill_events
                    FOR EACH ROW EXECUTE FUNCTION public.bill_events_are_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_income_entries_bill_lines_BillLineId",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropForeignKey(
                name: "FK_income_entries_bills_BillId",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropForeignKey(
                name: "FK_income_entries_products_ProductId",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropForeignKey(
                name: "FK_loyalty_redemptions_bill_lines_BillLineId",
                schema: "public",
                table: "loyalty_redemptions");

            migrationBuilder.DropTable(
                name: "bill_events",
                schema: "public");

            migrationBuilder.DropTable(
                name: "bill_number_series",
                schema: "public");

            migrationBuilder.DropTable(
                name: "bill_postings",
                schema: "public");

            migrationBuilder.DropTable(
                name: "bill_approvals",
                schema: "public");

            migrationBuilder.DropTable(
                name: "bill_lines",
                schema: "public");

            migrationBuilder.DropTable(
                name: "bills",
                schema: "public");

            migrationBuilder.DropTable(
                name: "payment_methods",
                schema: "public");

            migrationBuilder.DropIndex(
                name: "IX_loyalty_redemptions_BillLineId",
                schema: "public",
                table: "loyalty_redemptions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loyalty_redemptions_bill_line_shape",
                schema: "public",
                table: "loyalty_redemptions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_loyalty_redemptions_status_shape",
                schema: "public",
                table: "loyalty_redemptions");

            migrationBuilder.DropIndex(
                name: "IX_income_entries_BillId",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropIndex(
                name: "IX_income_entries_BillLineId",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropIndex(
                name: "IX_income_entries_ProductId",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_income_entries_bill_shape",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_income_entries_commission_base_range",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_income_entries_kind_consistency",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropColumn(
                name: "BillLineId",
                schema: "public",
                table: "loyalty_redemptions");

            migrationBuilder.DropColumn(
                name: "BillId",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropColumn(
                name: "BillLineId",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropColumn(
                name: "CommissionBaseAmount",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropColumn(
                name: "ProductId",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropColumn(
                name: "ProductNameSnapshot",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropColumn(
                name: "Quantity",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropColumn(
                name: "UnitCostSnapshot",
                schema: "public",
                table: "income_entries");

            migrationBuilder.AddCheckConstraint(
                name: "ck_loyalty_redemptions_status_shape",
                schema: "public",
                table: "loyalty_redemptions",
                sql: "(\"Status\" = 'Requested' AND \"ApprovedAt\" IS NULL AND \"DeliveredAt\" IS NULL AND \"RejectedAt\" IS NULL AND \"CancelledAt\" IS NULL AND \"ExpiredAt\" IS NULL) OR (\"Status\" = 'Approved' AND \"ApprovedAt\" IS NOT NULL AND \"ApprovedByUserId\" IS NOT NULL AND \"ApprovedOn\" IS NOT NULL AND \"DeliverBy\" IS NOT NULL AND \"DeliveredAt\" IS NULL AND \"RejectedAt\" IS NULL AND \"CancelledAt\" IS NULL AND \"ExpiredAt\" IS NULL) OR (\"Status\" = 'Delivered' AND \"ApprovedAt\" IS NOT NULL AND \"DeliveredAt\" IS NOT NULL AND \"DeliveredByUserId\" IS NOT NULL AND \"RejectedAt\" IS NULL AND \"CancelledAt\" IS NULL AND \"ExpiredAt\" IS NULL AND ((\"RewardKindSnapshot\" = 'Service' AND \"ServiceRecordId\" IS NOT NULL) OR (\"RewardKindSnapshot\" = 'Other' AND \"ServiceRecordId\" IS NULL))) OR (\"Status\" = 'Rejected' AND \"RejectedAt\" IS NOT NULL AND \"RejectedByUserId\" IS NOT NULL AND \"RejectionReason\" IS NOT NULL AND \"ApprovedAt\" IS NULL AND \"DeliveredAt\" IS NULL AND \"CancelledAt\" IS NULL AND \"ExpiredAt\" IS NULL) OR (\"Status\" = 'Cancelled' AND \"CancelledAt\" IS NOT NULL AND \"CancelledByUserId\" IS NOT NULL AND \"DeliveredAt\" IS NULL AND \"RejectedAt\" IS NULL AND \"ExpiredAt\" IS NULL) OR (\"Status\" = 'Expired' AND \"ExpiredAt\" IS NOT NULL AND \"DeliveredAt\" IS NULL AND \"RejectedAt\" IS NULL AND \"CancelledAt\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_income_entries_kind_consistency",
                schema: "public",
                table: "income_entries",
                sql: "(\"Kind\" = 'Service' AND \"ServiceId\" IS NOT NULL AND \"StaffProfileId\" IS NOT NULL AND \"ServiceNameSnapshot\" IS NOT NULL) OR (\"Kind\" = 'MembershipSale' AND \"ServiceId\" IS NULL AND \"StaffProfileId\" IS NULL AND \"ServiceNameSnapshot\" IS NULL AND \"BusinessPercentageSnapshot\" = 100 AND \"BasePriceSnapshot\" = 0 AND \"IsPromo\" = false)");

            migrationBuilder.Sql("DROP FUNCTION public.bill_events_are_append_only();");
        }
    }
}
