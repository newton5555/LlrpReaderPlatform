using LlrpReaderPlatform.Contracts.Errors;

namespace LlrpReaderPlatform.Contracts.Tagging;

/// <summary>盘存控制错误原因。</summary>
public enum InventoryError
{
    None = 0,
    ReaderBusy = 1,
    InvalidSettings = 2,
    DeviceFailed = 3,
}

/// <summary>盘存启动结果。</summary>
public sealed record StartInventoryResult(bool Succeeded, InventoryError Error = InventoryError.None, string? Message = null)
{
    public PlatformErrorCode ErrorCode { get; init; } = Succeeded
        ? PlatformErrorCode.None
        : Error == InventoryError.ReaderBusy
            ? PlatformErrorCode.ReaderBusy
            : Error == InventoryError.InvalidSettings
                ? PlatformErrorCode.InvalidSettings
                : PlatformErrorCode.DeviceFailed;
}

/// <summary>盘存租约的生命周期状态。</summary>
public enum InventoryLifecycleState
{
    Started = 0,
    Stopped = 1,
}

/// <summary>盘存结束原因。UI 只根据平台事件显示状态，不自行推断停止来源。</summary>
public enum InventoryStopReason
{
    Manual = 0,
    Gpi = 1,
    Duration = 2,
    DeviceDisconnected = 3,
    ConnectionFaulted = 4,
    ReaderException = 5,
    Removed = 6,
    Deactivated = 7,
    ApplicationExit = 8,
    StopFailed = 9,
}

/// <summary>盘存租约开始或结束的统一平台事件。</summary>
public sealed class InventoryLifecycleChangedEventArgs(
    Guid readerId,
    InventoryLifecycleState state,
    InventoryStopReason? stopReason = null,
    string? error = null) : EventArgs
{
    public Guid ReaderId { get; } = readerId;
    public InventoryLifecycleState State { get; } = state;
    public InventoryStopReason? StopReason { get; } = stopReason;
    public string? Error { get; } = error;
}

/// <summary>新标签观测事件参数（服务线程发布，UI 适配层负责切线程）。</summary>
public sealed class TagObservedEventArgs(Guid readerId, TagObservation tag) : EventArgs
{
    public Guid ReaderId { get; } = readerId;
    public TagObservation Tag { get; } = tag;
}

/// <summary>
/// 盘存 / TagReport 聚合 / TagAccess / GPI-GPO 服务（由 ReaderManager 实现）。
/// Inventory 是长连接租约；运行期间短操作返回 <see cref="InventoryError.ReaderBusy"/>。
/// </summary>
public interface IInventoryService
{
    /// <summary>当前进程内因聚合消费者饱和而丢弃的 TagReport 数量。</summary>
    long DroppedTagReportCount { get; }

    Task<StartInventoryResult> StartInventoryAsync(Guid readerId, InventorySpec spec, CancellationToken ct = default);

    Task StopInventoryAsync(Guid readerId, CancellationToken ct = default);

    /// <summary>聚合后的标签观测列表。</summary>
    IReadOnlyList<TagObservation> GetTags(Guid readerId);

    void ClearTags(Guid readerId);

    event EventHandler<TagObservedEventArgs>? TagObserved;

    /// <summary>
    /// 盘存租约的唯一生命周期事实来源。手动停止、GPI 触发、定时结束和设备断连
    /// 都必须通过此事件通知消费者，WPF 不直接猜测 Reader 是否仍在盘存。
    /// </summary>
    event EventHandler<InventoryLifecycleChangedEventArgs>? LifecycleChanged;

    event EventHandler<GpiObservedEventArgs>? GpiChanged;

    Task<IReadOnlyList<GpiPortStatus>> GetGpiStatusAsync(Guid readerId, CancellationToken ct = default);

    Task<IReadOnlyList<GpoPortStatus>> GetGpoStatusAsync(Guid readerId, CancellationToken ct = default);

    /// <summary>在一个短连接租约内读取一致的 GPI/GPO 状态。</summary>
    Task<GpioStatusSnapshot> GetGpioStatusAsync(Guid readerId, CancellationToken ct = default);

    Task<TagAccessResult> ReadTagMemoryAsync(Guid readerId, TagReadRequest request, CancellationToken ct = default);

    Task<TagAccessResult> WriteTagMemoryAsync(Guid readerId, TagWriteRequest request, CancellationToken ct = default);

    /// <summary>
    /// 设置 GPO。Inventory 运行时抛出服务层的 busy 异常，表示 ReaderBusy；
    /// 具体异常类型不下沉到 Contracts，以保持契约项目不依赖服务实现。
    /// </summary>
    Task SetGpoAsync(Guid readerId, GpioCommand command, CancellationToken ct = default);
}
