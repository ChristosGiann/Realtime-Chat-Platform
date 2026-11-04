using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chat.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClientMessageId",
                schema: "messages",
                table: "Messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql("UPDATE messages.\"Messages\" SET \"ClientMessageId\" = gen_random_uuid()");
            migrationBuilder.AlterColumn<Guid>(name: "ClientMessageId", schema: "messages", table: "Messages",
                type: "uuid", nullable: false, oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_Messages_ClientMessageId",
                schema: "messages",
                table: "Messages",
                column: "ClientMessageId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Messages_ClientMessageId",
                schema: "messages",
                table: "Messages",
                sql: "\"ClientMessageId\" <> '00000000-0000-0000-0000-000000000000'::uuid");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_Messages_ClientMessageId",
                schema: "messages",
                table: "Messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Messages_ClientMessageId",
                schema: "messages",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "ClientMessageId",
                schema: "messages",
                table: "Messages");
        }
    }
}
