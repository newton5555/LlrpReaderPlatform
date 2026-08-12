using LlrpReaderPlatform.Contracts.Persistence;

namespace LlrpReaderPlatform.Services.Persistence;

/// <summary>没有基础设施设置时默认只生成停止后的最终快照。</summary>
public sealed class DefaultInventoryLoggingPolicy : IInventoryLoggingPolicy
{
    public Task<InventoryLoggingMode> GetModeAsync(CancellationToken ct = default) =>
        Task.FromResult(InventoryLoggingMode.FinalSnapshot);
}
