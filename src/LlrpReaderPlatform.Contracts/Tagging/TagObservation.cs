namespace LlrpReaderPlatform.Contracts.Tagging;

/// <summary>聚合后的标签观测（平台 DTO，UI 无关）。</summary>
public sealed record TagObservation
{
    public required string Epc { get; init; }
    public string Tid { get; init; } = string.Empty;
    public ushort? PcBits { get; init; }
    public string? PcBitsHex { get; init; }
    public long ReadCount { get; init; }
    public DateTimeOffset FirstSeen { get; init; }
    public DateTimeOffset LastSeen { get; init; }
    public sbyte? LastRssi { get; init; }
    public ushort? LastChannelIndex { get; init; }
    public ushort? LastAntenna { get; init; }

    /// <summary>
    /// 厂商或扩展模块贡献的字符串字段。键和值均为平台语义，不能携带 SDK 对象。
    /// </summary>
    public IReadOnlyDictionary<string, string> ExtensionFields { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>寻卡报告中标准字段的请求覆盖。null 表示沿用 Reader 当前设置。</summary>
public sealed record InventoryReportSpec
{
    /// <summary>本次寻卡的设备报告批次；UI 默认按 1 个标签实时报告。</summary>
    public ushort? ReportEveryNTags { get; init; }

    public bool? IncludeAntennaId { get; init; }
    public bool? IncludeChannelIndex { get; init; }
    public bool? IncludePeakRssi { get; init; }
    public bool? IncludeFirstSeenTimestamp { get; init; }
    public bool? IncludeLastSeenTimestamp { get; init; }
    public bool? IncludeTagSeenCount { get; init; }
    public bool? IncludePcBits { get; init; }
}

/// <summary>寻卡启动参数（平台 DTO）。</summary>
public sealed record InventorySpec
{
    /// <summary>限定的天线集合；空表示使用 Reader 当前配置的全部天线。</summary>
    public IReadOnlyList<ushort> Antennas { get; init; } = [];

    public int? DurationSeconds { get; init; }

    /// <summary>开始本次寻卡时覆盖的报告字段；不携带时沿用设备当前报告配置。</summary>
    public InventoryReportSpec? Report { get; init; }
}
