using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ExpandStaffProfileDefaultDurationRange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_staff_profiles_default_duration",
                schema: "public",
                table: "staff_profiles");

            migrationBuilder.AddCheckConstraint(
                name: "ck_staff_profiles_default_duration",
                schema: "public",
                table: "staff_profiles",
                sql: "\"DefaultAppointmentDurationMinutes\" BETWEEN 10 AND 240");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_staff_profiles_default_duration",
                schema: "public",
                table: "staff_profiles");

            migrationBuilder.AddCheckConstraint(
                name: "ck_staff_profiles_default_duration",
                schema: "public",
                table: "staff_profiles",
                sql: "\"DefaultAppointmentDurationMinutes\" BETWEEN 15 AND 120");
        }
    }
}
