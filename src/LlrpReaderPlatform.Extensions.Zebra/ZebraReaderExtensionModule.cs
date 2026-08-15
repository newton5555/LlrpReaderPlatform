using System.Collections.ObjectModel;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Contracts.Tagging;
using LlrpReaderPlatform.Services.Extensions;
using LlrpReaderPlatform.Services.Settings;
using LlrpNet.Core.Protocol;
using LlrpSdk;
using LlrpSdk.Extensions;
using LlrpSdk.Extensions.Zebra;

namespace LlrpReaderPlatform.Extensions.Zebra;

/// <summary>
/// Zebra Reader 扩展模块（实验性）。标准 Probe 匹配到 Zebra（厂商 161）且协商 1.0.1 时，
/// 在构建 SDK Reader 前启用 Zebra 协议扩展。仅 FX9600 已知画像开放设置项；未知 Zebra
/// 只注册协议扩展与报告投影，不尝试写入未标定的厂商参数（SDK 注明 ICG 与固件字节可信度风险）。
/// </summary>
public sealed class ZebraReaderExtensionModule : IReaderExtensionModule
{
    private static readonly ZebraSettingsContributor Settings = new();

    /// <summary>Zebra 的 LLRP 厂商标识（IANA 161）。</summary>
    public const uint ZebraManufacturerId = 161;

    /// <summary>实验基线：Zebra FX9600 的 ModelId（MOTOROLA 960008）。</summary>
    public const uint Fx9600ModelId = 96008;

    /// <summary>FX9600 已验证固件基线（能力参数 + Phase/BrandIDCheckStatus 已标定）。</summary>
    public const string VerifiedFx9600Firmware = "3.32.37.0";

    public const string PhaseField = "zebra.phase";
    public const string GpsField = "zebra.gps";
    public const string XpcField = "zebra.xpc";

    public string Id => "zebra";

    public ISettingsExtensionContributor? SettingsContributor => Settings;

    public bool IsApplicable(ReaderProbeInfo info) =>
        info.ManufacturerId == ZebraManufacturerId
        && info.ProtocolVersion == LlrpProtocolVersion.Version101;

    public IReadOnlyList<Feature> GetFeatures(ReaderProbeInfo info)
    {
        if (!IsApplicable(info))
        {
            return [];
        }

        if (!IsVerifiedFx9600(info))
        {
            return [ZebraFeatures.ReportPhase, ZebraFeatures.ReportGps, ZebraFeatures.ReportXpc];
        }

        return
        [
            ZebraFeatures.Configuration,
            ZebraFeatures.InventoryOptions,
            ZebraFeatures.ReportPhase,
            ZebraFeatures.ReportGps,
            ZebraFeatures.ReportXpc,
        ];
    }

    public void ConfigureBuilder(ReaderBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Builder.UseZebra();
    }

    public InventorySettings ApplyInventoryReportSpec(
        InventorySettings settings,
        IReadOnlyList<string> semanticFields,
        LlrpReaderPlatform.Contracts.Settings.ReaderFeatureCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(settings);
        bool wantsPhase = semanticFields.Contains(ReportFieldSemantics.Phase);
        bool wantsGps = semanticFields.Contains(ReportFieldSemantics.Gps);
        bool wantsXpc = semanticFields.Contains(ReportFieldSemantics.Xpc);
        // 能力门控（ADR-0013）：每个语义键都必须由该 Reader 的能力目录明确支持才写入。
        bool canPhase = catalog is not null && catalog.Supports(ZebraFeatures.ReportPhase);
        bool canGps = catalog is not null && catalog.Supports(ZebraFeatures.ReportGps);
        bool canXpc = catalog is not null && catalog.Supports(ZebraFeatures.ReportXpc);
        // 只要设备能力目录支持任一报告字段就写开关（true/false 都写），不能因“本次未请求”就短路
        // 返回——否则取消勾选相位时设备沿用已开启值，出现“可开不可关”。
        if (!canPhase && !canGps && !canXpc)
        {
            return settings;
        }

        // settings 即 InventorySettings：写入其 Extensions 字典（与设置贡献者 Apply 对齐）。
        var inventoryExtensions = new Dictionary<string, object?>(settings.Extensions, StringComparer.Ordinal);
        ZebraInventoryReportOptions existing = inventoryExtensions.TryGetValue(
            ZebraInventoryReportOptions.ExtensionKey, out object? value) &&
            value is ZebraInventoryReportOptions report
                ? report
                : new ZebraInventoryReportOptions();
        // 显式开/关：不请求的字段置 false，避免“取消请求”被静默忽略而沿用设备已开启值。
        inventoryExtensions[ZebraInventoryReportOptions.ExtensionKey] = existing with
        {
            IncludePhase = wantsPhase && canPhase,
            IncludeGps = wantsGps && canGps,
            // Zebra XPC 由 MLT 报告携带（EnableMLTReport），因此 XPC 对应 IncludeMltReport。
            IncludeMltReport = wantsXpc && canXpc,
        };

        return settings with
        {
            Extensions = new ReadOnlyDictionary<string, object?>(inventoryExtensions),
        };
    }

    public ReaderTagReportProjection ProjectTagReport(TagReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        short? phase = ZebraTagReportExtensions.GetPhase(report);
        if (phase is { } p)
        {
            string value = p.ToString(System.Globalization.CultureInfo.InvariantCulture);
            fields[PhaseField] = value;
            fields[ReportFieldSemantics.Phase] = value;
        }

        ZebraGpsCoordinates? gps = ZebraTagReportExtensions.GetGps(report);
        if (gps is not null)
        {
            string value = $"{gps.Longitude.ToString(System.Globalization.CultureInfo.InvariantCulture)};{gps.Latitude.ToString(System.Globalization.CultureInfo.InvariantCulture)};{gps.Altitude.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            fields[GpsField] = value;
            fields[ReportFieldSemantics.Gps] = value;
        }

        ZebraExtendedPc? xpc = ZebraTagReportExtensions.GetExtendedPc(report);
        if (xpc is not null)
        {
            string value = $"{xpc.XPC1:X4}{xpc.XPC2:X4}";
            fields[XpcField] = value;
            fields[ReportFieldSemantics.Xpc] = value;
        }

        IReadOnlyDictionary<string, object?> extensions = report.Extensions
            ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach ((string key, object? value) in extensions)
        {
            if (value is null || !key.StartsWith("zebra.", StringComparison.Ordinal))
            {
                continue;
            }

            fields[key] = value is System.IFormattable formattable
                ? formattable.ToString(null, System.Globalization.CultureInfo.InvariantCulture)
                : value.ToString() ?? string.Empty;
        }

        return new ReaderTagReportProjection { Fields = fields };
    }

    private static bool IsVerifiedFx9600(ReaderProbeInfo info) =>
        info.ModelId == Fx9600ModelId
        && string.Equals(info.Firmware, VerifiedFx9600Firmware, StringComparison.OrdinalIgnoreCase);
}
