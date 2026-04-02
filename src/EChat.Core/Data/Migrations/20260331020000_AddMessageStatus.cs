using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EChat.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // DEFAULT 1 = MessageStatus.Sent — existing rows are already sent
            migrationBuilder.Sql(
                "ALTER TABLE Messages ADD COLUMN Status INTEGER NOT NULL DEFAULT 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // SQLite doesn't support DROP COLUMN on older versions;
            // recreate the table without the column
            migrationBuilder.Sql(@"
                CREATE TABLE Messages_backup AS SELECT
                    Id, MessageId, ChatId, Sender, Content,
                    Timestamp, DisplayTimestamp, ReceivedAt,
                    Encrypted, AttachmentPath, InReplyTo,
                    IsEdited, EditVersion, ClockSkewDetected
                FROM Messages;
                DROP TABLE Messages;
                ALTER TABLE Messages_backup RENAME TO Messages;
            ");
        }
    }
}
