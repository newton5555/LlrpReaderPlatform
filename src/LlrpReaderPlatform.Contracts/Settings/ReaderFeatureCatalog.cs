namespace LlrpReaderPlatform.Contracts.Settings;

/// <summary>
/// 能力标识（厂商无关）。由标准 LLRP 能力或厂商扩展模块贡献，
/// 用于表达"该设备支持什么"，从而驱动设置布局的显示/只读/可选值。
/// </summary>
public readonly record struct Feature(string Id, string? Vendor = null)
{
    public bool IsVendor => !string.IsNullOrEmpty(Vendor);

    public override string ToString() => IsVendor ? $"{Vendor}:{Id}" : Id;
}

/// <summary>
/// 平台内置的稳定能力标识。能力标识是跨 UI 的语义，不等同于某个 SDK 属性或控件。
/// 厂商模块可以通过 <see cref="Vendor"/> 命名空间贡献额外能力。
/// </summary>
public static class ReaderFeatures
{
    public static readonly Feature StandardSettings = new("standard.settings");
    public static readonly Feature StandardInventory = new("standard.inventory");
    public static readonly Feature StandardTagAccess = new("standard.tag-access");
    public static readonly Feature StandardGpi = new("standard.gpi");
    public static readonly Feature StandardGpo = new("standard.gpo");
    public static readonly Feature StandardRf = new("standard.rf");
    public static readonly Feature StandardFrequency = new("standard.frequency");
    public static readonly Feature StandardStateAwareSingulation = new("standard.state-aware-singulation");

    public static readonly Feature ImpinjFastId = new("fast-id", "impinj");
    public static readonly Feature ImpinjRfPhase = new("rf-phase", "impinj");
    public static readonly Feature ImpinjDoppler = new("doppler", "impinj");
    public static readonly Feature ImpinjSearchMode = new("search-mode", "impinj");
    public static readonly Feature ImpinjLowDutyCycle = new("low-duty-cycle", "impinj");
    public static readonly Feature ImpinjFixedFrequency = new("fixed-frequency", "impinj");
    public static readonly Feature ImpinjGpiDebounce = new("gpi-debounce", "impinj");
}

/// <summary>
/// 由 ReaderCapabilities + 激活的扩展模块聚合出的能力目录，决定该设备支持什么。
/// 只存内存，不持久化。
/// </summary>
public sealed class ReaderFeatureCatalog
{
    public required IReadOnlyList<Feature> SupportedFeatures { get; init; }

    public static ReaderFeatureCatalog Empty { get; } = new() { SupportedFeatures = [] };

    /// <summary>
    /// 是否已经收到平台标准能力基线。空目录或仅包含扩展能力时视为未知，
    /// 这样离线/旧测试替身不会被误判为明确不支持。
    /// </summary>
    public bool HasStandardCapabilityBaseline =>
        Supports(ReaderFeatures.StandardSettings)
        || Supports(ReaderFeatures.StandardInventory);

    public bool Supports(Feature feature) => SupportedFeatures.Contains(feature);

    public bool SupportsOrUnknown(Feature feature) =>
        !HasStandardCapabilityBaseline || Supports(feature);

    public ReaderFeatureCatalog Add(params Feature[] features) => new()
    {
        SupportedFeatures = SupportedFeatures
            .Concat(features)
            .Distinct()
            .ToArray(),
    };
}
