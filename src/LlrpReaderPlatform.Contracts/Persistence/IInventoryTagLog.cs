using LlrpReaderPlatform.Contracts.Tagging;

namespace LlrpReaderPlatform.Contracts.Persistence;

/// <summary>
/// Inventory 运行的可选标签日志边界。Services 只知道不可变平台 DTO，具体文件/数据库格式由
/// Infrastructure 决定；关闭日志时由无操作实现满足同一生命周期。
/// </summary>
public interface IInventoryTagLog
{
    Task<string?> StartAsync(InventoryRunRecord run, CancellationToken ct = default);

    Task AppendAsync(InventoryRunRecord run, TagObservation tag, CancellationToken ct = default);

    Task CompleteAsync(InventoryRunRecord run, CancellationToken ct = default);
}
