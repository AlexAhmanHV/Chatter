using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Chatter.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPinAndEditHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPinned",
                table: "Messages",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "MessageEditHistory",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MessageId = table.Column<long>(type: "INTEGER", nullable: false),
                    PreviousBody = table.Column<string>(type: "TEXT", nullable: false),
                    EditedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageEditHistory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessageEditHistory_MessageId",
                table: "MessageEditHistory",
                column: "MessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessageEditHistory");

            migrationBuilder.DropColumn(
                name: "IsPinned",
                table: "Messages");
        }
    }
}
