using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EChat.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class DropGroupOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupOperations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupOperations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GroupId = table.Column<string>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", nullable: false),
                    Applied = table.Column<bool>(type: "INTEGER", nullable: false),
                    Operation = table.Column<int>(type: "INTEGER", nullable: false),
                    Target = table.Column<string>(type: "TEXT", nullable: true),
                    Timestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupOperations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupOperations_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "GroupId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupOperations_Applied",
                table: "GroupOperations",
                column: "Applied");

            migrationBuilder.CreateIndex(
                name: "IX_GroupOperations_GroupId_Version",
                table: "GroupOperations",
                columns: new[] { "GroupId", "Version" });
        }
    }
}
