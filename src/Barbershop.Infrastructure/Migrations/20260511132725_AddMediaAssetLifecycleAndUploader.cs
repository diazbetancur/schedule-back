using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddMediaAssetLifecycleAndUploader : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FailureReason",
                schema: "public",
                table: "media_assets",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                schema: "public",
                table: "media_assets",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                schema: "public",
                table: "media_assets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UploadedByUserId",
                schema: "public",
                table: "media_assets",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_Status",
                schema: "public",
                table: "media_assets",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_UploadedByUserId",
                schema: "public",
                table: "media_assets",
                column: "UploadedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_media_assets_users_UploadedByUserId",
                schema: "public",
                table: "media_assets",
                column: "UploadedByUserId",
                principalSchema: "public",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_media_assets_users_UploadedByUserId",
                schema: "public",
                table: "media_assets");

            migrationBuilder.DropIndex(
                name: "IX_media_assets_Status",
                schema: "public",
                table: "media_assets");

            migrationBuilder.DropIndex(
                name: "IX_media_assets_UploadedByUserId",
                schema: "public",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "FailureReason",
                schema: "public",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "public",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                schema: "public",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "UploadedByUserId",
                schema: "public",
                table: "media_assets");
        }
    }
}
