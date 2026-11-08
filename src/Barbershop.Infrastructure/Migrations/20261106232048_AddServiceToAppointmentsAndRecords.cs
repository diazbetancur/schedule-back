using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    /// <summary>
    /// ADR-0016 B3/B4: appointments gain the general service booked, its name and the reserved duration; service records
    /// gain the registered and the confirmed general service, and the registered rate becomes optional (new records name
    /// a general service). Only nullable columns are added: no existing appointment or record is rewritten.
    /// Rolling back is only possible while every service record still has a registered rate (records registered with a
    /// general service only would have none): the rollback stops with an explicit error instead of inventing one.
    /// </summary>
    public partial class AddServiceToAppointmentsAndRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ServiceId",
                schema: "public",
                table: "service_records",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "ApprovedGeneralServiceId",
                schema: "public",
                table: "service_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "GeneralServiceId",
                schema: "public",
                table: "service_records",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "GeneralServiceId",
                schema: "public",
                table: "appointments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GeneralServiceNameSnapshot",
                schema: "public",
                table: "appointments",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReservedDurationMinutes",
                schema: "public",
                table: "appointments",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_service_records_ApprovedGeneralServiceId",
                schema: "public",
                table: "service_records",
                column: "ApprovedGeneralServiceId");

            migrationBuilder.CreateIndex(
                name: "IX_service_records_GeneralServiceId",
                schema: "public",
                table: "service_records",
                column: "GeneralServiceId");

            migrationBuilder.AddCheckConstraint(
                name: "ck_service_records_registered_service",
                schema: "public",
                table: "service_records",
                sql: "\"ServiceId\" IS NOT NULL OR \"GeneralServiceId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_GeneralServiceId",
                schema: "public",
                table: "appointments",
                column: "GeneralServiceId");

            migrationBuilder.AddCheckConstraint(
                name: "ck_appointments_reserved_service",
                schema: "public",
                table: "appointments",
                sql: "(\"GeneralServiceId\" IS NULL AND \"GeneralServiceNameSnapshot\" IS NULL AND \"ReservedDurationMinutes\" IS NULL) OR (\"GeneralServiceId\" IS NOT NULL AND \"GeneralServiceNameSnapshot\" IS NOT NULL AND \"ReservedDurationMinutes\" IS NOT NULL AND \"ReservedDurationMinutes\" >= 10 AND \"ReservedDurationMinutes\" <= 240 AND \"EndsAt\" = \"StartsAt\" + make_interval(mins => \"ReservedDurationMinutes\"))");

            migrationBuilder.AddForeignKey(
                name: "FK_appointments_general_services_GeneralServiceId",
                schema: "public",
                table: "appointments",
                column: "GeneralServiceId",
                principalSchema: "public",
                principalTable: "general_services",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_service_records_general_services_ApprovedGeneralServiceId",
                schema: "public",
                table: "service_records",
                column: "ApprovedGeneralServiceId",
                principalSchema: "public",
                principalTable: "general_services",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_service_records_general_services_GeneralServiceId",
                schema: "public",
                table: "service_records",
                column: "GeneralServiceId",
                principalSchema: "public",
                principalTable: "general_services",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_appointments_general_services_GeneralServiceId",
                schema: "public",
                table: "appointments");

            migrationBuilder.DropForeignKey(
                name: "FK_service_records_general_services_ApprovedGeneralServiceId",
                schema: "public",
                table: "service_records");

            migrationBuilder.DropForeignKey(
                name: "FK_service_records_general_services_GeneralServiceId",
                schema: "public",
                table: "service_records");

            migrationBuilder.DropIndex(
                name: "IX_service_records_ApprovedGeneralServiceId",
                schema: "public",
                table: "service_records");

            migrationBuilder.DropIndex(
                name: "IX_service_records_GeneralServiceId",
                schema: "public",
                table: "service_records");

            migrationBuilder.DropCheckConstraint(
                name: "ck_service_records_registered_service",
                schema: "public",
                table: "service_records");

            migrationBuilder.DropIndex(
                name: "IX_appointments_GeneralServiceId",
                schema: "public",
                table: "appointments");

            migrationBuilder.DropCheckConstraint(
                name: "ck_appointments_reserved_service",
                schema: "public",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "ApprovedGeneralServiceId",
                schema: "public",
                table: "service_records");

            migrationBuilder.DropColumn(
                name: "GeneralServiceId",
                schema: "public",
                table: "service_records");

            migrationBuilder.DropColumn(
                name: "GeneralServiceId",
                schema: "public",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "GeneralServiceNameSnapshot",
                schema: "public",
                table: "appointments");

            migrationBuilder.DropColumn(
                name: "ReservedDurationMinutes",
                schema: "public",
                table: "appointments");

            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM public.service_records WHERE "ServiceId" IS NULL) THEN
                        RAISE EXCEPTION 'Cannot roll back AddServiceToAppointmentsAndRecords: some service records were registered with a general service only and have no rate.';
                    END IF;
                END $$;
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "ServiceId",
                schema: "public",
                table: "service_records",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
