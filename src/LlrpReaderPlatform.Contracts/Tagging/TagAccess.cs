using LlrpReaderPlatform.Contracts.Errors;

namespace LlrpReaderPlatform.Contracts.Tagging;

/// <summary>Gen2 标签内存段。</summary>
public enum TagMemoryBank
{
    Reserved = 0,
    Epc = 1,
    Tid = 2,
    User = 3,
}

/// <summary>读标签内存请求（平台 DTO，EPC 十六进制字符串）。</summary>
public sealed record TagReadRequest
{
    /// <summary>目标匹配数据。默认按 EPC 十六进制值解释，兼容旧调用方。</summary>
    public required string Epc { get; init; }
    /// <summary>目标匹配所在的标签存储区；EPC 从 bit 32 开始，TID 从 bit 0 开始。</summary>
    public TagMemoryBank SelectionBank { get; init; } = TagMemoryBank.Epc;
    public TagMemoryBank MemoryBank { get; init; } = TagMemoryBank.Epc;
    public ushort OffsetWords { get; init; }
    public ushort WordCount { get; init; } = 1;
    public ushort? AntennaId { get; init; }
    public string? AccessPasswordHex { get; init; }
}

/// <summary>写标签内存请求（平台 DTO）。</summary>
public sealed record TagWriteRequest
{
    /// <summary>目标匹配数据。默认按 EPC 十六进制值解释，兼容旧调用方。</summary>
    public required string Epc { get; init; }
    /// <summary>目标匹配所在的标签存储区；EPC 从 bit 32 开始，TID 从 bit 0 开始。</summary>
    public TagMemoryBank SelectionBank { get; init; } = TagMemoryBank.Epc;
    public TagMemoryBank MemoryBank { get; init; } = TagMemoryBank.Epc;
    public ushort OffsetWords { get; init; }

    /// <summary>要写入的数据（十六进制，字数 2 字节/字）。</summary>
    public required string DataHex { get; init; }
    public ushort? AntennaId { get; init; }
    public string? AccessPasswordHex { get; init; }
}

/// <summary>TagAccess 操作结果。</summary>
public sealed record TagAccessResult(bool Succeeded, string? Error = null, string? DataHex = null)
{
    public PlatformErrorCode ErrorCode { get; init; } = Succeeded
        ? PlatformErrorCode.None
        : PlatformErrorCode.DeviceFailed;
}

/// <summary>GPI/GPO 控制命令（平台 DTO）。</summary>
public sealed record GpioCommand
{
    public required ushort PortNumber { get; init; }
    public required bool State { get; init; }
}

/// <summary>Reader GPI 当前状态的 UI 无关投影。</summary>
public sealed record GpiPortStatus
{
    public required ushort PortNumber { get; init; }
    public bool Configured { get; init; }
    public bool State { get; init; }
}

/// <summary>Reader GPO 当前状态的 UI 无关投影。</summary>
public sealed record GpoPortStatus
{
    public required ushort PortNumber { get; init; }
    public bool State { get; init; }
}

/// <summary>同一次 Reader 配置查询得到的 GPI/GPO 状态快照。</summary>
public sealed record GpioStatusSnapshot
{
    public required IReadOnlyList<GpiPortStatus> Gpis { get; init; }
    public required IReadOnlyList<GpoPortStatus> Gpos { get; init; }
}

public sealed class GpiObservedEventArgs(Guid readerId, GpiPortStatus status) : EventArgs
{
    public Guid ReaderId { get; } = readerId;
    public GpiPortStatus Status { get; } = status;
}
