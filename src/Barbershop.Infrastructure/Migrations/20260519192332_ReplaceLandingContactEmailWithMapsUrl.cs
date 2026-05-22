using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceLandingContactEmailWithMapsUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContactEmail",
                schema: "public",
                table: "landing_content");

            migrationBuilder.AddColumn<string>(
                name: "MapsUrl",
                schema: "public",
                table: "landing_content",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MapsUrl",
                schema: "public",
                table: "landing_content");

            migrationBuilder.AddColumn<string>(
                name: "ContactEmail",
                schema: "public",
                table: "landing_content",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        }
    }
}
