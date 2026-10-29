using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIncomeKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "StaffProfileId",
                schema: "public",
                table: "income_entries",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AlterColumn<string>(
                name: "ServiceNameSnapshot",
                schema: "public",
                table: "income_entries",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(120)",
                oldMaxLength: 120);

            migrationBuilder.AlterColumn<Guid>(
                name: "ServiceId",
                schema: "public",
                table: "income_entries",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                schema: "public",
                table: "income_entries",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Service");

            migrationBuilder.AddCheckConstraint(
                name: "ck_income_entries_kind_consistency",
                schema: "public",
                table: "income_entries",
                sql: "(\"Kind\" = 'Service' AND \"ServiceId\" IS NOT NULL AND \"StaffProfileId\" IS NOT NULL AND \"ServiceNameSnapshot\" IS NOT NULL) OR (\"Kind\" = 'MembershipSale' AND \"ServiceId\" IS NULL AND \"StaffProfileId\" IS NULL AND \"ServiceNameSnapshot\" IS NULL AND \"BusinessPercentageSnapshot\" = 100 AND \"BasePriceSnapshot\" = 0 AND \"IsPromo\" = false)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_income_entries_kind_consistency",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "public",
                table: "income_entries");

            migrationBuilder.AlterColumn<Guid>(
                name: "StaffProfileId",
                schema: "public",
                table: "income_entries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "ServiceNameSnapshot",
                schema: "public",
                table: "income_entries",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "character varying(120)",
                oldMaxLength: 120,
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ServiceId",
                schema: "public",
                table: "income_entries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
