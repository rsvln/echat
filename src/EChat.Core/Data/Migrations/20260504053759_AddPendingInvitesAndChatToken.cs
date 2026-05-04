using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EChat.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingInvitesAndChatToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PendingOutgoingInviteToken",
                table: "Chats",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PendingInvites",
                columns: table => new
                {
                    TokenId = table.Column<string>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", nullable: false),
                    AccountId = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UsedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Label = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingInvites", x => x.TokenId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingInvites_AccountId_UsedAt_ExpiresAt",
                table: "PendingInvites",
                columns: new[] { "AccountId", "UsedAt", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingInvites_TokenHash",
                table: "PendingInvites",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingInvites");

            migrationBuilder.DropColumn(
                name: "PendingOutgoingInviteToken",
                table: "Chats");
        }
    }
}
