using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialDomainModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "public");

            migrationBuilder.CreateTable(
                name: "landing_content",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    HeroTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    HeroSubtitle = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    AboutTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AboutText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ContactPhone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    ContactEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Address = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_landing_content", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "media_assets",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    ContentType = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    PublicUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    Purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_media_assets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.Id);
                    table.CheckConstraint("ck_roles_supported_names", "\"NormalizedName\" IN ('ADMIN', 'STAFF', 'CUSTOMER')");
                });

            migrationBuilder.CreateTable(
                name: "app_branding_settings",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    PrimaryColor = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    SecondaryColor = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    LogoMediaAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    AppIconMediaAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_branding_settings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_app_branding_settings_media_assets_AppIconMediaAssetId",
                        column: x => x.AppIconMediaAssetId,
                        principalSchema: "public",
                        principalTable: "media_assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_app_branding_settings_media_assets_LogoMediaAssetId",
                        column: x => x.LogoMediaAssetId,
                        principalSchema: "public",
                        principalTable: "media_assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "banners",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Subtitle = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ImageMediaAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    LinkUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    StartsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    EndsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_banners", x => x.Id);
                    table.CheckConstraint("ck_banners_schedule_window", "\"StartsAt\" IS NULL OR \"EndsAt\" IS NULL OR \"StartsAt\" < \"EndsAt\"");
                    table.ForeignKey(
                        name: "FK_banners_media_assets_ImageMediaAssetId",
                        column: x => x.ImageMediaAssetId,
                        principalSchema: "public",
                        principalTable: "media_assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FullName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PhoneNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    PasswordHash = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    ProfilePhotoMediaAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                    table.ForeignKey(
                        name: "FK_users_media_assets_ProfilePhotoMediaAssetId",
                        column: x => x.ProfilePhotoMediaAssetId,
                        principalSchema: "public",
                        principalTable: "media_assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReplacedByRefreshTokenId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeviceLabel = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.Id);
                    table.CheckConstraint("ck_refresh_tokens_expiration", "\"ExpiresAt\" > \"CreatedAt\"");
                    table.ForeignKey(
                        name: "FK_refresh_tokens_refresh_tokens_ReplacedByRefreshTokenId",
                        column: x => x.ReplacedByRefreshTokenId,
                        principalSchema: "public",
                        principalTable: "refresh_tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_refresh_tokens_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "staff_profiles",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Bio = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PhoneNumber = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    PhotoMediaAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    TipsQrMediaAssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    DefaultAppointmentDurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_profiles", x => x.Id);
                    table.CheckConstraint("ck_staff_profiles_default_duration", "\"DefaultAppointmentDurationMinutes\" BETWEEN 15 AND 120");
                    table.ForeignKey(
                        name: "FK_staff_profiles_media_assets_PhotoMediaAssetId",
                        column: x => x.PhotoMediaAssetId,
                        principalSchema: "public",
                        principalTable: "media_assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_staff_profiles_media_assets_TipsQrMediaAssetId",
                        column: x => x.TipsQrMediaAssetId,
                        principalSchema: "public",
                        principalTable: "media_assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_staff_profiles_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                schema: "public",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_roles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_user_roles_roles_RoleId",
                        column: x => x.RoleId,
                        principalSchema: "public",
                        principalTable: "roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_roles_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "appointments",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StaffProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CustomerName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    CustomerEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CustomerPhone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    StartsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Source = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_appointments", x => x.Id);
                    table.CheckConstraint("ck_appointments_time_range", "\"StartsAt\" < \"EndsAt\"");
                    table.ForeignKey(
                        name: "FK_appointments_staff_profiles_StaffProfileId",
                        column: x => x.StaffProfileId,
                        principalSchema: "public",
                        principalTable: "staff_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_appointments_users_CustomerUserId",
                        column: x => x.CustomerUserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "staff_availability_rules",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StaffProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    DayOfWeek = table.Column<int>(type: "integer", nullable: false),
                    StartTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    EndTime = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_availability_rules", x => x.Id);
                    table.CheckConstraint("ck_staff_availability_rules_time_range", "\"StartTime\" < \"EndTime\"");
                    table.ForeignKey(
                        name: "FK_staff_availability_rules_staff_profiles_StaffProfileId",
                        column: x => x.StaffProfileId,
                        principalSchema: "public",
                        principalTable: "staff_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "staff_services",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StaffProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_services", x => x.Id);
                    table.CheckConstraint("ck_staff_services_duration", "\"DurationMinutes\" BETWEEN 15 AND 240");
                    table.CheckConstraint("ck_staff_services_price_non_negative", "\"Price\" IS NULL OR \"Price\" >= 0");
                    table.ForeignKey(
                        name: "FK_staff_services_staff_profiles_StaffProfileId",
                        column: x => x.StaffProfileId,
                        principalSchema: "public",
                        principalTable: "staff_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "staff_unavailable_periods",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StaffProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_unavailable_periods", x => x.Id);
                    table.CheckConstraint("ck_staff_unavailable_periods_time_range", "\"StartsAt\" < \"EndsAt\"");
                    table.ForeignKey(
                        name: "FK_staff_unavailable_periods_staff_profiles_StaffProfileId",
                        column: x => x.StaffProfileId,
                        principalSchema: "public",
                        principalTable: "staff_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "reviews",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AppointmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stars = table.Column<int>(type: "integer", nullable: false),
                    Comment = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reviews", x => x.Id);
                    table.CheckConstraint("ck_reviews_stars_range", "\"Stars\" BETWEEN 1 AND 5");
                    table.ForeignKey(
                        name: "FK_reviews_appointments_AppointmentId",
                        column: x => x.AppointmentId,
                        principalSchema: "public",
                        principalTable: "appointments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_reviews_users_CustomerUserId",
                        column: x => x.CustomerUserId,
                        principalSchema: "public",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_branding_settings_AppIconMediaAssetId",
                schema: "public",
                table: "app_branding_settings",
                column: "AppIconMediaAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_app_branding_settings_LogoMediaAssetId",
                schema: "public",
                table: "app_branding_settings",
                column: "LogoMediaAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_CustomerUserId",
                schema: "public",
                table: "appointments",
                column: "CustomerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_StaffProfileId_StartsAt",
                schema: "public",
                table: "appointments",
                columns: new[] { "StaffProfileId", "StartsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_banners_ImageMediaAssetId",
                schema: "public",
                table: "banners",
                column: "ImageMediaAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_banners_IsActive_SortOrder",
                schema: "public",
                table: "banners",
                columns: new[] { "IsActive", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_media_assets_Purpose",
                schema: "public",
                table: "media_assets",
                column: "Purpose");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_ReplacedByRefreshTokenId",
                schema: "public",
                table: "refresh_tokens",
                column: "ReplacedByRefreshTokenId");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_TokenHash",
                schema: "public",
                table: "refresh_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_UserId_ExpiresAt",
                schema: "public",
                table: "refresh_tokens",
                columns: new[] { "UserId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_reviews_AppointmentId",
                schema: "public",
                table: "reviews",
                column: "AppointmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reviews_CustomerUserId",
                schema: "public",
                table: "reviews",
                column: "CustomerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_roles_NormalizedName",
                schema: "public",
                table: "roles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_staff_availability_rules_StaffProfileId_DayOfWeek",
                schema: "public",
                table: "staff_availability_rules",
                columns: new[] { "StaffProfileId", "DayOfWeek" });

            migrationBuilder.CreateIndex(
                name: "IX_staff_profiles_PhotoMediaAssetId",
                schema: "public",
                table: "staff_profiles",
                column: "PhotoMediaAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_staff_profiles_TipsQrMediaAssetId",
                schema: "public",
                table: "staff_profiles",
                column: "TipsQrMediaAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_staff_profiles_UserId",
                schema: "public",
                table: "staff_profiles",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_staff_services_StaffProfileId",
                schema: "public",
                table: "staff_services",
                column: "StaffProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_staff_unavailable_periods_StaffProfileId_StartsAt",
                schema: "public",
                table: "staff_unavailable_periods",
                columns: new[] { "StaffProfileId", "StartsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_user_roles_RoleId",
                schema: "public",
                table: "user_roles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "IX_users_NormalizedEmail",
                schema: "public",
                table: "users",
                column: "NormalizedEmail",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_ProfilePhotoMediaAssetId",
                schema: "public",
                table: "users",
                column: "ProfilePhotoMediaAssetId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_branding_settings",
                schema: "public");

            migrationBuilder.DropTable(
                name: "banners",
                schema: "public");

            migrationBuilder.DropTable(
                name: "landing_content",
                schema: "public");

            migrationBuilder.DropTable(
                name: "refresh_tokens",
                schema: "public");

            migrationBuilder.DropTable(
                name: "reviews",
                schema: "public");

            migrationBuilder.DropTable(
                name: "staff_availability_rules",
                schema: "public");

            migrationBuilder.DropTable(
                name: "staff_services",
                schema: "public");

            migrationBuilder.DropTable(
                name: "staff_unavailable_periods",
                schema: "public");

            migrationBuilder.DropTable(
                name: "user_roles",
                schema: "public");

            migrationBuilder.DropTable(
                name: "appointments",
                schema: "public");

            migrationBuilder.DropTable(
                name: "roles",
                schema: "public");

            migrationBuilder.DropTable(
                name: "staff_profiles",
                schema: "public");

            migrationBuilder.DropTable(
                name: "users",
                schema: "public");

            migrationBuilder.DropTable(
                name: "media_assets",
                schema: "public");
        }
    }
}
