using LlrpReaderPlatform.Contracts.Readers;

namespace LlrpReaderPlatform.VirtualReader;

/// <summary>
/// 可被版本化保存的虚拟 Reader 场景。场景只描述设备和数据源，
/// 不把 WPF 控件或 SDK 对象写入文件。
/// </summary>
public sealed record VirtualReaderScenario
{
    public int SchemaVersion { get; init; } = 1;
    public string Name { get; init; } = "Virtual Reader";
    public Guid ReaderId { get; init; } = Guid.NewGuid();
    public string ReaderName { get; init; } = "Virtual Reader";
    public string Host { get; init; } = "virtual-reader";
    public int Port { get; init; } = 5084;
    public LlrpProtocolVersionOption ProtocolVersion { get; init; } = LlrpProtocolVersionOption.Force101;
    public VirtualReaderIdentity Identity { get; init; } = new();
    public VirtualReaderCapabilities Capabilities { get; init; } = new();
    public VirtualInventorySource Inventory { get; init; } = new();
    public VirtualReplayOptions Replay { get; init; } = new();
    public IReadOnlyList<VirtualTagMemorySeed> TagMemory { get; init; } = [];
    public VirtualReaderFaultProfile Faults { get; init; } = new();

    public ReaderProfile ToReaderProfile() => new()
    {
        Id = ReaderId,
        Name = ReaderName,
        Host = Host,
        Port = Port,
        LlrpVersion = ProtocolVersion,
        IsEnabled = true,
    };
}

public sealed record VirtualReaderIdentity
{
    public uint ManufacturerId { get; init; } = 0x56495254;
    public uint ModelId { get; init; } = 1;
    public string Firmware { get; init; } = "virtual-1.0";
    public string Model { get; init; } = "Virtual Reader";
}

public sealed record VirtualReaderCapabilities
{
    public ushort MaxAntennas { get; init; } = 4;
    public bool RequireExplicitAntennaIds { get; init; } = true;
    public ushort GpiCount { get; init; }
    public ushort GpoCount { get; init; } = 4;
    public bool TagAccessAvailable { get; init; } = true;
    public bool BlockEraseAvailable { get; init; } = true;
    public IReadOnlyList<ushort> TxPowerIndices { get; init; } = [1, 2, 3, 4];
    public IReadOnlyList<ushort> RxSensitivityIndices { get; init; } = [1, 2, 3, 4];
    public IReadOnlyList<ushort> RfModeIndices { get; init; } = [0, 20];
}

/// <summary>
/// 现有真实机台数据目录或文件。JSONL 是逐条回放的首选，snapshot 是聚合结果兜底。
/// 路径相对于场景文件所在目录解析。
/// </summary>
public sealed record VirtualInventorySource
{
    public string? TagLogPath { get; init; }
    public string? SnapshotPath { get; init; }
}

public enum VirtualReplayMode
{
    RealTime = 0,
    Accelerated = 1,
    Step = 2,
    Loop = 3,
}

public sealed record VirtualReplayOptions
{
    public VirtualReplayMode Mode { get; init; } = VirtualReplayMode.RealTime;
    public double Speed { get; init; } = 1.0;
    public int FallbackIntervalMilliseconds { get; init; } = 50;
    public bool Loop { get; init; }
}

public sealed record VirtualTagMemorySeed
{
    public required string Epc { get; init; }
    public string? TidHex { get; init; }
    public string? ReservedHex { get; init; }
    public string? UserHex { get; init; }
    public string? AccessPasswordHex { get; init; }
    public bool UserWritable { get; init; } = true;
}

public sealed record VirtualReaderFaultProfile
{
    public bool FailConnect { get; init; }
    public bool FailSettingsQuery { get; init; }
    public bool FailSettingsApply { get; init; }
    public bool FailInventoryStart { get; init; }
    public bool CloseConnectionOnInventoryStart { get; init; }
    public int ResponseDelayMilliseconds { get; init; }
}
