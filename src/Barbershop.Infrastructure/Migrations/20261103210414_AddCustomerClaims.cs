using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_claims",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "uuid", nullable: false),
                    FullName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Phone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    NormalizedPhone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ApprovedCustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DecisionNotes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_claims", x => x.Id);
                    table.CheckConstraint("ck_customer_claims_status_shape", "(\"Status\" = 'Pending' AND \"ApprovedCustomerId\" IS NULL AND \"DecidedAt\" IS NULL AND \"DecidedByUserId\" IS NULL) OR (\"Status\" = 'Approved' AND \"ApprovedCustomerId\" IS NOT NULL AND \"DecidedAt\" IS NOT NULL AND \"DecidedByUserId\" IS NOT NULL) OR (\"Status\" = 'Rejected' AND \"ApprovedCustomerId\" IS NULL AND \"DecidedAt\" IS NOT NULL AND \"DecidedByUserId\" IS NOT NULL AND \"DecisionNotes\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_customer_claims_customers_ApprovedCustomerId",
                        column: x => x.ApprovedCustomerId,
                        principalSchema: "public",
                        principalTable: "customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_claims_users_DecidedByUserId",
                        column: x => x.DecidedByUserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_customer_claims_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_claims_ApprovedCustomerId",
                schema: "public",
                table: "customer_claims",
                column: "ApprovedCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_customer_claims_DecidedByUserId",
                schema: "public",
                table: "customer_claims",
                column: "DecidedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_customer_claims_IdempotencyKey",
                schema: "public",
                table: "customer_claims",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customer_claims_Status_SubmittedAt",
                schema: "public",
                table: "customer_claims",
                columns: new[] { "Status", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_customer_claims_UserId_pending",
                schema: "public",
                table: "customer_claims",
                column: "UserId",
                unique: true,
                filter: "\"Status\" = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "IX_customer_claims_UserId_SubmittedAt",
                schema: "public",
                table: "customer_claims",
                columns: new[] { "UserId", "SubmittedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_claims",
                schema: "public");
        }
    }
}
