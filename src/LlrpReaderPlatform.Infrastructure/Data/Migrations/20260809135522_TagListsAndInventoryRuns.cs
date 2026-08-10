using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LlrpReaderPlatform.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class TagListsAndInventoryRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InventoryRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReaderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EndedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    StopReason = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    TotalReadCount = table.Column<long>(type: "INTEGER", nullable: false),
                    UniqueTagCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LogFilePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TagLists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ColorHex = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagLists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TagListEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TagListId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EpcHex = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ColorHex = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TagListEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TagListEntries_TagLists_TagListId",
                        column: x => x.TagListId,
                        principalTable: "TagLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryRuns_ReaderId_StartedAtUtc",
                table: "InventoryRuns",
                columns: new[] { "ReaderId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TagListEntries_TagListId_EpcHex",
                table: "TagListEntries",
                columns: new[] { "TagListId", "EpcHex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InventoryRuns");

            migrationBuilder.DropTable(
                name: "TagListEntries");

            migrationBuilder.DropTable(
                name: "TagLists");
        }
    }
}
