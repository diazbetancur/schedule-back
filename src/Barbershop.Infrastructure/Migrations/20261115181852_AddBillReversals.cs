using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBillReversals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADR-0020 stage 4 (Q8, Q9, Q20). Additive: nullable columns (income_entries.ReversesIncomeEntryId with its
            // unique index and foreign key, the unapproval of bill_approvals, the reversal of bill_postings,
            // loyalty_redemptions.ReopenedAt, loyalty_redemption_allocations.SupersededAt) and the CHECKs
            // ck_income_entries_reversal_shape and ck_bill_approvals_unapproval_shape. NOT additive, authorized by the user:
            // ck_income_entries_amount_non_negative and ck_income_entries_commission_base_range are replaced so that only a
            // counter-entry may be negative. Every existing row keeps satisfying them; no row is changed. Down restores the
            // old CHECKs and fails if counter-entries exist (restore a backup instead of going down).
            migrationBuilder.DropCheckConstraint(
                name: "ck_income_entries_amount_non_negative",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_income_entries_commission_base_range",
                schema: "public",
                table: "income_entries");

            migrationBuilder.AddColumn<DateTime>(
                name: "ReopenedAt",
                schema: "public",
                table: "loyalty_redemptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SupersededAt",
                schema: "public",
                table: "loyalty_redemption_allocations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReversesIncomeEntryId",
                schema: "public",
                table: "income_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReversalTargetId",
                schema: "public",
                table: "bill_postings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReversedAt",
                schema: "public",
                table: "bill_postings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsSelfUnapproved",
                schema: "public",
                table: "bill_approvals",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "UnapprovalKey",
                schema: "public",
                table: "bill_approvals",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UnapprovalReason",
                schema: "public",
                table: "bill_approvals",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UnapprovedAt",
                schema: "public",
                table: "bill_approvals",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UnapprovedByUserId",
                schema: "public",
                table: "bill_approvals",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_income_entries_ReversesIncomeEntryId",
                schema: "public",
                table: "income_entries",
                column: "ReversesIncomeEntryId",
                unique: true,
                filter: "\"ReversesIncomeEntryId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_income_entries_amount_non_negative",
                schema: "public",
                table: "income_entries",
                sql: "\"Amount\" >= 0 OR \"ReversesIncomeEntryId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_income_entries_commission_base_range",
                schema: "public",
                table: "income_entries",
                sql: "\"CommissionBaseAmount\" IS NULL OR (\"ReversesIncomeEntryId\" IS NULL AND \"CommissionBaseAmount\" >= \"Amount\") OR (\"ReversesIncomeEntryId\" IS NOT NULL AND \"CommissionBaseAmount\" <= \"Amount\")");

            migrationBuilder.AddCheckConstraint(
                name: "ck_income_entries_reversal_shape",
                schema: "public",
                table: "income_entries",
                sql: "\"ReversesIncomeEntryId\" IS NULL OR (\"BillLineId\" IS NOT NULL AND \"Amount\" <= 0)");

            migrationBuilder.CreateIndex(
                name: "IX_bill_approvals_UnapprovalKey",
                schema: "public",
                table: "bill_approvals",
                column: "UnapprovalKey",
                unique: true,
                filter: "\"UnapprovalKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_bill_approvals_UnapprovedByUserId",
                schema: "public",
                table: "bill_approvals",
                column: "UnapprovedByUserId");

            migrationBuilder.AddCheckConstraint(
                name: "ck_bill_approvals_unapproval_shape",
                schema: "public",
                table: "bill_approvals",
                sql: "(\"UnapprovedAt\" IS NULL AND \"UnapprovedByUserId\" IS NULL AND \"UnapprovalReason\" IS NULL AND \"UnapprovalKey\" IS NULL) OR (\"UnapprovedAt\" IS NOT NULL AND \"UnapprovedByUserId\" IS NOT NULL AND \"UnapprovalReason\" IS NOT NULL AND \"UnapprovalKey\" IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_bill_approvals_users_UnapprovedByUserId",
                schema: "public",
                table: "bill_approvals",
                column: "UnapprovedByUserId",
                principalSchema: "public",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_income_entries_income_entries_ReversesIncomeEntryId",
                schema: "public",
                table: "income_entries",
                column: "ReversesIncomeEntryId",
                principalSchema: "public",
                principalTable: "income_entries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_bill_approvals_users_UnapprovedByUserId",
                schema: "public",
                table: "bill_approvals");

            migrationBuilder.DropForeignKey(
                name: "FK_income_entries_income_entries_ReversesIncomeEntryId",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropIndex(
                name: "IX_income_entries_ReversesIncomeEntryId",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_income_entries_amount_non_negative",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_income_entries_commission_base_range",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropCheckConstraint(
                name: "ck_income_entries_reversal_shape",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropIndex(
                name: "IX_bill_approvals_UnapprovalKey",
                schema: "public",
                table: "bill_approvals");

            migrationBuilder.DropIndex(
                name: "IX_bill_approvals_UnapprovedByUserId",
                schema: "public",
                table: "bill_approvals");

            migrationBuilder.DropCheckConstraint(
                name: "ck_bill_approvals_unapproval_shape",
                schema: "public",
                table: "bill_approvals");

            migrationBuilder.DropColumn(
                name: "ReopenedAt",
                schema: "public",
                table: "loyalty_redemptions");

            migrationBuilder.DropColumn(
                name: "SupersededAt",
                schema: "public",
                table: "loyalty_redemption_allocations");

            migrationBuilder.DropColumn(
                name: "ReversesIncomeEntryId",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropColumn(
                name: "ReversalTargetId",
                schema: "public",
                table: "bill_postings");

            migrationBuilder.DropColumn(
                name: "ReversedAt",
                schema: "public",
                table: "bill_postings");

            migrationBuilder.DropColumn(
                name: "IsSelfUnapproved",
                schema: "public",
                table: "bill_approvals");

            migrationBuilder.DropColumn(
                name: "UnapprovalKey",
                schema: "public",
                table: "bill_approvals");

            migrationBuilder.DropColumn(
                name: "UnapprovalReason",
                schema: "public",
                table: "bill_approvals");

            migrationBuilder.DropColumn(
                name: "UnapprovedAt",
                schema: "public",
                table: "bill_approvals");

            migrationBuilder.DropColumn(
                name: "UnapprovedByUserId",
                schema: "public",
                table: "bill_approvals");

            migrationBuilder.AddCheckConstraint(
                name: "ck_income_entries_amount_non_negative",
                schema: "public",
                table: "income_entries",
                sql: "\"Amount\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_income_entries_commission_base_range",
                schema: "public",
                table: "income_entries",
                sql: "\"CommissionBaseAmount\" IS NULL OR \"CommissionBaseAmount\" >= \"Amount\"");
        }
    }
}
