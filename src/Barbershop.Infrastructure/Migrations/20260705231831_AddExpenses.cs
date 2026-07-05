using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddExpenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fixed_expenses",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    DefaultAmount = table.Column<int>(type: "integer", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fixed_expenses", x => x.Id);
                    table.CheckConstraint("ck_fixed_expenses_default_amount_non_negative", "\"DefaultAmount\" IS NULL OR \"DefaultAmount\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "expense_entries",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FixedExpenseId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Amount = table.Column<int>(type: "integer", nullable: false),
                    OccurredOn = table.Column<DateOnly>(type: "date", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_expense_entries", x => x.Id);
                    table.CheckConstraint("ck_expense_entries_amount_non_negative", "\"Amount\" >= 0");
                    table.ForeignKey(
                        name: "FK_expense_entries_fixed_expenses_FixedExpenseId",
                        column: x => x.FixedExpenseId,
                        principalSchema: "public",
                        principalTable: "fixed_expenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_expense_entries_FixedExpenseId",
                schema: "public",
                table: "expense_entries",
                column: "FixedExpenseId");

            migrationBuilder.CreateIndex(
                name: "IX_expense_entries_OccurredOn",
                schema: "public",
                table: "expense_entries",
                column: "OccurredOn");

            migrationBuilder.CreateIndex(
                name: "IX_fixed_expenses_Name",
                schema: "public",
                table: "fixed_expenses",
                column: "Name",
                unique: true,
                filter: "\"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "expense_entries",
                schema: "public");

            migrationBuilder.DropTable(
                name: "fixed_expenses",
                schema: "public");
        }
    }
}
