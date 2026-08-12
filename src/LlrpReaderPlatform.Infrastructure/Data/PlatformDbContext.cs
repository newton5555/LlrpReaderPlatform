using LlrpReaderPlatform.Contracts.Readers;
using Microsoft.EntityFrameworkCore;

namespace LlrpReaderPlatform.Infrastructure.Data;

/// <summary>新平台 SQLite 数据库上下文。只保存平台持久化模型，不保存 SDK 类型。</summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public DbSet<ReaderProfileEntity> ReaderProfiles => Set<ReaderProfileEntity>();
    public DbSet<ReaderSettingsPresetEntity> ReaderSettingsPresets => Set<ReaderSettingsPresetEntity>();
    public DbSet<AppSettingEntity> AppSettings => Set<AppSettingEntity>();
    public DbSet<TagListEntity> TagLists => Set<TagListEntity>();
    public DbSet<TagListEntryEntity> TagListEntries => Set<TagListEntryEntity>();
    public DbSet<InventoryRunEntity> InventoryRuns => Set<InventoryRunEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ReaderProfileEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Host).HasMaxLength(250).IsRequired();
            entity.Property(x => x.LlrpVersion).HasConversion<int>();
            entity.HasIndex(x => new { x.Host, x.Port }).IsUnique();
        });
        modelBuilder.Entity<ReaderSettingsPresetEntity>(entity =>
        {
            entity.HasKey(x => x.ReaderId);
            entity.Property(x => x.SettingsJson).IsRequired();
        });
        modelBuilder.Entity<AppSettingEntity>(entity =>
        {
            entity.HasKey(x => x.Key);
            entity.Property(x => x.Key).HasMaxLength(100);
            entity.Property(x => x.Value).IsRequired();
        });
        modelBuilder.Entity<TagListEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
            entity.Property(x => x.ColorHex).HasMaxLength(20).IsRequired();
            entity.HasMany(x => x.Entries).WithOne(x => x.TagList)
                .HasForeignKey(x => x.TagListId).OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<TagListEntryEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EpcHex).HasMaxLength(128).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();
            entity.Property(x => x.ColorHex).HasMaxLength(20);
            entity.HasIndex(x => new { x.TagListId, x.EpcHex }).IsUnique();
        });
        modelBuilder.Entity<InventoryRunEntity>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.StopReason).HasMaxLength(50).IsRequired();
            entity.Property(x => x.SnapshotFilePath).HasMaxLength(500);
            entity.Property(x => x.LogFilePath).HasMaxLength(500);
            entity.HasIndex(x => new { x.ReaderId, x.StartedAtUtc });
        });
    }
}

public sealed class ReaderProfileEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public LlrpProtocolVersionOption LlrpVersion { get; set; }
    public bool IsEnabled { get; set; }
}

public sealed class ReaderSettingsPresetEntity
{
    public Guid ReaderId { get; set; }
    public int SchemaVersion { get; set; }
    public string SettingsJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class AppSettingEntity
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed class TagListEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public string ColorHex { get; set; } = "#5EEAD4";
    public List<TagListEntryEntity> Entries { get; set; } = [];
}

public sealed class TagListEntryEntity
{
    public Guid Id { get; set; }
    public Guid TagListId { get; set; }
    public TagListEntity? TagList { get; set; }
    public string EpcHex { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ColorHex { get; set; }
}

public sealed class InventoryRunEntity
{
    public Guid Id { get; set; }
    public Guid ReaderId { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public string StopReason { get; set; } = "Manual";
    public long TotalReadCount { get; set; }
    public int UniqueTagCount { get; set; }
    public string? SnapshotFilePath { get; set; }
    public string? LogFilePath { get; set; }
}
