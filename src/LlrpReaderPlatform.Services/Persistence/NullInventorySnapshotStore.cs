using LlrpReaderPlatform.Contracts.Persistence;

namespace LlrpReaderPlatform.Services.Persistence;

/// <summary>未注册基础设施时的快照空实现，保持服务层可独立测试。</summary>
public sealed class NullInventorySnapshotStore : IInventorySnapshotStore
{
    public Task<string?> SaveAsync(InventoryRunSnapshot snapshot, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}
