using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chat.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMembershipDeliveryProgress : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LastDeliveredMessageId",
                schema: "conversations",
                table: "ConversationMembers",
                type: "uuid",
                nullable: true);

            // Existing durable reads already assert observation of their complete prefix.
            migrationBuilder.Sql("""
                UPDATE conversations."ConversationMembers"
                SET "LastDeliveredMessageId" = "LastReadMessageId"
                WHERE "LastReadMessageId" IS NOT NULL;
                """);

            migrationBuilder.AddColumn<Guid>(
                name: "MembershipId",
                schema: "conversations",
                table: "ConversationMembers",
                type: "uuid",
                nullable: false,
                defaultValueSql: "gen_random_uuid()");

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMembers_ConversationId_LastDeliveredMessageId",
                schema: "conversations",
                table: "ConversationMembers",
                columns: new[] { "ConversationId", "LastDeliveredMessageId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMembers_MembershipId",
                schema: "conversations",
                table: "ConversationMembers",
                column: "MembershipId",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_ConversationMembers_MembershipId",
                schema: "conversations",
                table: "ConversationMembers",
                sql: "\"MembershipId\" <> '00000000-0000-0000-0000-000000000000'::uuid");

            migrationBuilder.AddForeignKey(
                name: "FK_ConversationMembers_Messages_ConversationId_LastDeliveredMe~",
                schema: "conversations",
                table: "ConversationMembers",
                columns: new[] { "ConversationId", "LastDeliveredMessageId" },
                principalSchema: "messages",
                principalTable: "Messages",
                principalColumns: new[] { "ConversationId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConversationMembers_Messages_ConversationId_LastDeliveredMe~",
                schema: "conversations",
                table: "ConversationMembers");

            migrationBuilder.DropIndex(
                name: "IX_ConversationMembers_ConversationId_LastDeliveredMessageId",
                schema: "conversations",
                table: "ConversationMembers");

            migrationBuilder.DropIndex(
                name: "IX_ConversationMembers_MembershipId",
                schema: "conversations",
                table: "ConversationMembers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ConversationMembers_MembershipId",
                schema: "conversations",
                table: "ConversationMembers");

            migrationBuilder.DropColumn(
                name: "LastDeliveredMessageId",
                schema: "conversations",
                table: "ConversationMembers");

            migrationBuilder.DropColumn(
                name: "MembershipId",
                schema: "conversations",
                table: "ConversationMembers");
        }
    }
}
