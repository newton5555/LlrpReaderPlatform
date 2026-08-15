using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Tagging;

namespace LlrpReaderPlatform.VirtualReader;

/// <summary>一条由真实平台 JSONL 或 snapshot 生成的确定性回放事件。</summary>
public sealed record VirtualReplayEvent
{
    public required int Sequence { get; init; }
    public Guid? SourceRunId { get; init; }
    public Guid? SourceReaderId { get; init; }
    public TimeSpan Offset { get; init; }
    public required TagObservation Tag { get; init; }
}

/// <summary>虚拟 Reader 使用的标准化数据集，不修改来源文件。</summary>
public sealed record VirtualInventoryDataset
{
    public required VirtualReaderScenario Scenario { get; init; }
    public IReadOnlyList<VirtualReplayEvent> Events { get; init; } = [];
    public IReadOnlyList<TagObservation> SnapshotTags { get; init; } = [];
    public IReadOnlyList<InventoryRunRecord> SourceRuns { get; init; } = [];
    public IReadOnlyDictionary<string, VirtualTagMemorySeed> MemoryByEpc { get; init; } =
        new Dictionary<string, VirtualTagMemorySeed>(StringComparer.OrdinalIgnoreCase);
}
