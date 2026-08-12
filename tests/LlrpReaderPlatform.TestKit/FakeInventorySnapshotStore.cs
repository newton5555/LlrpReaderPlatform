using LlrpReaderPlatform.Contracts.Persistence;

namespace LlrpReaderPlatform.TestKit;

public sealed class FakeInventorySnapshotStore : IInventorySnapshotStore
{
    private readonly List<InventoryRunSnapshot> snapshots = [];

    public IReadOnlyList<InventoryRunSnapshot> Snapshots => snapshots;

    public string? PathToReturn { get; set; } = "test-snapshots/run.json";

    public Task<string?> SaveAsync(InventoryRunSnapshot snapshot, CancellationToken ct = default)
    {
        snapshots.Add(snapshot);
        return Task.FromResult(PathToReturn);
    }
}
