using LlrpSdk;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Services.Settings;
using LlrpNet.Core.Protocol;

namespace LlrpReaderPlatform.Services.Extensions;

/// <summary>
/// 一次标准探测得到的厂商信息，供扩展模块判定是否适用。由 Session 的
/// <c>ReaderIdentity</c> 提取（可能为空，取决于设备）。
/// </summary>
public sealed record ReaderProbeInfo(
    uint? ManufacturerId,
    uint? ModelId,
    string? Firmware,
    string? Model,
    LlrpProtocolVersion? ProtocolVersion = null)
{
    public static ReaderProbeInfo FromIdentity(
        LlrpSdk.ReaderIdentity? identity,
        LlrpProtocolVersion? protocolVersion = null) => new(
        identity?.ManufacturerId,
        identity?.ModelId,
        identity?.FirmwareVersion,
        identity is null ? null : $"{identity.ManufacturerId}:{identity.ModelId}",
        protocolVersion);
}

/// <summary>
/// 构建期上下文：暴露 LlrpSdk 的 <c>LlrpReaderBuilder</c>，扩展模块在其中注册厂商协议扩展
/// （例如 Impinj 的 UseImpinj）。Services 只透传 builder，不感知具体厂商类型。
/// </summary>
public sealed class ReaderBuilderContext
{
    public ReaderBuilderContext(LlrpReaderBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        Builder = builder;
    }

    public LlrpReaderBuilder Builder { get; }
}

/// <summary>
/// 厂商 TagReport 投影结果。SDK/厂商对象只在 Services 扩展边界内出现，
/// 下游聚合和 UI 只接收字符串字段。
/// </summary>
public sealed record ReaderTagReportProjection
{
    public string? TidHex { get; init; }
    public IReadOnlyDictionary<string, string> Fields { get; init; } = EmptyFields;

    public static IReadOnlyDictionary<string, string> EmptyFields { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// 可插拔的厂商扩展模块。宿主在组合根显式注册（AddImpinjExtension 等）；
/// 服务层通过此抽象在标准探测匹配后应用扩展，不直接引用厂商包。
/// </summary>
public interface IReaderExtensionModule
{
    /// <summary>稳定唯一标识（如 "impinj"）。</summary>
    string Id { get; }

    /// <summary>根据标准探测到的厂商/型号信息判定本模块是否适用。</summary>
    bool IsApplicable(ReaderProbeInfo info);

    /// <summary>
    /// 返回本模块在该 Reader 上启用的稳定能力标识。默认不贡献能力，保证旧模块实现
    /// 可以逐步升级，而不会把厂商类型泄漏到 Contracts 或 WPF。
    /// </summary>
    IReadOnlyList<Feature> GetFeatures(ReaderProbeInfo info) => [];

    /// <summary>
    /// 模块自带的设置贡献者。宿主不必为同一个厂商模块重复注册第二个 DI 服务；
    /// StandardSettingsCompiler 会把它与兼容的独立贡献者按 Id 去重。
    /// </summary>
    ISettingsExtensionContributor? SettingsContributor => null;

    /// <summary>在创建/连接 SDK Reader 前注册协议扩展（例如 Impinj 的 Builder 配置）。</summary>
    void ConfigureBuilder(ReaderBuilderContext context);

    /// <summary>可选的 TagReport 扩展字段投影。</summary>
    ReaderTagReportProjection ProjectTagReport(TagReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return new() { TidHex = GetTidHex(report) };
    }

    /// <summary>
    /// 兼容旧扩展实现的 TID 投影钩子。新扩展应优先实现
    /// <see cref="ProjectTagReport"/>，以便同时贡献多个 UI 无关字段。
    /// </summary>
    string? GetTidHex(TagReport report) => null;
}
