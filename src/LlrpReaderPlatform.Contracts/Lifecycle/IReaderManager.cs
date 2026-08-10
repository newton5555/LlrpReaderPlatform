using LlrpReaderPlatform.Contracts.Readers;

namespace LlrpReaderPlatform.Contracts.Lifecycle;

/// <summary>Reader 生命周期管理服务。厂商无关，供多个 UI 消费者共享。</summary>
public interface IReaderManager
{
    /// <summary>从持久化存储恢复 Reader 注册；应用启动时调用一次。</summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>补偿式添加：Probe → 持久化 → 注册 → 可选激活；任何失败按补偿顺序回滚。</summary>
    Task<ReaderAddResult> AddAsync(ReaderProfile profile, bool enableAfterAdding, CancellationToken ct = default);

    /// <summary>临时连接探测，不注册不持久化。</summary>
    Task<ReaderProbeResult> ProbeAsync(ReaderProfile profile, CancellationToken ct = default);

    Task RemoveAsync(Guid readerId, CancellationToken ct = default);
    Task SetEnabledAsync(Guid readerId, bool enabled, CancellationToken ct = default);

    /// <summary>激活（短连接：连接→读身份/能力/配置→写缓存→断开），不保持 Session。</summary>
    Task<ReaderActivationResult> ActivateAsync(Guid readerId, CancellationToken ct = default);
    Task DeactivateAsync(Guid readerId, CancellationToken ct = default);

    IReadOnlyList<ReaderRuntimeSnapshot> Readers { get; }
    ReaderRuntimeSnapshot GetSnapshot(Guid readerId);

    event EventHandler<ReaderStateChangedEventArgs> StateChanged;
}

public sealed class ReaderStateChangedEventArgs(ReaderRuntimeSnapshot snapshot) : EventArgs
{
    public ReaderRuntimeSnapshot Snapshot { get; } = snapshot;
}

/// <summary>补偿式 AddAsync 的原子结果。</summary>
public enum ReaderAddStatus
{
    Added,
    ProbeFailed,
    PersistFailed,
    RegisterFailed,
    ActivationFailed,
}

public sealed record ReaderAddResult(ReaderAddStatus Status, string? Error = null, Guid? ReaderId = null)
{
    public bool Succeeded => Status == ReaderAddStatus.Added;

    /// <summary>标准 Probe 得到的设备型号，供添加页显示诊断结果。</summary>
    public string? Model { get; init; }

    /// <summary>标准 Probe 得到的固件版本，供添加页显示诊断结果。</summary>
    public string? Firmware { get; init; }

    /// <summary>标准 Probe 得到的厂商标识；不向 UI 暴露 SDK 或厂商类型。</summary>
    public uint? ManufacturerId { get; init; }

    /// <summary>标准 Probe 得到的型号标识。</summary>
    public uint? ModelId { get; init; }

    /// <summary>SDK 实际协商出的 LLRP 版本。</summary>
    public LlrpProtocolVersion? NegotiatedProtocolVersion { get; init; }

    /// <summary>标准 Probe 后匹配到的扩展模块稳定 Id；空集合表示走标准路径。</summary>
    public IReadOnlyList<string> MatchedExtensionIds { get; init; } = [];
}

public sealed record ReaderProbeResult(
    string? Model,
    string? Firmware,
    string? Error = null,
    uint? ManufacturerId = null,
    uint? ModelId = null,
    LlrpProtocolVersion? NegotiatedProtocolVersion = null)
{
    public bool Succeeded => Error is null;
}

public sealed record ReaderActivationResult(bool Succeeded, string? Error = null);
