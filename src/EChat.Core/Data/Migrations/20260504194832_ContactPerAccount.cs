using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EChat.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class ContactPerAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // SQLite ignores PRAGMA foreign_keys inside a transaction.
            // suppressTransaction: true causes EF to commit the active migration
            // transaction before executing each command, so the PRAGMA actually
            // takes effect and subsequent DDL (DROP TABLE, table rebuilds) can run
            // without FK-constraint failures.

            // 1 — disable FK enforcement
            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);

            // 2 — add AccountId column; existing rows get AccountId = '' (the DEFAULT)
            migrationBuilder.Sql(
                """ALTER TABLE "Contacts" ADD COLUMN "AccountId" TEXT NOT NULL DEFAULT '';""",
                suppressTransaction: true);

            // 3 — rebuild Contacts with composite PK (AccountId, Email).
            //     Done BEFORE the data migration so that INSERT OR IGNORE is keyed on
            //     (AccountId, Email), not just Email — otherwise the new per-account rows
            //     would collide with the existing ('', email) placeholder rows.
            migrationBuilder.Sql("""
                CREATE TABLE "ef_temp_Contacts" (
                    "AccountId"        TEXT NOT NULL,
                    "Email"            TEXT NOT NULL,
                    "DisplayName"      TEXT,
                    "PublicKey"        TEXT,
                    "KeyFingerprint"   TEXT,
                    "Verified"         INTEGER NOT NULL,
                    "LastSeen"         TEXT,
                    "ProtocolVersion"  TEXT,
                    "SupportsBatching" INTEGER NOT NULL,
                    "AddedAt"          TEXT,
                    "Notes"            TEXT,
                    "IsBlocked"        INTEGER NOT NULL,
                    "BlockedAt"        TEXT,
                    "LocalPublicKey"   TEXT,
                    "LocalPrivateKey"  TEXT,
                    "LocalKeyRevokedAt" TEXT,
                    CONSTRAINT "PK_Contacts" PRIMARY KEY ("AccountId", "Email")
                );
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                INSERT INTO "ef_temp_Contacts" (
                    "AccountId","Email","DisplayName","PublicKey","KeyFingerprint",
                    "Verified","LastSeen","ProtocolVersion","SupportsBatching",
                    "AddedAt","Notes","IsBlocked","BlockedAt",
                    "LocalPublicKey","LocalPrivateKey","LocalKeyRevokedAt"
                )
                SELECT
                    "AccountId","Email","DisplayName","PublicKey","KeyFingerprint",
                    "Verified","LastSeen","ProtocolVersion","SupportsBatching",
                    "AddedAt","Notes","IsBlocked","BlockedAt",
                    "LocalPublicKey","LocalPrivateKey","LocalKeyRevokedAt"
                FROM "Contacts";
                """, suppressTransaction: true);

            migrationBuilder.Sql("""DROP TABLE "Contacts";""", suppressTransaction: true);
            migrationBuilder.Sql("""ALTER TABLE "ef_temp_Contacts" RENAME TO "Contacts";""", suppressTransaction: true);
            migrationBuilder.Sql("""CREATE INDEX "IX_Contacts_Verified" ON "Contacts" ("Verified");""", suppressTransaction: true);

            // 4 — data migration: one Contact row per (accountId, contactEmail) pair.
            //     PK is now (AccountId, Email) so ('realId', email) != ('', email) — no collision.
            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO "Contacts"
                    ("AccountId","Email","DisplayName","PublicKey","KeyFingerprint",
                     "Verified","LastSeen","ProtocolVersion","SupportsBatching",
                     "AddedAt","Notes","IsBlocked","BlockedAt",
                     "LocalPublicKey","LocalPrivateKey","LocalKeyRevokedAt")
                SELECT DISTINCT
                    c."AccountId",
                    c."ContactEmail",
                    COALESCE(co."DisplayName", c."ContactEmail"),
                    co."PublicKey",
                    co."KeyFingerprint",
                    COALESCE(co."Verified", 0),
                    co."LastSeen",
                    co."ProtocolVersion",
                    COALESCE(co."SupportsBatching", 0),
                    co."AddedAt",
                    co."Notes",
                    COALESCE(co."IsBlocked", 0),
                    co."BlockedAt",
                    co."LocalPublicKey",
                    co."LocalPrivateKey",
                    co."LocalKeyRevokedAt"
                FROM "Chats" c
                LEFT JOIN "Contacts" co ON co."Email" = c."ContactEmail" AND co."AccountId" = ''
                WHERE c."AccountId" IS NOT NULL AND c."ContactEmail" IS NOT NULL;
                """, suppressTransaction: true);

            // 5 — remove the '' placeholder rows; real per-account rows are now in place
            migrationBuilder.Sql("""DELETE FROM "Contacts" WHERE "AccountId" = '';""", suppressTransaction: true);

            // 6 — rebuild Chats without FK to Contacts.
            //     FK_Chats_Groups_GroupId is preserved; FK_Chats_Contacts_ContactEmail is dropped.
            //     Cannot use ALTER TABLE to drop a FK in SQLite — must recreate the table.
            //     Dropping "Chats" requires FK-checks OFF (Messages → Chats); that is already set.
            migrationBuilder.Sql("""
                CREATE TABLE "ef_temp_Chats" (
                    "ChatId"                     TEXT NOT NULL,
                    "Type"                       INTEGER NOT NULL,
                    "Name"                       TEXT NOT NULL,
                    "AccountId"                  TEXT,
                    "ContactEmail"               TEXT,
                    "GroupId"                    TEXT,
                    "LastMessageId"              TEXT,
                    "UnreadCount"                INTEGER NOT NULL,
                    "Muted"                      INTEGER NOT NULL,
                    "Archived"                   INTEGER NOT NULL,
                    "Deleted"                    INTEGER NOT NULL,
                    "TombstoneVersion"           INTEGER,
                    "CreatedAt"                  TEXT NOT NULL,
                    "LastActivityAt"             TEXT,
                    "PendingOutgoingInviteToken" TEXT,
                    CONSTRAINT "PK_Chats" PRIMARY KEY ("ChatId"),
                    CONSTRAINT "FK_Chats_Groups_GroupId"
                        FOREIGN KEY ("GroupId") REFERENCES "Groups" ("GroupId") ON DELETE RESTRICT
                );
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                INSERT INTO "ef_temp_Chats" (
                    "ChatId","Type","Name","AccountId","ContactEmail","GroupId",
                    "LastMessageId","UnreadCount","Muted","Archived","Deleted",
                    "TombstoneVersion","CreatedAt","LastActivityAt","PendingOutgoingInviteToken"
                )
                SELECT
                    "ChatId","Type","Name","AccountId","ContactEmail","GroupId",
                    "LastMessageId","UnreadCount","Muted","Archived","Deleted",
                    "TombstoneVersion","CreatedAt","LastActivityAt","PendingOutgoingInviteToken"
                FROM "Chats";
                """, suppressTransaction: true);

            migrationBuilder.Sql("""DROP TABLE "Chats";""", suppressTransaction: true);
            migrationBuilder.Sql("""ALTER TABLE "ef_temp_Chats" RENAME TO "Chats";""", suppressTransaction: true);

            // Recreate all Chats indexes (IX_Chats_ContactEmail is intentionally omitted)
            migrationBuilder.Sql("""CREATE INDEX "IX_Chats_LastActivityAt"         ON "Chats" ("LastActivityAt");""", suppressTransaction: true);
            migrationBuilder.Sql("""CREATE INDEX "IX_Chats_Archived_LastActivityAt" ON "Chats" ("Archived", "LastActivityAt");""", suppressTransaction: true);
            migrationBuilder.Sql("""CREATE INDEX "IX_Chats_AccountId"              ON "Chats" ("AccountId");""", suppressTransaction: true);
            migrationBuilder.Sql("""CREATE INDEX "IX_Chats_AccountId_ContactEmail" ON "Chats" ("AccountId", "ContactEmail");""", suppressTransaction: true);
            migrationBuilder.Sql("""CREATE INDEX "IX_Chats_GroupId"                ON "Chats" ("GroupId");""", suppressTransaction: true);

            // 7 — re-enable FK enforcement
            migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("PRAGMA foreign_keys = OFF;", suppressTransaction: true);

            // Rebuild Contacts: revert to single-column PK (Email).
            // Keep only one row per email (first by AccountId) — data loss is expected on rollback.
            migrationBuilder.Sql("""
                CREATE TABLE "ef_temp_Contacts" (
                    "Email"            TEXT NOT NULL,
                    "DisplayName"      TEXT,
                    "PublicKey"        TEXT,
                    "KeyFingerprint"   TEXT,
                    "Verified"         INTEGER NOT NULL,
                    "LastSeen"         TEXT,
                    "ProtocolVersion"  TEXT,
                    "SupportsBatching" INTEGER NOT NULL,
                    "AddedAt"          TEXT,
                    "Notes"            TEXT,
                    "IsBlocked"        INTEGER NOT NULL,
                    "BlockedAt"        TEXT,
                    "LocalPublicKey"   TEXT,
                    "LocalPrivateKey"  TEXT,
                    "LocalKeyRevokedAt" TEXT,
                    CONSTRAINT "PK_Contacts" PRIMARY KEY ("Email")
                );
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                INSERT OR IGNORE INTO "ef_temp_Contacts" (
                    "Email","DisplayName","PublicKey","KeyFingerprint",
                    "Verified","LastSeen","ProtocolVersion","SupportsBatching",
                    "AddedAt","Notes","IsBlocked","BlockedAt",
                    "LocalPublicKey","LocalPrivateKey","LocalKeyRevokedAt"
                )
                SELECT
                    "Email","DisplayName","PublicKey","KeyFingerprint",
                    "Verified","LastSeen","ProtocolVersion","SupportsBatching",
                    "AddedAt","Notes","IsBlocked","BlockedAt",
                    "LocalPublicKey","LocalPrivateKey","LocalKeyRevokedAt"
                FROM "Contacts"
                ORDER BY "AccountId";
                """, suppressTransaction: true);

            migrationBuilder.Sql("""DROP TABLE "Contacts";""", suppressTransaction: true);
            migrationBuilder.Sql("""ALTER TABLE "ef_temp_Contacts" RENAME TO "Contacts";""", suppressTransaction: true);
            migrationBuilder.Sql("""CREATE INDEX "IX_Contacts_Verified" ON "Contacts" ("Verified");""", suppressTransaction: true);

            // Rebuild Chats: restore FK to Contacts(Email) and IX_Chats_ContactEmail
            migrationBuilder.Sql("""
                CREATE TABLE "ef_temp_Chats" (
                    "ChatId"                     TEXT NOT NULL,
                    "Type"                       INTEGER NOT NULL,
                    "Name"                       TEXT NOT NULL,
                    "AccountId"                  TEXT,
                    "ContactEmail"               TEXT,
                    "GroupId"                    TEXT,
                    "LastMessageId"              TEXT,
                    "UnreadCount"                INTEGER NOT NULL,
                    "Muted"                      INTEGER NOT NULL,
                    "Archived"                   INTEGER NOT NULL,
                    "Deleted"                    INTEGER NOT NULL,
                    "TombstoneVersion"           INTEGER,
                    "CreatedAt"                  TEXT NOT NULL,
                    "LastActivityAt"             TEXT,
                    "PendingOutgoingInviteToken" TEXT,
                    CONSTRAINT "PK_Chats" PRIMARY KEY ("ChatId"),
                    CONSTRAINT "FK_Chats_Contacts_ContactEmail"
                        FOREIGN KEY ("ContactEmail") REFERENCES "Contacts" ("Email") ON DELETE RESTRICT,
                    CONSTRAINT "FK_Chats_Groups_GroupId"
                        FOREIGN KEY ("GroupId") REFERENCES "Groups" ("GroupId") ON DELETE RESTRICT
                );
                """, suppressTransaction: true);

            migrationBuilder.Sql("""
                INSERT INTO "ef_temp_Chats" (
                    "ChatId","Type","Name","AccountId","ContactEmail","GroupId",
                    "LastMessageId","UnreadCount","Muted","Archived","Deleted",
                    "TombstoneVersion","CreatedAt","LastActivityAt","PendingOutgoingInviteToken"
                )
                SELECT
                    "ChatId","Type","Name","AccountId","ContactEmail","GroupId",
                    "LastMessageId","UnreadCount","Muted","Archived","Deleted",
                    "TombstoneVersion","CreatedAt","LastActivityAt","PendingOutgoingInviteToken"
                FROM "Chats";
                """, suppressTransaction: true);

            migrationBuilder.Sql("""DROP TABLE "Chats";""", suppressTransaction: true);
            migrationBuilder.Sql("""ALTER TABLE "ef_temp_Chats" RENAME TO "Chats";""", suppressTransaction: true);

            migrationBuilder.Sql("""CREATE INDEX "IX_Chats_LastActivityAt"         ON "Chats" ("LastActivityAt");""", suppressTransaction: true);
            migrationBuilder.Sql("""CREATE INDEX "IX_Chats_Archived_LastActivityAt" ON "Chats" ("Archived", "LastActivityAt");""", suppressTransaction: true);
            migrationBuilder.Sql("""CREATE INDEX "IX_Chats_AccountId"              ON "Chats" ("AccountId");""", suppressTransaction: true);
            migrationBuilder.Sql("""CREATE INDEX "IX_Chats_ContactEmail"           ON "Chats" ("ContactEmail");""", suppressTransaction: true);
            migrationBuilder.Sql("""CREATE INDEX "IX_Chats_AccountId_ContactEmail" ON "Chats" ("AccountId", "ContactEmail");""", suppressTransaction: true);
            migrationBuilder.Sql("""CREATE INDEX "IX_Chats_GroupId"                ON "Chats" ("GroupId");""", suppressTransaction: true);

            migrationBuilder.Sql("PRAGMA foreign_keys = ON;", suppressTransaction: true);
        }
    }
}
