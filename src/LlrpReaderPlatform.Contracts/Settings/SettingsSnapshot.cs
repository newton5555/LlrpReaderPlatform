namespace LlrpReaderPlatform.Contracts.Settings;

/// <summary>Reader 当前设置值的不可变快照（能力已捕获时才有意义）。</summary>
public sealed record SettingsSnapshot
{
    public required Guid ReaderId { get; init; }
    public required long CapabilityRevision { get; init; }
    public required IReadOnlyDictionary<string, object?> Values { get; init; }
}
