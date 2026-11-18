using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBillChargeSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ChargedPaymentMethodId",
                schema: "public",
                table: "bills",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChargedPaymentMethodName",
                schema: "public",
                table: "bills",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ChargedTotal",
                schema: "public",
                table: "bills",
                type: "integer",
                nullable: true);

            // SONDA BUG-121: fill the copy of the charge only where bill_events tells it without ambiguity. Rows that
            // cannot be rebuilt stay NULL, and the cash closing keeps using their current total and payment method.
            // 1) The total: from the single «Closed» event of the bill (its afterJson keeps the total charged).
            migrationBuilder.Sql("""
                UPDATE public.bills b
                SET "ChargedTotal" = (e."AfterJson" ->> 'total')::integer
                FROM public.bill_events e
                WHERE e."BillId" = b."Id" AND e."Type" = 'Closed' AND b."ClosedAt" IS NOT NULL
                  AND (e."AfterJson" ->> 'total') ~ '^-?[0-9]{1,9}$'
                  AND (SELECT count(*) FROM public.bill_events c WHERE c."BillId" = b."Id" AND c."Type" = 'Closed') = 1;
                """);

            // 2) The payment method, when it was never changed after the close: the current one is the charged one.
            migrationBuilder.Sql("""
                UPDATE public.bills b
                SET "ChargedPaymentMethodId" = b."PaymentMethodId", "ChargedPaymentMethodName" = b."PaymentMethodNameSnapshot"
                WHERE b."ChargedTotal" IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM public.bill_events c WHERE c."BillId" = b."Id" AND c."Type" = 'PaymentMethodChanged');
                """);

            // 3) Changed after the close: the method named in the «Closed» event, only when exactly one method has that name.
            migrationBuilder.Sql("""
                UPDATE public.bills b
                SET "ChargedPaymentMethodId" = pm."Id", "ChargedPaymentMethodName" = pm."Name"
                FROM public.bill_events e
                JOIN public.payment_methods pm ON lower(pm."Name") = lower(e."AfterJson" ->> 'paymentMethod')
                WHERE e."BillId" = b."Id" AND e."Type" = 'Closed'
                  AND b."ChargedTotal" IS NOT NULL AND b."ChargedPaymentMethodId" IS NULL
                  AND EXISTS (SELECT 1 FROM public.bill_events c WHERE c."BillId" = b."Id" AND c."Type" = 'PaymentMethodChanged')
                  AND (SELECT count(*) FROM public.payment_methods p2 WHERE lower(p2."Name") = lower(e."AfterJson" ->> 'paymentMethod')) = 1;
                """);

            // 4) A charge whose method could not be rebuilt is left entirely as it was (no half copy).
            migrationBuilder.Sql("""
                UPDATE public.bills SET "ChargedTotal" = NULL WHERE "ChargedTotal" > 0 AND "ChargedPaymentMethodId" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChargedPaymentMethodId",
                schema: "public",
                table: "bills");

            migrationBuilder.DropColumn(
                name: "ChargedPaymentMethodName",
                schema: "public",
                table: "bills");

            migrationBuilder.DropColumn(
                name: "ChargedTotal",
                schema: "public",
                table: "bills");
        }
    }
}
