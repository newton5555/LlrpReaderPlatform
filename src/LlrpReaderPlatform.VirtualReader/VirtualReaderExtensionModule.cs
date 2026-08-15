using System.Globalization;
using LlrpNet.Core.Protocol;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Services.Extensions;
using LlrpReaderPlatform.Services.Settings;
using LlrpSdk;

namespace LlrpReaderPlatform.VirtualReader;

/// <summary>
/// 将虚拟场景中保存的 TID/扩展字段投影回平台报告。
/// 虚拟设备本身仍只发标准 SDK TagReport，扩展字段通过明确的 virtual.* 键跨过 Services 扩展边界。
/// </summary>
public sealed class VirtualReaderExtensionModule(VirtualReaderCatalog catalog) : IReaderExtensionModule
{
    public const string VirtualTidField = "virtual.tid";
    public const string VirtualReaderIdField = "virtual.readerId";

    public string Id => "virtual-reader";

    public bool IsApplicable(ReaderProbeInfo info) =>
        info.ManufacturerId is { } manufacturer
        && catalog.GetAll().Any(dataset => dataset.Scenario.Identity.ManufacturerId == manufacturer);

    public IReadOnlyList<Feature> GetFeatures(ReaderProbeInfo info) => [];

    public void ConfigureBuilder(ReaderBuilderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
    }

    public ReaderTagReportProjection ProjectTagReport(TagReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? tid = null;

        if (report.Extensions is not null)
        {
            if (report.Extensions.TryGetValue(VirtualTidField, out object? tidValue))
            {
                tid = FormatValue(tidValue);
            }

            foreach ((string key, object? value) in report.Extensions)
            {
                if (!string.IsNullOrWhiteSpace(key) && value is not null)
                {
                    string? formatted = FormatValue(value);
                    if (!string.IsNullOrEmpty(formatted))
                    {
                        fields[key] = formatted;
                    }
                }
            }
        }

        return new ReaderTagReportProjection
        {
            TidHex = tid,
            Fields = fields,
        };
    }

    private static string? FormatValue(object? value) => value switch
    {
        null => null,
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
