using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EChat.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class MessageIdPerAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite cannot alter primary keys, so we recreate the Messages table.
            // Existing rows keep MessageId as their Id value (already unique globally).
            migrationBuilder.Sql(@"
CREATE TABLE ""Messages_new"" (
    ""Id"" TEXT NOT NULL CONSTRAINT ""PK_Messages"" PRIMARY KEY,
    ""MessageId"" TEXT NOT NULL,
    ""ChatId"" TEXT NOT NULL,
    ""Sender"" TEXT NOT NULL,
    ""Content"" TEXT NOT NULL,
    ""Timestamp"" TEXT NOT NULL,
    ""DisplayTimestamp"" TEXT NOT NULL,
    ""ReceivedAt"" TEXT NOT NULL,
    ""Encrypted"" INTEGER NOT NULL,
    ""AttachmentPath"" TEXT NULL,
    ""InReplyTo"" TEXT NULL,
    ""IsEdited"" INTEGER NOT NULL,
    ""EditVersion"" INTEGER NOT NULL,
    ""ClockSkewDetected"" INTEGER NOT NULL,
    CONSTRAINT ""FK_Messages_Chats_ChatId"" FOREIGN KEY (""ChatId"") REFERENCES ""Chats"" (""ChatId"") ON DELETE CASCADE
);");

            migrationBuilder.Sql(@"
INSERT INTO ""Messages_new"" (
    ""Id"", ""MessageId"", ""ChatId"", ""Sender"", ""Content"",
    ""Timestamp"", ""DisplayTimestamp"", ""ReceivedAt"", ""Encrypted"",
    ""AttachmentPath"", ""InReplyTo"", ""IsEdited"", ""EditVersion"", ""ClockSkewDetected"")
SELECT
    ""MessageId"", ""MessageId"", ""ChatId"", ""Sender"", ""Content"",
    ""Timestamp"", ""DisplayTimestamp"", ""ReceivedAt"", ""Encrypted"",
    ""AttachmentPath"", ""InReplyTo"", ""IsEdited"", ""EditVersion"", ""ClockSkewDetected""
FROM ""Messages"";");

            migrationBuilder.Sql(@"DROP TABLE ""Messages"";");
            migrationBuilder.Sql(@"ALTER TABLE ""Messages_new"" RENAME TO ""Messages"";");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ChatId_Timestamp",
                table: "Messages",
                columns: new[] { "ChatId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_MessageId",
                table: "Messages",
                column: "MessageId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_MessageId_ChatId",
                table: "Messages",
                columns: new[] { "MessageId", "ChatId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Sender",
                table: "Messages",
                column: "Sender");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
CREATE TABLE ""Messages_old"" (
    ""MessageId"" TEXT NOT NULL CONSTRAINT ""PK_Messages"" PRIMARY KEY,
    ""ChatId"" TEXT NOT NULL,
    ""Sender"" TEXT NOT NULL,
    ""Content"" TEXT NOT NULL,
    ""Timestamp"" TEXT NOT NULL,
    ""DisplayTimestamp"" TEXT NOT NULL,
    ""ReceivedAt"" TEXT NOT NULL,
    ""Encrypted"" INTEGER NOT NULL,
    ""AttachmentPath"" TEXT NULL,
    ""InReplyTo"" TEXT NULL,
    ""IsEdited"" INTEGER NOT NULL,
    ""EditVersion"" INTEGER NOT NULL,
    ""ClockSkewDetected"" INTEGER NOT NULL,
    CONSTRAINT ""FK_Messages_Chats_ChatId"" FOREIGN KEY (""ChatId"") REFERENCES ""Chats"" (""ChatId"") ON DELETE CASCADE
);");

            migrationBuilder.Sql(@"
INSERT OR IGNORE INTO ""Messages_old"" (
    ""MessageId"", ""ChatId"", ""Sender"", ""Content"",
    ""Timestamp"", ""DisplayTimestamp"", ""ReceivedAt"", ""Encrypted"",
    ""AttachmentPath"", ""InReplyTo"", ""IsEdited"", ""EditVersion"", ""ClockSkewDetected"")
SELECT
    ""MessageId"", ""ChatId"", ""Sender"", ""Content"",
    ""Timestamp"", ""DisplayTimestamp"", ""ReceivedAt"", ""Encrypted"",
    ""AttachmentPath"", ""InReplyTo"", ""IsEdited"", ""EditVersion"", ""ClockSkewDetected""
FROM ""Messages"";");

            migrationBuilder.Sql(@"DROP TABLE ""Messages"";");
            migrationBuilder.Sql(@"ALTER TABLE ""Messages_old"" RENAME TO ""Messages"";");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ChatId_Timestamp",
                table: "Messages",
                columns: new[] { "ChatId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Sender",
                table: "Messages",
                column: "Sender");
        }
    }
}
