using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chat.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMembershipReadState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LastReadMessageId",
                schema: "conversations",
                table: "ConversationMembers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Messages_ConversationId_Id",
                schema: "messages",
                table: "Messages",
                columns: new[] { "ConversationId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMembers_ConversationId_LastReadMessageId",
                schema: "conversations",
                table: "ConversationMembers",
                columns: new[] { "ConversationId", "LastReadMessageId" });

            migrationBuilder.AddForeignKey(
                name: "FK_ConversationMembers_Messages_ConversationId_LastReadMessage~",
                schema: "conversations",
                table: "ConversationMembers",
                columns: new[] { "ConversationId", "LastReadMessageId" },
                principalSchema: "messages",
                principalTable: "Messages",
                principalColumns: new[] { "ConversationId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ConversationMembers_Messages_ConversationId_LastReadMessage~",
                schema: "conversations",
                table: "ConversationMembers");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Messages_ConversationId_Id",
                schema: "messages",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_ConversationMembers_ConversationId_LastReadMessageId",
                schema: "conversations",
                table: "ConversationMembers");

            migrationBuilder.DropColumn(
                name: "LastReadMessageId",
                schema: "conversations",
                table: "ConversationMembers");
        }
    }
}
