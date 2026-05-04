using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EChat.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropChatPriority : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Priority",
                table: "Chats");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "Chats",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }
    }
}
