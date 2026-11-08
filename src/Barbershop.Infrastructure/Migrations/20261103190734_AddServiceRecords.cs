using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_records",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RegistrationKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    StaffProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceNameSnapshot = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    PerformedOn = table.Column<DateOnly>(type: "date", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    CustomerNameSnapshot = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RegisteredByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RegisteredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OperationType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ApprovedServiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedServiceNameSnapshot = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ApprovedStaffProfileId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedCustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedPerformedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ChargedAmount = table.Column<int>(type: "integer", nullable: true),
                    IncomeEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    CustomerMembershipId = table.Column<Guid>(type: "uuid", nullable: true),
                    MembershipConsumptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    LoyaltyRedemptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    AccruesLoyaltyPoints = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    LoyaltyPointsSnapshot = table.Column<int>(type: "integer", nullable: true),
                    ReviewNotes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ApprovedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RejectionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    RejectedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RejectedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_records", x => x.Id);
                    table.CheckConstraint("ck_service_records_loyalty_points", "\"AccruesLoyaltyPoints\" = false OR (\"OperationType\" = 'Paid' AND \"ApprovedCustomerId\" IS NOT NULL AND \"LoyaltyPointsSnapshot\" IS NOT NULL AND \"LoyaltyPointsSnapshot\" > 0)");
                    table.CheckConstraint("ck_service_records_operation_shape", "\"OperationType\" IS NULL OR (\"OperationType\" = 'Paid' AND \"ChargedAmount\" IS NOT NULL AND \"ChargedAmount\" > 0 AND \"IncomeEntryId\" IS NOT NULL AND \"CustomerMembershipId\" IS NULL AND \"MembershipConsumptionId\" IS NULL) OR (\"OperationType\" = 'MembershipConsumption' AND \"ChargedAmount\" IS NULL AND \"IncomeEntryId\" IS NULL AND \"ApprovedCustomerId\" IS NOT NULL AND \"CustomerMembershipId\" IS NOT NULL AND \"MembershipConsumptionId\" IS NOT NULL) OR (\"OperationType\" = 'Courtesy' AND \"ChargedAmount\" IS NULL AND \"IncomeEntryId\" IS NULL AND \"CustomerMembershipId\" IS NULL AND \"MembershipConsumptionId\" IS NULL)");
                    table.CheckConstraint("ck_service_records_status_shape", "(\"Status\" = 'Pending' AND \"OperationType\" IS NULL AND \"ApprovedAt\" IS NULL AND \"RejectedAt\" IS NULL) OR (\"Status\" = 'Approved' AND \"OperationType\" IS NOT NULL AND \"ApprovedAt\" IS NOT NULL AND \"ApprovedByUserId\" IS NOT NULL AND \"ApprovedServiceId\" IS NOT NULL AND \"ApprovedStaffProfileId\" IS NOT NULL AND \"ApprovedPerformedOn\" IS NOT NULL AND \"RejectedAt\" IS NULL) OR (\"Status\" = 'Rejected' AND \"OperationType\" IS NULL AND \"RejectionReason\" IS NOT NULL AND \"RejectedAt\" IS NOT NULL AND \"RejectedByUserId\" IS NOT NULL AND \"ApprovedAt\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_service_records_appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalSchema: "public",
                        principalTable: "appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_service_records_customer_memberships_CustomerMembershipId",
                        column: x => x.CustomerMembershipId,
                        principalSchema: "public",
                        principalTable: "customer_memberships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_service_records_customers_ApprovedCustomerId",
                        column: x => x.ApprovedCustomerId,
                        principalSchema: "public",
                        principalTable: "customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_service_records_customers_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "public",
                        principalTable: "customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_service_records_income_entries_IncomeEntryId",
                        column: x => x.IncomeEntryId,
                        principalSchema: "public",
                        principalTable: "income_entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_service_records_membership_consumptions_MembershipConsumpti~",
                        column: x => x.MembershipConsumptionId,
                        principalSchema: "public",
                        principalTable: "membership_consumptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_service_records_services_ApprovedServiceId",
                        column: x => x.ApprovedServiceId,
                        principalSchema: "public",
                        principalTable: "services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_service_records_services_ServiceId",
                        column: x => x.ServiceId,
                        principalSchema: "public",
                        principalTable: "services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_service_records_staff_profiles_ApprovedStaffProfileId",
                        column: x => x.ApprovedStaffProfileId,
                        principalSchema: "public",
                        principalTable: "staff_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_service_records_staff_profiles_StaffProfileId",
                        column: x => x.StaffProfileId,
                        principalSchema: "public",
                        principalTable: "staff_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_service_records_AppointmentId",
                schema: "public",
                table: "service_records",
                column: "AppointmentId");

            migrationBuilder.CreateIndex(
                name: "IX_service_records_ApprovedCustomerId",
                schema: "public",
                table: "service_records",
                column: "ApprovedCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_service_records_ApprovedServiceId",
                schema: "public",
                table: "service_records",
                column: "ApprovedServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_service_records_ApprovedStaffProfileId",
                schema: "public",
                table: "service_records",
                column: "ApprovedStaffProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_service_records_CustomerId",
                schema: "public",
                table: "service_records",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_service_records_CustomerMembershipId",
                schema: "public",
                table: "service_records",
                column: "CustomerMembershipId");

            migrationBuilder.CreateIndex(
                name: "IX_service_records_IncomeEntryId",
                schema: "public",
                table: "service_records",
                column: "IncomeEntryId",
                unique: true,
                filter: "\"IncomeEntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_service_records_MembershipConsumptionId",
                schema: "public",
                table: "service_records",
                column: "MembershipConsumptionId",
                unique: true,
                filter: "\"MembershipConsumptionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_service_records_RegistrationKey_Position",
                schema: "public",
                table: "service_records",
                columns: new[] { "RegistrationKey", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_service_records_ServiceId",
                schema: "public",
                table: "service_records",
                column: "ServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_service_records_StaffProfileId_RegisteredAt",
                schema: "public",
                table: "service_records",
                columns: new[] { "StaffProfileId", "RegisteredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_service_records_Status_RegisteredAt",
                schema: "public",
                table: "service_records",
                columns: new[] { "Status", "RegisteredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "service_records",
                schema: "public");
        }
    }
}
