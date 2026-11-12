using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Barbershop.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRefreshTokenSessionId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                schema: "public",
                table: "refresh_tokens",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            // Existing tokens keep their sessions (BUG-013): every token of a rotation chain A -> B -> C gets the id
            // of the chain's first token, so presenting any of them later (e.g. logging out with a stale A) reaches
            // the active C. Chains are linear (a token is replaced at most once and by a new token), so walking
            // from each first token covers every row exactly once; independent sessions stay separate.
            migrationBuilder.Sql("""
                WITH RECURSIVE chain AS (
                    SELECT t."Id", t."Id" AS "RootId"
                    FROM public.refresh_tokens t
                    WHERE NOT EXISTS (
                        SELECT 1 FROM public.refresh_tokens previous
                        WHERE previous."ReplacedByRefreshTokenId" = t."Id")
                    UNION ALL
                    SELECT successor."Id", chain."RootId"
                    FROM chain
                    JOIN public.refresh_tokens current ON current."Id" = chain."Id"
                    JOIN public.refresh_tokens successor ON successor."Id" = current."ReplacedByRefreshTokenId"
                )
                UPDATE public.refresh_tokens r
                SET "SessionId" = chain."RootId"
                FROM chain
                WHERE r."Id" = chain."Id";
                """);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_SessionId",
                schema: "public",
                table: "refresh_tokens",
                column: "SessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_refresh_tokens_SessionId",
                schema: "public",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "SessionId",
                schema: "public",
                table: "refresh_tokens");
        }
    }
}
