using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceBusinessPercentage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BusinessPercentage",
                schema: "public",
                table: "services",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "ck_services_business_percentage_range",
                schema: "public",
                table: "services",
                sql: "\"BusinessPercentage\" >= 0 AND \"BusinessPercentage\" <= 100");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_services_business_percentage_range",
                schema: "public",
                table: "services");

            migrationBuilder.DropColumn(
                name: "BusinessPercentage",
                schema: "public",
                table: "services");
        }
    }
}
