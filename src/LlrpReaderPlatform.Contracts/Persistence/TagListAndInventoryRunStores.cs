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
    public string? LogFilePath { get; init; }
}

public interface IInventoryRunStore
{
    Task<IReadOnlyList<InventoryRunRecord>> GetForReaderAsync(Guid readerId, CancellationToken ct = default);
    Task SaveAsync(InventoryRunRecord run, CancellationToken ct = default);
}
