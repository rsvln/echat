using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EChat.Core.Data.Migrations
{
    public partial class AddGroupMemberAvatarColor : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NameColor",
                table: "GroupMembers",
                type: "TEXT",
                nullable: true);

            // Backfill NameColor for existing group members using deterministic hash
            migrationBuilder.Sql(@"
UPDATE GroupMembers SET NameColor = 
    CASE (ABS(SUBSTR(MemberEmail, 1, 1))) % 16
        WHEN 0 THEN '#5b8fd9'
        WHEN 1 THEN '#e07a5f'
        WHEN 2 THEN '#81b29a'
        WHEN 3 THEN '#9b5de5'
        WHEN 4 THEN '#00bbf9'
        WHEN 5 THEN '#f15bb5'
        WHEN 6 THEN '#00f5d4'
        WHEN 7 THEN '#d4a373'
        WHEN 8 THEN '#a8dadc'
        WHEN 9 THEN '#e9c46a'
        WHEN 10 THEN '#2a9d8f'
        WHEN 11 THEN '#e76f51'
        WHEN 12 THEN '#606c38'
        WHEN 13 THEN '#99c2b2'
        WHEN 14 THEN '#f4511e'
        WHEN 15 THEN '#7CB342'
    END
WHERE NameColor IS NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NameColor",
                table: "GroupMembers");
        }
    }
}