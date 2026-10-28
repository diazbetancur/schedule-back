using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customers",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FullName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    NormalizedPhone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    LinkedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LinkedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    MergedIntoCustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    MergedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MergedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_customers_customers_MergedIntoCustomerId",
                        column: x => x.MergedIntoCustomerId,
                        principalSchema: "public",
                        principalTable: "customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customers_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "customer_events",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RelatedCustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    PerformedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PerformedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_customer_events_customers_CustomerId",
                        column: x => x.CustomerId,
                        principalSchema: "public",
                        principalTable: "customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_events_CustomerId_PerformedAt",
                schema: "public",
                table: "customer_events",
                columns: new[] { "CustomerId", "PerformedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_customers_MergedIntoCustomerId",
                schema: "public",
                table: "customers",
                column: "MergedIntoCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_customers_NormalizedEmail",
                schema: "public",
                table: "customers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "IX_customers_NormalizedPhone",
                schema: "public",
                table: "customers",
                column: "NormalizedPhone");

            migrationBuilder.CreateIndex(
                name: "IX_customers_UserId",
                schema: "public",
                table: "customers",
                column: "UserId",
                unique: true,
                filter: "\"UserId\" IS NOT NULL AND \"MergedIntoCustomerId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_events",
                schema: "public");

            migrationBuilder.DropTable(
                name: "customers",
                schema: "public");
        }
    }
}
