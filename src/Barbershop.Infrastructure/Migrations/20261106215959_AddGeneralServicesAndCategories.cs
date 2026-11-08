using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// ADR-0016 B1: service categories and general services, with the settlement rates (services) attached to one
    /// general service each. Seeds "Barbería" / "Corte" and attaches every existing rate to them provisionally.
    /// Equivalent names are unique per category (general services) and among categories, through unique indexes on
    /// the normalized name of the live rows.
    /// </summary>
    public partial class AddGeneralServicesAndCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_categories",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    PublicCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_service_categories", x => x.Id);
                    table.CheckConstraint("ck_service_categories_public_code_format", "\"PublicCode\" ~ '^[a-z0-9]{4,16}$'");
                    table.CheckConstraint("ck_service_categories_sort_order_range", "\"SortOrder\" >= 0 AND \"SortOrder\" <= 9999");
                });

            migrationBuilder.CreateTable(
                name: "general_services",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    PublicCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_general_services", x => x.Id);
                    table.CheckConstraint("ck_general_services_public_code_format", "\"PublicCode\" ~ '^[a-z0-9]{4,16}$'");
                    table.CheckConstraint("ck_general_services_sort_order_range", "\"SortOrder\" >= 0 AND \"SortOrder\" <= 9999");
                    table.ForeignKey(
                        name: "FK_general_services_service_categories_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "public",
                        principalTable: "service_categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "public",
                table: "service_categories",
                columns: new[] { "Id", "CreatedAt", "IsActive", "IsDeleted", "Name", "NormalizedName", "PublicCode", "SortOrder", "UpdatedAt" },
                values: new object[] { new Guid("5f0c2a8e-3b1d-4c7a-9e51-0b6d2f4a8c01"), new DateTime(2026, 11, 6, 0, 0, 0, 0, DateTimeKind.Utc), true, false, "Barbería", "BARBERÍA", "barberia", 0, null });

            migrationBuilder.InsertData(
                schema: "public",
                table: "general_services",
                columns: new[] { "Id", "CategoryId", "CreatedAt", "Description", "IsActive", "IsDeleted", "Name", "NormalizedName", "PublicCode", "SortOrder", "UpdatedAt" },
                values: new object[] { new Guid("8a4e1c27-6d93-4b0f-a2c5-7e19d3b6f402"), new Guid("5f0c2a8e-3b1d-4c7a-9e51-0b6d2f4a8c01"), new DateTime(2026, 11, 6, 0, 0, 0, 0, DateTimeKind.Utc), null, true, false, "Corte", "CORTE", "corte", 0, null });

            // ADR-0016 D4: every existing rate (active, inactive or deleted) is attached to the provisional general
            // service "Corte" of the category "Barbería". Nothing is inferred from names, and only the services table
            // gets the new column: income, service records, memberships, consumptions, gifts, rewards and
            // redemptions are not touched (their ids and snapshots stay as they are).
            migrationBuilder.AddColumn<Guid>(
                name: "GeneralServiceId",
                schema: "public",
                table: "services",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                "UPDATE public.services SET \"GeneralServiceId\" = '8a4e1c27-6d93-4b0f-a2c5-7e19d3b6f402';");

            migrationBuilder.AlterColumn<Guid>(
                name: "GeneralServiceId",
                schema: "public",
                table: "services",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_services_GeneralServiceId",
                schema: "public",
                table: "services",
                column: "GeneralServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_general_services_PublicCode",
                schema: "public",
                table: "general_services",
                column: "PublicCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_general_services_category_normalized_name_live",
                schema: "public",
                table: "general_services",
                columns: new[] { "CategoryId", "NormalizedName" },
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_service_categories_PublicCode",
                schema: "public",
                table: "service_categories",
                column: "PublicCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_service_categories_normalized_name_live",
                schema: "public",
                table: "service_categories",
                column: "NormalizedName",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.AddForeignKey(
                name: "FK_services_general_services_GeneralServiceId",
                schema: "public",
                table: "services",
                column: "GeneralServiceId",
                principalSchema: "public",
                principalTable: "general_services",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_services_general_services_GeneralServiceId",
                schema: "public",
                table: "services");

            migrationBuilder.DropTable(
                name: "general_services",
                schema: "public");

            migrationBuilder.DropTable(
                name: "service_categories",
                schema: "public");

            migrationBuilder.DropIndex(
                name: "IX_services_GeneralServiceId",
                schema: "public",
                table: "services");

            migrationBuilder.DropColumn(
                name: "GeneralServiceId",
                schema: "public",
                table: "services");
        }
    }
}
