using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPasswordResetToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PasswordResetTokenExpiresAt",
                schema: "public",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PasswordResetTokenHash",
                schema: "public",
                table: "users",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_PasswordResetTokenHash",
                schema: "public",
                table: "users",
                column: "PasswordResetTokenHash",
                unique: true,
                filter: "\"PasswordResetTokenHash\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_users_PasswordResetTokenHash",
                schema: "public",
                table: "users");

            migrationBuilder.DropColumn(
                name: "PasswordResetTokenExpiresAt",
                schema: "public",
                table: "users");

            migrationBuilder.DropColumn(
                name: "PasswordResetTokenHash",
                schema: "public",
                table: "users");
        }
    }
}
