using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chat.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddConversationPersistence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "conversations");

            migrationBuilder.CreateTable(
                name: "Conversations",
                schema: "conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    DirectUserLowId = table.Column<Guid>(type: "uuid", nullable: true),
                    DirectUserHighId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.CheckConstraint("CK_Conversations_CreatedAt", "isfinite(\"CreatedAt\")");
                    table.CheckConstraint("CK_Conversations_Id", "\"Id\" <> '00000000-0000-0000-0000-000000000000'::uuid");
                    table.CheckConstraint("CK_Conversations_Shape", "(\"Type\" = 1 AND \"Name\" IS NULL AND \"DirectUserLowId\" IS NOT NULL\n    AND \"DirectUserHighId\" IS NOT NULL AND \"DirectUserLowId\" < \"DirectUserHighId\")\nOR\n(\"Type\" = 2 AND \"Name\" IS NOT NULL AND \"Name\" ~ '[^[:space:]]'\n    AND \"DirectUserLowId\" IS NULL AND \"DirectUserHighId\" IS NULL)");
                    table.CheckConstraint("CK_Conversations_Type", "\"Type\" IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_Conversations_AspNetUsers_DirectUserHighId",
                        column: x => x.DirectUserHighId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Conversations_AspNetUsers_DirectUserLowId",
                        column: x => x.DirectUserLowId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConversationMembers",
                schema: "conversations",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationMembers", x => new { x.ConversationId, x.UserId });
                    table.CheckConstraint("CK_ConversationMembers_JoinedAt", "isfinite(\"JoinedAt\")");
                    table.CheckConstraint("CK_ConversationMembers_Role", "\"Role\" IN (1, 2)");
                    table.ForeignKey(
                        name: "FK_ConversationMembers_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ConversationMembers_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalSchema: "conversations",
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMembers_UserId_ConversationId",
                schema: "conversations",
                table: "ConversationMembers",
                columns: new[] { "UserId", "ConversationId" });

            migrationBuilder.CreateIndex(
                name: "UX_ConversationMembers_Owner",
                schema: "conversations",
                table: "ConversationMembers",
                column: "ConversationId",
                unique: true,
                filter: "\"Role\" = 2");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_DirectUserHighId",
                schema: "conversations",
                table: "Conversations",
                column: "DirectUserHighId");

            migrationBuilder.CreateIndex(
                name: "UX_Conversations_DirectPair",
                schema: "conversations",
                table: "Conversations",
                columns: new[] { "DirectUserLowId", "DirectUserHighId" },
                unique: true,
                filter: "\"Type\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationMembers",
                schema: "conversations");

            migrationBuilder.DropTable(
                name: "Conversations",
                schema: "conversations");
        }
    }
}
