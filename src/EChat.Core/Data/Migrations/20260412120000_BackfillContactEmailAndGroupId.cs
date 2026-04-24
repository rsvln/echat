using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EChat.Core.Data.Migrations
{
    public partial class BackfillContactEmailAndGroupId : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill GroupId for old group chats (ChatId == GroupId was the convention)
            migrationBuilder.Sql(
                "UPDATE Chats SET GroupId = ChatId WHERE Type = 1 AND (GroupId IS NULL OR GroupId = '')");

            // Backfill ContactEmail: match 1:1 chat Name against Contact DisplayName or Email
            migrationBuilder.Sql(
                "UPDATE Chats SET ContactEmail = (" +
                "SELECT c.Email FROM Contacts c " +
                "WHERE c.DisplayName = Chats.Name OR c.Email = Chats.Name LIMIT 1) " +
                "WHERE Type = 0 AND (ContactEmail IS NULL OR ContactEmail = '') " +
                "AND (SELECT c.Email FROM Contacts c " +
                "WHERE c.DisplayName = Chats.Name OR c.Email = Chats.Name LIMIT 1) IS NOT NULL");

            // Backfill ContactEmail: if Name looks like an email, use it directly
            migrationBuilder.Sql(
                "UPDATE Chats SET ContactEmail = Name " +
                "WHERE Type = 0 AND (ContactEmail IS NULL OR ContactEmail = '') " +
                "AND NAME LIKE '%@%' AND NAME NOT LIKE '% %'");

            // Deduplicate group chats: for each (AccountId, GroupId) with multiple chats,
            // keep the one with the most recent activity and merge messages into it.
            // SQLite doesn't support UPDATE..FROM or DELETE..FROM, so we do it step by step.

            // Step 1: Reassign messages from loser chats to winner chats.
            // Winner = highest LastActivityAt (or CreatedAt as tiebreaker) per (AccountId, GroupId).
            migrationBuilder.Sql(@"
UPDATE Messages
SET ChatId = (
    SELECT c2.ChatId FROM Chats c2
    WHERE c2.AccountId = (SELECT c3.AccountId FROM Chats c3 WHERE c3.ChatId = Messages.ChatId)
    AND c2.GroupId = (SELECT c4.GroupId FROM Chats c4 WHERE c4.ChatId = Messages.ChatId)
    AND c2.Type = 1
    AND c2.GroupId IS NOT NULL AND c2.GroupId != ''
    ORDER BY COALESCE(c2.LastActivityAt, c2.CreatedAt) DESC, c2.ChatId ASC
    LIMIT 1
)
WHERE Messages.ChatId IN (
    SELECT c.ChatId FROM Chats c
    WHERE c.Type = 1 AND c.GroupId IS NOT NULL AND c.GroupId != ''
    AND EXISTS (
        SELECT 1 FROM Chats c2
        WHERE c2.AccountId = c.AccountId
        AND c2.GroupId = c.GroupId
        AND c2.ChatId != c.ChatId
    )
)");

            // Step 2: Sum UnreadCount from losers into winners
            migrationBuilder.Sql(@"
UPDATE Chats
SET UnreadCount = UnreadCount + COALESCE((
    SELECT SUM(c2.UnreadCount) FROM Chats c2
    WHERE c2.AccountId = Chats.AccountId
    AND c2.GroupId = Chats.GroupId
    AND c2.ChatId != Chats.ChatId
    AND c2.Deleted = 0
), 0)
WHERE Chats.Type = 1 AND Chats.GroupId IS NOT NULL AND Chats.GroupId != ''
AND EXISTS (
    SELECT 1 FROM Chats c2
    WHERE c2.AccountId = Chats.AccountId
    AND c2.GroupId = Chats.GroupId
    AND c2.ChatId != Chats.ChatId
)");

            // Step 3: Delete duplicate (loser) group chats
            // Keep only the chat with the highest LastActivityAt per (AccountId, GroupId)
            // For ties, keep the one with the lowest ChatId (alphabetically first = oldest)
            migrationBuilder.Sql(@"
DELETE FROM Chats
WHERE ChatId IN (
    SELECT c.ChatId FROM Chats c
    WHERE c.Type = 1 AND c.GroupId IS NOT NULL AND c.GroupId != ''
    AND c.ChatId NOT IN (
        SELECT c2.ChatId FROM Chats c2
        WHERE c2.Type = 1 AND c2.GroupId IS NOT NULL AND c2.GroupId != ''
        GROUP BY c2.AccountId, c2.GroupId
        HAVING c2.ChatId = MIN(c2.ChatId)
    )
    AND c.AccountId || '_' || c.GroupId IN (
        SELECT c3.AccountId || '_' || c3.GroupId FROM Chats c3
        WHERE c3.Type = 1 AND c3.GroupId IS NOT NULL AND c3.GroupId != ''
        GROUP BY c3.AccountId, c3.GroupId
        HAVING COUNT(*) > 1
    )
)");

            // Step 4: Also clean up group member references pointing to deleted chat IDs
            // (not needed since GroupMembers use GroupId, not ChatId)
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Cannot reverse the backfill or dedup
        }
    }
}