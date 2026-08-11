using LlrpReaderPlatform.Contracts.Settings;

namespace LlrpReaderPlatform.Contracts.Readers;

/// <summary>Reader 的运行时连接/操作状态（不持久化，仅内存）。</summary>
public enum ReaderState
{
    Disconnected,
    Connecting,
    Connected,
    Stopping,
    Disconnecting,
    Inventorying,
    Faulted,
}

/// <summary>
/// Reader 运行时快照副本。每次激活/连接更新；断开后保留本进程最后一次成功激活得到的能力，
/// 并标注 <see cref="IsStale"/>/<see cref="CapturedAt"/>。只存内存，不写入持久化。
/// </summary>
public sealed record ReaderRuntimeSnapshot
{
    public required Guid ReaderId { get; init; }
    public required ReaderProfile Profile { get; init; }
    public required ReaderState State { get; init; }
    public string? Model { get; init; }
    public uint? ManufacturerId { get; init; }
    public uint? ModelId { get; init; }
    public string? Firmware { get; init; }
    /// <summary>最近一次成功连接实际协商出的 LLRP 版本。</summary>
    public LlrpProtocolVersion? NegotiatedProtocolVersion { get; init; }
    public string? Error { get; init; }
    public bool IsEnabled { get; init; }

    /// <summary>最近一次成功激活能力捕获的时间；null 表示尚未成功激活。</summary>
    public DateTimeOffset? CapturedAt { get; init; }

    /// <summary>能力是否为上次连接的陈旧副本（进程重启后、重新激活前为 true）。</summary>
    public bool IsStale { get; init; } = true;

    /// <summary>能力快照版本号，用于设置 Draft 保存前的能力复核（CapabilityRevision）。</summary>
    public long CapabilityRevision { get; init; }

    /// <summary>标准天线能力（激活时填充；用于能力驱动设置布局）。</summary>
    public IReadOnlyList<ReaderAntennaInfo> Antennas { get; init; } = [];

    /// <summary>标准 GPIO 数量；null 表示设备未返回可识别的 GPIO 能力参数。</summary>
    public ushort? GpiCount { get; init; }
    public ushort? GpoCount { get; init; }

    /// <summary>最近一次成功激活得到的能力目录；断开后随能力快照保留并标记为陈旧。</summary>
    public ReaderFeatureCatalog FeatureCatalog { get; init; } = ReaderFeatureCatalog.Empty;

    /// <summary>当前 Session 选择的扩展模块稳定 Id；空集合表示标准路径。</summary>
    public IReadOnlyList<string> ActiveExtensionIds { get; init; } = [];
}

/// <summary>一次性能力捕获（激活/短连接探测得到，用于填充内存缓存）。</summary>
public sealed record ReaderCapabilityCapture
{
    public required Guid ReaderId { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? Model { get; init; }
    public uint? ManufacturerId { get; init; }
    public uint? ModelId { get; init; }
    public string? Firmware { get; init; }
    public LlrpProtocolVersion? NegotiatedProtocolVersion { get; init; }
    public required long Revision { get; init; }

    /// <summary>标准 LLRP 能力（天线数、支持的 RF 参数等）。具体字段由能力目录逐步填充。</summary>
    public IReadOnlyList<ReaderAntennaInfo> Antennas { get; init; } = [];

    public ushort? GpiCount { get; init; }
    public ushort? GpoCount { get; init; }

    public ReaderFeatureCatalog FeatureCatalog { get; init; } = ReaderFeatureCatalog.Empty;

    /// <summary>捕获能力时使用的扩展模块稳定 Id；不持久化。</summary>
    public IReadOnlyList<string> ActiveExtensionIds { get; init; } = [];
}

public sealed record ReaderAntennaInfo
{
    public required ushort AntennaId { get; init; }
    public string? Name { get; init; }
}
