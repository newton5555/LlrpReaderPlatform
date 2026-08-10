namespace LlrpReaderPlatform.Contracts.Persistence;

/// <summary>
/// Reader 设置的最后一次平台语义快照。只保存版本化 JSON，不把 SDK 或厂商对象写进 Contracts/数据库模型。
/// </summary>
public sealed record ReaderSettingsPreset
{
    public required Guid ReaderId { get; init; }
    public int SchemaVersion { get; init; } = 1;
    public required string SettingsJson { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public interface IReaderSettingsPresetStore
{
    Task<ReaderSettingsPreset?> GetAsync(Guid readerId, CancellationToken ct = default);
    Task SaveAsync(ReaderSettingsPreset preset, CancellationToken ct = default);
    Task DeleteAsync(Guid readerId, CancellationToken ct = default);
}
