using LlrpReaderPlatform.Services.Extensions;
using LlrpReaderPlatform.Services.Settings;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpNet.Core.Protocol;
using LlrpSdk;
using LlrpSdk.Extensions;
using LlrpSdk.Extensions.Impinj;
using System.Collections.ObjectModel;
using System.Globalization;

namespace LlrpReaderPlatform.Extensions.Impinj;

/// <summary>
/// Impinj reader 扩展模块。由宿主在组合根通过 <c>AddImpinjExtension()</c> 显式注册。
/// 标准探测匹配到 Impinj 设备后，在构建 SDK Reader 前启用 Impinj 协议扩展。
/// </summary>
public sealed class ImpinjReaderExtensionModule : IReaderExtensionModule
{
    private static readonly ImpinjSettingsContributor Settings = new();
    /// <summary>Impinj 的 LLRP 厂商标识；已由 Impinj R420 的标准 Probe 实测校准。</summary>
    public const uint ImpinjManufacturerId = 0x651A;

    /// <summary>首批 L4 真机基线：Impinj R420 的 ModelId。</summary>
    public const uint R420ModelId = 2001002;

    public const string SerializedTidField = "impinj.serializedTid";

    public string Id => "impinj";

    public ISettingsExtensionContributor SettingsContributor => Settings;

    public bool IsApplicable(ReaderProbeInfo info) =>
        info.ManufacturerId is { } manufacturer && manufacturer == ImpinjManufacturerId;

    public IReadOnlyList<Feature> GetFeatures(ReaderProbeInfo info)
    {
        if (!IsR420(info))
        {
            return [];
        }

        ImpinjInventoryCapabilities? capabilities = GetVerifiedCapabilities(info);
        if (capabilities is null)
        {
            return [];
        }

        var features = new List<Feature>();
        if (capabilities.SupportsSerializedTid)
        {
            features.Add(ReaderFeatures.ImpinjFastId);
        }

        if (capabilities.SupportsRfPhaseAngle)
        {
            features.Add(ReaderFeatures.ImpinjRfPhase);
        }

        if (capabilities.SupportsRfDopplerFrequency)
        {
            features.Add(ReaderFeatures.ImpinjDoppler);
        }

        // The current SDK capability catalog has no separate flags for these settings.
        // Expose them only when the firmware itself is a verified R420 profile; an unknown
        // firmware must not inherit L4 controls from the model name alone.
        if (capabilities.SupportsTagReportContentSelector)
        {
            features.AddRange([
                ReaderFeatures.ImpinjSearchMode,
                ReaderFeatures.ImpinjLowDutyCycle,
                ReaderFeatures.ImpinjFixedFrequency,
                ReaderFeatures.ImpinjGpiDebounce,
            ]);
        }

        return features;
    }

    public static bool IsR420(ReaderProbeInfo info) =>
        IsApplicableForManufacturer(info)
        && info.ModelId == R420ModelId;

    private static bool IsApplicableForManufacturer(ReaderProbeInfo info) =>
        info.ManufacturerId is { } manufacturer && manufacturer == ImpinjManufacturerId;

    private static ImpinjInventoryCapabilities? GetVerifiedCapabilities(ReaderProbeInfo info)
    {
        if (!IsR420(info) || string.IsNullOrWhiteSpace(info.Firmware))
        {
            return null;
        }

        try
        {
            var context = new ReaderExtensionMatchContext(
                info.ManufacturerId!.Value,
                info.ModelId!.Value,
                info.Firmware,
                info.ProtocolVersion ?? LlrpProtocolVersion.Version101);
            return ImpinjInventoryCapabilityCatalog.Get(context);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void ConfigureBuilder(ReaderBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Builder.UseImpinj();
    }

    public InventorySettings ApplyInventoryReportSpec(
        InventorySettings settings,
        IReadOnlyList<string> semanticFields,
        LlrpReaderPlatform.Contracts.Settings.ReaderFeatureCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // 能力门控（ADR-0013）：仅当请求含 phase-report 且该 Reader 已仲裁支持 RF Phase 时才写入。
        if (!semanticFields.Contains(LlrpReaderPlatform.Contracts.Tagging.ReportFieldSemantics.Phase)
            || catalog is null
            || !catalog.Supports(ReaderFeatures.ImpinjRfPhase))
        {
            return settings;
        }

        // R420 已实测：RF Phase angle 属于 ImpinjInventoryReportOptions 扩展字典。
        var extensions = new Dictionary<string, object?>(settings.Extensions, StringComparer.Ordinal);
        ImpinjInventoryReportOptions existing = extensions.TryGetValue(
            ImpinjInventoryReportOptions.ExtensionKey, out object? value) &&
            value is ImpinjInventoryReportOptions report
                ? report
                : new ImpinjInventoryReportOptions();
        extensions[ImpinjInventoryReportOptions.ExtensionKey] = existing with
        {
            IncludeRfPhaseAngle = true,
        };
        return settings with
        {
            Extensions = new ReadOnlyDictionary<string, object?>(extensions),
        };
    }

    public string? GetTidHex(TagReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return ImpinjTagReportExtensions.GetSerializedTidHex(report);
    }

    public ReaderTagReportProjection ProjectTagReport(TagReport report)
    {
        string? tid = GetTidHex(report);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        // Keep the extension boundary deliberately narrow: vendor objects stay in the
        // module, while downstream services receive stable string fields only. This also
        // preserves future Impinj report fields without changing Contracts for each SDK type.
        IReadOnlyDictionary<string, object?> extensions = report.Extensions
            ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach ((string key, object? value) in extensions)
        {
            if (!string.IsNullOrWhiteSpace(key) && value is not null)
            {
                string? formatted = FormatExtensionValue(value);
                if (!string.IsNullOrEmpty(formatted))
                {
                    fields[key] = formatted;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(tid))
        {
            fields[SerializedTidField] = tid;
        }

        return new ReaderTagReportProjection
        {
            TidHex = tid,
            Fields = fields,
        };
    }

    private static string? FormatExtensionValue(object value) =>
        value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value.ToString();
}
