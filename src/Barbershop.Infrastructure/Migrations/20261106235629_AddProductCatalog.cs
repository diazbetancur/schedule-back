using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "product_categories",
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
                    table.PrimaryKey("PK_product_categories", x => x.Id);
                    table.CheckConstraint("ck_product_categories_public_code_format", "\"PublicCode\" ~ '^[a-z0-9]{4,16}$'");
                    table.CheckConstraint("ck_product_categories_sort_order_range", "\"SortOrder\" >= 0 AND \"SortOrder\" <= 9999");
                });

            migrationBuilder.CreateTable(
                name: "products",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Price = table.Column<int>(type: "integer", nullable: false),
                    ImageMediaAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    PublicCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_products", x => x.Id);
                    table.CheckConstraint("ck_products_price_non_negative", "\"Price\" >= 0");
                    table.CheckConstraint("ck_products_public_code_format", "\"PublicCode\" ~ '^[a-z0-9]{4,16}$'");
                    table.CheckConstraint("ck_products_sort_order_range", "\"SortOrder\" >= 0 AND \"SortOrder\" <= 9999");
                    table.ForeignKey(
                        name: "FK_products_media_assets_ImageMediaAssetId",
                        column: x => x.ImageMediaAssetId,
                        principalSchema: "public",
                        principalTable: "media_assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_products_product_categories_CategoryId",
                        column: x => x.CategoryId,
                        principalSchema: "public",
                        principalTable: "product_categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_product_categories_PublicCode",
                schema: "public",
                table: "product_categories",
                column: "PublicCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_product_categories_normalized_name_live",
                schema: "public",
                table: "product_categories",
                column: "NormalizedName",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_products_ImageMediaAssetId",
                schema: "public",
                table: "products",
                column: "ImageMediaAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_products_PublicCode",
                schema: "public",
                table: "products",
                column: "PublicCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_products_category_normalized_name_live",
                schema: "public",
                table: "products",
                columns: new[] { "CategoryId", "NormalizedName" },
                unique: true,
                filter: "\"IsDeleted\" = false");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "products",
                schema: "public");

            migrationBuilder.DropTable(
                name: "product_categories",
                schema: "public");
        }
    }
}
