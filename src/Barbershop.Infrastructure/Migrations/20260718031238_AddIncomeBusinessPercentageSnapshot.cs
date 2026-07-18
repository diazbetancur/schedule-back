using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIncomeBusinessPercentageSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BusinessPercentageSnapshot",
                schema: "public",
                table: "income_entries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_income_entries_business_percentage_range",
                schema: "public",
                table: "income_entries",
                sql: "\"BusinessPercentageSnapshot\" >= 0 AND \"BusinessPercentageSnapshot\" <= 100");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_income_entries_business_percentage_range",
                schema: "public",
                table: "income_entries");

            migrationBuilder.DropColumn(
                name: "BusinessPercentageSnapshot",
                schema: "public",
                table: "income_entries");
        }
    }
}
