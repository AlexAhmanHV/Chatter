using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chatter.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPinnedChats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PinnedChats",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", nullable: false),
                    ChatId = table.Column<string>(type: "TEXT", nullable: false),
                    PinnedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PinnedChats", x => new { x.UserId, x.ChatId });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PinnedChats");
        }
    }
}
