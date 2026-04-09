using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EChat.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartnerEmailToChat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PartnerEmail",
                table: "Chats",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Chats_AccountId_PartnerEmail",
                table: "Chats",
                columns: new[] { "AccountId", "PartnerEmail" });

            // Backfill PartnerEmail for existing 1:1 chats from message history.
            // For each 1:1 chat, find the most recent message whose sender is NOT
            // the account owner — that sender is the chat partner.
            migrationBuilder.Sql(@"
UPDATE Chats
SET PartnerEmail = (
    SELECT m.Sender
    FROM Messages m
    WHERE m.ChatId = Chats.ChatId
      AND m.Sender != Chats.AccountId
      AND m.Sender != ''
    ORDER BY m.Timestamp DESC
    LIMIT 1
)
WHERE Chats.Type = 0
  AND Chats.PartnerEmail IS NULL
  AND Chats.AccountId IS NOT NULL;
            ");

            // For 1:1 chats where we have no incoming messages (only sent messages),
            // infer partner from our own sent messages' recipients.
            // For now, also set PartnerEmail from the chat name if it looks like an email.
            migrationBuilder.Sql(@"
UPDATE Chats
SET PartnerEmail = Chats.Name
WHERE Chats.Type = 0
  AND Chats.PartnerEmail IS NULL
  AND Chats.AccountId IS NOT NULL
  AND Chats.Name LIKE '%@%';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Chats_AccountId_PartnerEmail",
                table: "Chats");

            migrationBuilder.DropColumn(
                name: "PartnerEmail",
                table: "Chats");
        }
    }
}
