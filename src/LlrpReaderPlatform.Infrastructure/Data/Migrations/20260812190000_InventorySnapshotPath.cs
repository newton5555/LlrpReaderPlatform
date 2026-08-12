using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace LlrpReaderPlatform.Infrastructure.Data.Migrations;

/// <inheritdoc />
[DbContext(typeof(PlatformDbContext))]
[Migration("20260812190000_InventorySnapshotPath")]
public partial class InventorySnapshotPath : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "SnapshotFilePath",
            table: "InventoryRuns",
            type: "TEXT",
            maxLength: 500,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "SnapshotFilePath",
            table: "InventoryRuns");
    }
}
