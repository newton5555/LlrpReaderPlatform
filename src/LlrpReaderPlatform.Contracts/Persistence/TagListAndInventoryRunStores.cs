using LlrpReaderPlatform.Contracts.Tagging;

namespace LlrpReaderPlatform.Contracts.Persistence;

public sealed record TagListEntry
{
    public required Guid Id { get; init; }
    public required Guid TagListId { get; init; }
    public required string EpcHex { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string? ColorHex { get; init; }
}

public sealed record TagListDefinition
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public bool IsEnabled { get; init; } = true;
    public string ColorHex { get; init; } = "#5EEAD4";
    public IReadOnlyList<TagListEntry> Entries { get; init; } = [];
}

public interface ITagListStore
{
    Task<IReadOnlyList<TagListDefinition>> GetAllAsync(CancellationToken ct = default);
    Task<TagListDefinition?> GetAsync(Guid tagListId, CancellationToken ct = default);
    Task SaveAsync(TagListDefinition tagList, CancellationToken ct = default);
    Task DeleteAsync(Guid tagListId, CancellationToken ct = default);
}

public sealed record InventoryRunRecord
{
    public required Guid Id { get; init; }
    public required Guid ReaderId { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? EndedAtUtc { get; init; }
    public string StopReason { get; init; } = "Manual";
    public long TotalReadCount { get; init; }
    public int UniqueTagCount { get; init; }
    /// <summary>停止后最终聚合标签快照的 JSON 文件路径；运行中为空。</summary>
    public string? SnapshotFilePath { get; init; }
    public string? LogFilePath { get; init; }
}

public interface IInventoryRunStore
{
    Task<IReadOnlyList<InventoryRunRecord>> GetForReaderAsync(Guid readerId, CancellationToken ct = default);
    Task SaveAsync(InventoryRunRecord run, CancellationToken ct = default);
}

/// <summary>一次盘存停止后生成的最终聚合快照。实时 TagObserved 不属于日志路径。</summary>
public sealed record InventoryRunSnapshot
{
    public required InventoryRunRecord Run { get; init; }
    public IReadOnlyList<TagObservation> Tags { get; init; } = [];
}

/// <summary>停止后持久化最终盘存快照的边界；具体文件格式由 Infrastructure 决定。</summary>
public interface IInventorySnapshotStore
{
    Task<string?> SaveAsync(InventoryRunSnapshot snapshot, CancellationToken ct = default);
}
