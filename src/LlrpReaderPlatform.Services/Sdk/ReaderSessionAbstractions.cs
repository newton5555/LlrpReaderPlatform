using Tagging = LlrpReaderPlatform.Contracts.Tagging;
using LlrpNet.Core.Protocol;
using LlrpSdk;

namespace LlrpReaderPlatform.Services.Sdk;

/// <summary>标准 LLRP SDK TagReport 事件参数（Services 内部，不向 Contracts 泄漏）。</summary>
public sealed class SdkTagReportEventArgs(TagReport report) : EventArgs
{
    public TagReport Report { get; } = report;
}

/// <summary>Reader 设备异常事件参数（Services 内部）。</summary>
public sealed class ReaderDeviceExceptionEventArgs(
    string message,
    uint? roSpecId,
    ushort? antennaId,
    DateTimeOffset timestamp) : EventArgs
{
    public string Message { get; } = message;
    public uint? ROSpecId { get; } = roSpecId;
    public ushort? AntennaId { get; } = antennaId;
    public DateTimeOffset Timestamp { get; } = timestamp;
}

/// <summary>Reader 传输连接进入 Faulted 的事件参数（设备主动关闭另有专门事件）。</summary>
public sealed class ReaderConnectionFaultedEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

public sealed class SdkGpiChangedEventArgs(ushort portNumber, bool state, DateTimeOffset timestamp) : EventArgs
{
    public ushort PortNumber { get; } = portNumber;
    public bool State { get; } = state;
    public DateTimeOffset Timestamp { get; } = timestamp;
}

/// <summary>
/// 单个 Reader 的标准 LLRP 连接会话抽象。此为 Services 内部依赖（引用 LlrpSdk），
/// 不暴露给 Contracts；TestKit 通过 <c>FakeSession</c> 提供可控替身。
/// 不含任何厂商扩展——Impinj 等能力由扩展模块在二次连接阶段叠加（F5）。
/// </summary>
public interface IReaderSession : IAsyncDisposable
{
    bool IsConnected { get; }
    ReaderIdentity? Identity { get; }
    ReaderCapabilities? Capabilities { get; }
    LlrpProtocolVersion? NegotiatedVersion => null;

    event EventHandler<SdkTagReportEventArgs>? TagReported;
    event EventHandler<ReaderDeviceExceptionEventArgs>? ReaderExceptionOccurred;
    event EventHandler<ReaderConnectionFaultedEventArgs>? ConnectionFaulted;
    event EventHandler<EventArgs>? DeviceInitiatedClosed;
    event EventHandler<SdkGpiChangedEventArgs>? GpiChanged;

    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);

    /// <summary>读取 Reader 当前由 SDK 管理的标准设置。</summary>
    Task<ReaderSettingsSnapshot> QuerySettingsAsync(CancellationToken cancellationToken);

    /// <summary>读取 SDK 根据设备能力计算出的默认设置。</summary>
    Task<ReaderSettingsDefaults> GetDefaultSettingsAsync(CancellationToken cancellationToken);

    /// <summary>校验并应用一份完整的标准设置。</summary>
    Task ApplySettingsAsync(ReaderSettings settings, CancellationToken cancellationToken);

    /// <summary>启动标准盘存（当前 Reader 配置）。</summary>
    Task StartInventoryAsync(Tagging.InventorySpec spec, CancellationToken cancellationToken);

    /// <summary>使用已经编译好的 SDK InventorySettings 启动盘存。</summary>
    Task StartInventoryAsync(InventorySettings settings, CancellationToken cancellationToken);

    Task StopInventoryAsync(CancellationToken cancellationToken);

    Task<Tagging.TagAccessResult> ReadTagMemoryAsync(Tagging.TagReadRequest request, CancellationToken cancellationToken);

    Task<Tagging.TagAccessResult> WriteTagMemoryAsync(Tagging.TagWriteRequest request, CancellationToken cancellationToken);

    /// <summary>在执行块的 Sdk 块擦除。仅当 Reader 支持块擦除时调用。</summary>
    Task<Tagging.TagAccessResult> BlockEraseTagMemoryAsync(Tagging.TagBlockEraseRequest request, CancellationToken cancellationToken);

    Task SetGpoAsync(ushort portNumber, bool state, CancellationToken cancellationToken);

    Task<IReadOnlyList<Tagging.GpiPortStatus>> GetGpiStatusAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<Tagging.GpoPortStatus>> GetGpoStatusAsync(CancellationToken cancellationToken);

    Task<Tagging.GpioStatusSnapshot> GetGpioStatusAsync(CancellationToken cancellationToken);
}

/// <summary>创建标准 LLRP 会话的工厂。提供默认实现；测试注入 FakeSession 工厂。</summary>
public interface IReaderSessionFactory
{
    /// <summary>
    /// 创建会话。传入已匹配的扩展模块时，工厂在构建 SDK Reader 前对其逐一调用
    /// <c>IReaderExtensionModule.ConfigureBuilder</c>（如 Impinj 的 UseImpinj）。
    /// </summary>
    IReaderSession Create(
        LlrpReaderPlatform.Contracts.Readers.ReaderProfile profile,
        IReadOnlyList<LlrpReaderPlatform.Services.Extensions.IReaderExtensionModule>? extensions = null);
}
