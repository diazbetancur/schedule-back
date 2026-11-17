using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inventory_movements",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    OnHandAfter = table.Column<int>(type: "integer", nullable: false),
                    AverageUnitCostAfter = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    OccurredOn = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Supplier = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Reference = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    ExpenseEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdempotencyKey = table.Column<Guid>(type: "uuid", nullable: false),
                    PerformedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inventory_movements", x => x.Id);
                    table.CheckConstraint("ck_inventory_movements_quantity_positive", "\"Quantity\" > 0");
                    table.CheckConstraint("ck_inventory_movements_shape", "(\"Type\" = 'Receipt' AND \"UnitCost\" IS NOT NULL AND \"Reason\" IS NULL) OR (\"Type\" IN ('AdjustmentIn', 'AdjustmentOut') AND \"Reason\" IS NOT NULL AND \"ExpenseEntryId\" IS NULL) OR (\"Type\" IN ('Sale', 'SaleReversal') AND \"ExpenseEntryId\" IS NULL)");
                    table.CheckConstraint("ck_inventory_movements_unit_cost_non_negative", "\"UnitCost\" IS NULL OR \"UnitCost\" >= 0");
                    table.ForeignKey(
                        name: "FK_inventory_movements_expense_entries_ExpenseEntryId",
                        column: x => x.ExpenseEntryId,
                        principalSchema: "public",
                        principalTable: "expense_entries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_movements_products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "public",
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inventory_movements_users_PerformedByUserId",
                        column: x => x.PerformedByUserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_stock",
                schema: "public",
                columns: table => new
                {
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    OnHand = table.Column<int>(type: "integer", nullable: false),
                    AverageUnitCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    MinStock = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_stock", x => x.ProductId);
                    table.CheckConstraint("ck_product_stock_average_cost_non_negative", "\"AverageUnitCost\" IS NULL OR \"AverageUnitCost\" >= 0");
                    table.CheckConstraint("ck_product_stock_min_stock_range", "\"MinStock\" IS NULL OR (\"MinStock\" >= 0 AND \"MinStock\" <= 100000)");
                    table.ForeignKey(
                        name: "FK_product_stock_products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "public",
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_ExpenseEntryId",
                schema: "public",
                table: "inventory_movements",
                column: "ExpenseEntryId",
                unique: true,
                filter: "\"ExpenseEntryId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_IdempotencyKey",
                schema: "public",
                table: "inventory_movements",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_PerformedByUserId",
                schema: "public",
                table: "inventory_movements",
                column: "PerformedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_inventory_movements_ProductId_CreatedAt",
                schema: "public",
                table: "inventory_movements",
                columns: new[] { "ProductId", "CreatedAt" });

            // ADR-0020: the stock card is append-only; a correction is a new movement, never an edit.
            // Existing products get no rows: no stock row means 0 units and no known cost (nothing is made up).
            migrationBuilder.Sql("""
                CREATE FUNCTION public.inventory_movements_are_append_only() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'inventory movements (%) cannot be updated or deleted', TG_TABLE_NAME
                        USING ERRCODE = 'restrict_violation';
                END;
                $$;

                CREATE TRIGGER trg_inventory_movements_append_only
                    BEFORE UPDATE OR DELETE ON public.inventory_movements
                    FOR EACH ROW EXECUTE FUNCTION public.inventory_movements_are_append_only();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inventory_movements",
                schema: "public");

            migrationBuilder.DropTable(
                name: "product_stock",
                schema: "public");

            migrationBuilder.Sql("DROP FUNCTION public.inventory_movements_are_append_only();");
        }
    }
}
