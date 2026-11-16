using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RetireServiceRecordRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADR-0020 stage 6 (BUG-091, part 2), authorized by the user: the old registration of services performed
            // answers 410 since stage 2, so no role keeps service-records.register (Staff, Admin and any role created by
            // the administration). NOT additive: it deletes only the role_permissions rows of that permission; the
            // permission itself stays in the catalog while records registered before wait for review, and nothing else
            // is touched. The seed never grants it again (ADR-0008, and it is no longer a Staff default).
            migrationBuilder.Sql("""
                DELETE FROM public.role_permissions
                WHERE "PermissionId" IN (SELECT "Id" FROM public.permissions WHERE "Code" = 'service-records.register');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The deleted grants are not restored: which roles held the permission is not known any more. To give it
            // back, grant it again by hand from «Roles» (or restore a backup).
        }
    }
}
