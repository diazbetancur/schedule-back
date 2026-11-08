using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// ADR-0016 B2: the general services each professional offers and their duration (staff_service_offerings), seeded
    /// with "Corte" at each professional's default duration (F2). Rolling back drops the table and its rows.
    /// </summary>
    public partial class AddStaffServiceOfferings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "staff_service_offerings",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StaffProfileId = table.Column<Guid>(type: "uuid", nullable: false),
                    GeneralServiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DurationMinutes = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_service_offerings", x => x.Id);
                    table.CheckConstraint("ck_staff_service_offerings_duration_range", "\"DurationMinutes\" >= 10 AND \"DurationMinutes\" <= 240");
                    table.ForeignKey(
                        name: "FK_staff_service_offerings_general_services_GeneralServiceId",
                        column: x => x.GeneralServiceId,
                        principalSchema: "public",
                        principalTable: "general_services",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_staff_service_offerings_staff_profiles_StaffProfileId",
                        column: x => x.StaffProfileId,
                        principalSchema: "public",
                        principalTable: "staff_profiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_staff_service_offerings_GeneralServiceId",
                schema: "public",
                table: "staff_service_offerings",
                column: "GeneralServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_staff_service_offerings_StaffProfileId_GeneralServiceId",
                schema: "public",
                table: "staff_service_offerings",
                columns: new[] { "StaffProfileId", "GeneralServiceId" },
                unique: true);

            // ADR-0016 F2: every existing professional (active or not) offers the provisional general service "Corte"
            // with their current default duration (kept within 10-240), so booking keeps working after the deploy until
            // administration configures the real offering. Nothing else is touched: appointments, staff profiles and
            // the legacy staff_services table stay as they are. The id is derived from the professional's id, so it is
            // deterministic.
            migrationBuilder.Sql(
                """
                INSERT INTO public.staff_service_offerings
                    ("Id", "StaffProfileId", "GeneralServiceId", "DurationMinutes", "IsActive", "CreatedAt", "UpdatedAt")
                SELECT md5('staff-service-offering:' || sp."Id"::text)::uuid,
                       sp."Id",
                       '8a4e1c27-6d93-4b0f-a2c5-7e19d3b6f402'::uuid,
                       LEAST(GREATEST(sp."DefaultAppointmentDurationMinutes", 10), 240),
                       true,
                       now(),
                       NULL
                FROM public.staff_profiles sp;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "staff_service_offerings",
                schema: "public");
        }
    }
}
