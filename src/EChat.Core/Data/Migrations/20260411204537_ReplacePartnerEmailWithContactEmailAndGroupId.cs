using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EChat.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplacePartnerEmailWithContactEmailAndGroupId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Chats_AccountId_PartnerEmail",
                table: "Chats");

            migrationBuilder.AddColumn<string>(
                name: "ContactEmail",
                table: "Chats",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GroupId",
                table: "Chats",
                type: "TEXT",
                nullable: true);

            migrationBuilder.DropColumn(
                name: "PartnerEmail",
                table: "Chats");

            migrationBuilder.CreateIndex(
                name: "IX_Chats_AccountId_ContactEmail",
                table: "Chats",
                columns: new[] { "AccountId", "ContactEmail" });

            migrationBuilder.CreateIndex(
                name: "IX_Chats_ContactEmail",
                table: "Chats",
                column: "ContactEmail");

            migrationBuilder.CreateIndex(
                name: "IX_Chats_GroupId",
                table: "Chats",
                column: "GroupId");

            migrationBuilder.AddForeignKey(
                name: "FK_Chats_Contacts_ContactEmail",
                table: "Chats",
                column: "ContactEmail",
                principalTable: "Contacts",
                principalColumn: "Email",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Chats_Groups_GroupId",
                table: "Chats",
                column: "GroupId",
                principalTable: "Groups",
                principalColumn: "GroupId",
                onDelete: ReferentialAction.Restrict);

            // Backfill MUST run after DropColumn because SQLite rebuilds the table on DropColumn,
            // which would wipe out data written before the rebuild.
            // Group chats: ChatId was the GroupId conventionally, copy it.
            migrationBuilder.Sql(
                "UPDATE Chats SET GroupId = ChatId WHERE Type = 1 AND GroupId IS NULL");

            // 1:1 chats: match chat Name against Contact DisplayName or Email to fill ContactEmail.
            migrationBuilder.Sql(
                "UPDATE Chats SET ContactEmail = (SELECT c.Email FROM Contacts c WHERE c.DisplayName = Chats.Name OR c.Email = Chats.Name LIMIT 1) WHERE Type = 0 AND ContactEmail IS NULL AND (SELECT c.Email FROM Contacts c WHERE c.DisplayName = Chats.Name OR c.Email = Chats.Name LIMIT 1) IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Chats_Contacts_ContactEmail",
                table: "Chats");

            migrationBuilder.DropForeignKey(
                name: "FK_Chats_Groups_GroupId",
                table: "Chats");

            migrationBuilder.DropIndex(
                name: "IX_Chats_AccountId_ContactEmail",
                table: "Chats");

            migrationBuilder.DropIndex(
                name: "IX_Chats_ContactEmail",
                table: "Chats");

            migrationBuilder.DropIndex(
                name: "IX_Chats_GroupId",
                table: "Chats");

            migrationBuilder.DropColumn(
                name: "ContactEmail",
                table: "Chats");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "Chats");

            migrationBuilder.AddColumn<string>(
                name: "PartnerEmail",
                table: "Chats",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Chats_AccountId_PartnerEmail",
                table: "Chats",
                columns: new[] { "AccountId", "PartnerEmail" });
        }
    }
}