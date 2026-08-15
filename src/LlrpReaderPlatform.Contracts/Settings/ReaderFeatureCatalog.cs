using LlrpReaderPlatform.Contracts.Readers;

namespace LlrpReaderPlatform.Contracts.Settings;

/// <summary>
/// 能力标识（厂商无关）。由标准 LLRP 能力或厂商扩展模块贡献，
/// 用于表达"该设备支持了什么"，从而驱动设置布局的显示/只读/可选值。
/// 相等性仅基于 <see cref="Id"/> + <see cref="Vendor"/>，语义键与毕业元数据不参与比较。
/// （ADR-0012：语义能力键 + 标准优先仲裁 + 毕业机制）
/// </summary>
public readonly record struct Feature
{
    [System.Text.Json.Serialization.JsonConstructor]
    public Feature(
        string id,
        string? vendor = null,
        string? semanticId = null,
        LlrpProtocolVersion? standardizedSince = null)
    {
        Id = id;
        Vendor = vendor;
        SemanticId = semanticId ?? id;
        StandardizedSince = standardizedSince;
    }

    /// <summary>能力 ID（在同一厂商命名空间内唯一）。</summary>
    public string Id { get; }

    /// <summary>贡献厂商；null 表示标准轴。</summary>
    public string? Vendor { get; }

    /// <summary>跨厂商、跨协议版本的稳定语义键；UI 与仲裁只认该键。</summary>
    public string SemanticId { get; }

    /// <summary>吸收该语义的标准协议版本；厂商在设备协商版本 ≥ 此值时让位给标准轴。尚无标准化的为 null。</summary>
    public LlrpProtocolVersion? StandardizedSince { get; }

    public bool IsVendor => !string.IsNullOrEmpty(Vendor);

    public override string ToString() => IsVendor ? $"{Vendor}:{Id}" : Id;

    public bool Equals(Feature other) => Id == other.Id && Vendor == other.Vendor;

    public override int GetHashCode() => HashCode.Combine(Id, Vendor);
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
    public static readonly Feature StandardBlockTagAccess = new("standard.block-tag-access", semanticId: "block-tag-access");

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

    /// <summary>是否存在语义键等于指定值的已仲裁能力（ADR-0012）。用于 UI 按语义键判断可用性。</summary>
    public bool SupportsSemantic(string semanticId) =>
        SupportedFeatures.Any(f => string.Equals(f.SemanticId, semanticId, StringComparison.Ordinal));

    public ReaderFeatureCatalog Add(params Feature[] features) => new()
    {
        SupportedFeatures = SupportedFeatures
            .Concat(features)
            .Distinct()
            .ToArray(),
    };
}
