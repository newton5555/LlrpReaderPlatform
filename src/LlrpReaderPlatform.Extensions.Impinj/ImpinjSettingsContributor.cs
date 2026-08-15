using System.Collections.ObjectModel;
using System.Globalization;
using LlrpNet.Protocol.Impinj.Enumerations.V1_0_1;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Services.Settings;
using LlrpSdk;
using LlrpSdk.Extensions.Impinj;

namespace LlrpReaderPlatform.Extensions.Impinj;

/// <summary>
/// Impinj 设置贡献者。它只在标准 Probe 已识别为 Impinj 时向能力驱动布局增加字段，
/// 并在同一个 Settings Apply 租约中把平台值编译回 Impinj extension dictionary。
/// </summary>
public sealed class ImpinjSettingsContributor : ISettingsExtensionContributor
{
    public const string SearchMode = "impinj.search-mode";
    public const string FastId = "impinj.fast-id";
    public const string PhaseAngle = "impinj.phase-angle";
    public const string Doppler = "impinj.doppler";
    public const string LowDutyCycle = "impinj.low-duty-cycle";
    public const string EmptyFieldTimeout = "impinj.empty-field-timeout-ms";
    public const string FieldPingInterval = "impinj.field-ping-interval-ms";
    public const string FixedFrequencyMode = "impinj.fixed-frequency-mode";
    public const string FixedFrequencyChannels = "impinj.fixed-frequency-channels";

    public static string GpiDebounce(ushort port) => $"impinj.gpi-debounce-ms.{port}";

    public string Id => "impinj-settings";

    public bool IsApplicable(ReaderRuntimeSnapshot reader) =>
        reader.FeatureCatalog.SupportedFeatures.Any(static feature =>
            feature.IsVendor
            && string.Equals(feature.Vendor, "impinj", StringComparison.Ordinal));

    public void ContributeLayout(
        IList<SettingsEntry> entries,
        ReaderRuntimeSnapshot reader,
        ReaderSettingsRuntimeSnapshot runtime)
    {
        InventorySettings inventory = runtime.Settings.ManagedRoSpec?.Inventory
            ?? runtime.Settings.Settings.Inventory
            ?? new InventorySettings();
        var configuration = runtime.Settings.Settings.Configuration.Extensions.TryGetValue(
            ImpinjReaderConfiguration.ExtensionKey, out object? configurationValue)
            ? configurationValue as ImpinjReaderConfiguration
            : null;
        IReadOnlyDictionary<string, object?> extensionValues = inventory.Extensions;
        var report = extensionValues.TryGetValue(ImpinjInventoryReportOptions.ExtensionKey, out object? reportValue)
            ? reportValue as ImpinjInventoryReportOptions
            : null;
        var control = extensionValues.TryGetValue(ImpinjInventoryControlOptions.ExtensionKey, out object? controlValue)
            ? controlValue as ImpinjInventoryControlOptions
            : null;

        if (Supports(reader, ReaderFeatures.ImpinjFastId))
        {
            entries.Add(new SettingsEntry
            {
                Key = FastId,
                Title = "Impinj FastID / serialized TID",
                EditorKind = EditorKind.Boolean,
                ValueType = typeof(bool),
                CurrentValue = report?.IncludeSerializedTid ?? false,
                DefaultValue = false,
                Source = SettingsSource.VendorExtension,
            });
        }

        if (Supports(reader, ReaderFeatures.ImpinjGpiDebounce))
        {
            for (int port = 1; port <= ResolveGpiPortCount(reader); port++)
            {
                entries.Add(new SettingsEntry
                {
                    Key = GpiDebounce(checked((ushort)port)),
                    Title = $"Impinj GPI {port} debounce (ms)",
                    EditorKind = EditorKind.Integer,
                    ValueType = typeof(int),
                    Range = new SettingsRange(0, int.MaxValue),
                    CurrentValue = configuration?.GpiDebounce.FirstOrDefault(x => x.GpiPortNumber == port)?.DebounceMilliseconds ?? 0,
                    DefaultValue = 0,
                    Source = SettingsSource.VendorExtension,
                });
            }
        }

        if (Supports(reader, ReaderFeatures.ImpinjRfPhase))
        {
            entries.Add(new SettingsEntry
            {
                Key = PhaseAngle,
                Title = "Impinj RF phase angle",
                EditorKind = EditorKind.Boolean,
                ValueType = typeof(bool),
                CurrentValue = report?.IncludeRfPhaseAngle ?? false,
                DefaultValue = false,
                Source = SettingsSource.VendorExtension,
                // ADR-0013：相位已由寻卡页列开关联动控制，设置页只读并提示。
                ReadOnlyReason = "由寻卡页联动控制",
            });
        }

        if (Supports(reader, ReaderFeatures.ImpinjDoppler))
        {
            entries.Add(new SettingsEntry
            {
                Key = Doppler,
                Title = "Impinj RF Doppler",
                EditorKind = EditorKind.Boolean,
                ValueType = typeof(bool),
                CurrentValue = report?.IncludeRfDopplerFrequency ?? false,
                DefaultValue = false,
                Source = SettingsSource.VendorExtension,
            });
        }

        if (Supports(reader, ReaderFeatures.ImpinjSearchMode))
        {
            entries.Add(new SettingsEntry
            {
                Key = SearchMode,
                Title = "Impinj inventory search mode",
                EditorKind = EditorKind.Choice,
                ValueType = typeof(int),
                Options = SearchModeOptions(),
                CurrentValue = control?.InventorySearchMode is { } search ? (int)search : -1,
                DefaultValue = -1,
                Source = SettingsSource.VendorExtension,
            });
        }

        if (Supports(reader, ReaderFeatures.ImpinjLowDutyCycle))
        {
            entries.Add(new SettingsEntry
            {
                Key = LowDutyCycle,
                Title = "Impinj low duty cycle",
                EditorKind = EditorKind.Boolean,
                ValueType = typeof(bool),
                CurrentValue = control?.LowDutyCycle?.Mode == ImpinjLowDutyCycleMode.Enabled,
                DefaultValue = false,
                Source = SettingsSource.VendorExtension,
            });
            entries.Add(new SettingsEntry
            {
                Key = EmptyFieldTimeout,
                Title = "Impinj empty-field timeout (ms)",
                EditorKind = EditorKind.Integer,
                ValueType = typeof(int),
                Range = new SettingsRange(0, ushort.MaxValue),
                CurrentValue = control?.LowDutyCycle?.EmptyFieldTimeoutMilliseconds ?? 500,
                DefaultValue = 500,
                Source = SettingsSource.VendorExtension,
            });
            entries.Add(new SettingsEntry
            {
                Key = FieldPingInterval,
                Title = "Impinj field-ping interval (ms)",
                EditorKind = EditorKind.Integer,
                ValueType = typeof(int),
                Range = new SettingsRange(0, ushort.MaxValue),
                CurrentValue = control?.LowDutyCycle?.FieldPingIntervalMilliseconds ?? 200,
                DefaultValue = 200,
                Source = SettingsSource.VendorExtension,
            });
        }

        if (Supports(reader, ReaderFeatures.ImpinjFixedFrequency))
        {
            IReadOnlyList<SettingsOption> frequencyOptions =
            [
                new(-1, "Disabled"),
                new((int)ImpinjFixedFrequencyMode.Auto_Select, "Auto select"),
                new((int)ImpinjFixedFrequencyMode.Channel_List, "Channel list"),
            ];
            entries.Add(new SettingsEntry
            {
                Key = FixedFrequencyMode,
                Title = "Impinj fixed frequency mode",
                EditorKind = EditorKind.Choice,
                ValueType = typeof(int),
                Options = frequencyOptions,
                CurrentValue = control?.FixedFrequency is { } fixedFrequency ? (int)fixedFrequency.Mode : -1,
                DefaultValue = -1,
                Source = SettingsSource.VendorExtension,
            });
            entries.Add(new SettingsEntry
            {
                Key = FixedFrequencyChannels,
                Title = "Impinj fixed frequency channels",
                EditorKind = BuildFrequencyOptions(runtime).Count > 0 ? EditorKind.Collection : EditorKind.Text,
                ValueType = typeof(string),
                Options = BuildFrequencyOptions(runtime),
                CurrentValue = control?.FixedFrequency is { } channelsSettings
                    ? string.Join(",", channelsSettings.ChannelList)
                    : string.Empty,
                DefaultValue = string.Empty,
                Source = SettingsSource.VendorExtension,
            });
        }
    }

    public ReaderSettings Apply(
        SettingsDraft draft,
        EffectiveSettingsLayout layout,
        ReaderRuntimeSnapshot reader,
        ReaderSettingsRuntimeSnapshot runtime,
        ReaderSettings settings)
    {
        IReadOnlyDictionary<string, object?> sourceExtensions = settings.Inventory?.Extensions
            ?? new Dictionary<string, object?>();
        var inventoryExtensions = new Dictionary<string, object?>(sourceExtensions, StringComparer.Ordinal);
        ImpinjInventoryReportOptions existingReport = inventoryExtensions.TryGetValue(
            ImpinjInventoryReportOptions.ExtensionKey, out object? reportValue) &&
            reportValue is ImpinjInventoryReportOptions report
                ? report
                : new ImpinjInventoryReportOptions();
        inventoryExtensions[ImpinjInventoryReportOptions.ExtensionKey] = existingReport with
        {
            IncludeSerializedTid = Supports(reader, ReaderFeatures.ImpinjFastId)
                ? GetBool(draft, FastId, existingReport.IncludeSerializedTid)
                : false,
            IncludeRfPhaseAngle = Supports(reader, ReaderFeatures.ImpinjRfPhase)
                ? GetBool(draft, PhaseAngle, existingReport.IncludeRfPhaseAngle)
                : false,
            IncludeRfDopplerFrequency = Supports(reader, ReaderFeatures.ImpinjDoppler)
                ? GetBool(draft, Doppler, existingReport.IncludeRfDopplerFrequency)
                : false,
        };

        ImpinjInventoryControlOptions existingControl = inventoryExtensions.TryGetValue(
            ImpinjInventoryControlOptions.ExtensionKey, out object? controlValue) &&
            controlValue is ImpinjInventoryControlOptions control
                ? control
                : new ImpinjInventoryControlOptions();
        int searchMode = GetInt(draft, SearchMode, -1);
        int frequencyMode = GetInt(draft, FixedFrequencyMode, -1);
        existingControl = existingControl with
        {
            InventorySearchMode = Supports(reader, ReaderFeatures.ImpinjSearchMode) && searchMode >= 0
                ? (ImpinjInventorySearchType)searchMode
                : null,
            LowDutyCycle = Supports(reader, ReaderFeatures.ImpinjLowDutyCycle) && GetBool(draft, LowDutyCycle)
                ? new ImpinjLowDutyCycleSettings(
                    ImpinjLowDutyCycleMode.Enabled,
                    checked((ushort)GetInt(draft, EmptyFieldTimeout, 500)),
                    checked((ushort)GetInt(draft, FieldPingInterval, 200)))
                : null,
            FixedFrequency = Supports(reader, ReaderFeatures.ImpinjFixedFrequency)
                ? BuildFixedFrequency(draft, frequencyMode)
                : null,
        };
        inventoryExtensions[ImpinjInventoryControlOptions.ExtensionKey] = existingControl;

        var configurationExtensions = new Dictionary<string, object?>(settings.Configuration.Extensions, StringComparer.Ordinal);
        ImpinjReaderConfiguration existingConfiguration = configurationExtensions.TryGetValue(
            ImpinjReaderConfiguration.ExtensionKey, out object? configurationValue) &&
            configurationValue is ImpinjReaderConfiguration configuration
                ? configuration
                : new ImpinjReaderConfiguration();
        int gpiPortCount = ResolveGpiPortCount(reader);
        var debounce = existingConfiguration.GpiDebounce
            .Where(item => item.GpiPortNumber > 0 && item.GpiPortNumber <= gpiPortCount)
            .ToDictionary(x => x.GpiPortNumber);
        for (int port = 1; port <= gpiPortCount; port++)
        {
            ushort portNumber = checked((ushort)port);
            if (draft.Values.TryGetValue(GpiDebounce(portNumber), out object? value) && value is not null)
            {
                debounce[portNumber] = new ImpinjGpiDebounceSetting(portNumber, checked((uint)GetIntValue(value)));
            }
        }

        configurationExtensions[ImpinjReaderConfiguration.ExtensionKey] = existingConfiguration with
        {
            GpiDebounce = debounce.Values.OrderBy(x => x.GpiPortNumber).ToArray(),
        };

        InventorySettings inventory = settings.Inventory ?? new InventorySettings();
        inventory = inventory with
        {
            Extensions = new ReadOnlyDictionary<string, object?>(inventoryExtensions),
        };
        return settings with
        {
            Inventory = inventory,
            Configuration = settings.Configuration with { Extensions = new ReadOnlyDictionary<string, object?>(configurationExtensions) },
        };
    }

    private static ImpinjFixedFrequencySettings? BuildFixedFrequency(SettingsDraft draft, int mode)
    {
        if (mode < 0)
        {
            return null;
        }

        if ((ImpinjFixedFrequencyMode)mode == ImpinjFixedFrequencyMode.Auto_Select)
        {
            return new ImpinjFixedFrequencySettings(ImpinjFixedFrequencyMode.Auto_Select, []);
        }

        ushort[] channels = GetString(draft, FixedFrequencyChannels)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(static value => ushort.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        if (channels.Length == 0)
        {
            throw new InvalidOperationException("Impinj Channel List 模式至少需要一个频道。");
        }

        return new ImpinjFixedFrequencySettings(ImpinjFixedFrequencyMode.Channel_List, channels);
    }

    private static IReadOnlyList<SettingsOption> SearchModeOptions() =>
        new SettingsOption[] { new(-1, "Reader selected") }
            .Concat(Enum.GetValues<ImpinjInventorySearchType>()
                .Select(value => new SettingsOption((int)value, value.ToString())))
            .ToArray();

    private static int ResolveGpiPortCount(ReaderRuntimeSnapshot reader) =>
        reader.GpiCount is { } count ? count : 4;

    private static bool Supports(ReaderRuntimeSnapshot reader, Feature feature) =>
        reader.FeatureCatalog.Supports(feature);

    private static IReadOnlyList<SettingsOption> BuildFrequencyOptions(ReaderSettingsRuntimeSnapshot runtime)
    {
        IReadOnlyList<uint> frequencies = runtime.Capabilities?.HopTables.FirstOrDefault()?.Frequencies
            ?? runtime.Capabilities?.TxFrequencies
            ?? [];
        return frequencies
            .Select((frequency, index) => new SettingsOption(
                index + 1,
                $"{index + 1} ({(frequency / 1000.0).ToString("0.###", CultureInfo.InvariantCulture)} MHz)"))
            .ToArray();
    }

    private static int GetInt(SettingsDraft draft, string key, int fallback) =>
        draft.Values.TryGetValue(key, out object? value) && value is not null
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : fallback;

    private static int GetIntValue(object value) => Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private static bool GetBool(SettingsDraft draft, string key, bool fallback = false) =>
        draft.Values.TryGetValue(key, out object? value) && value is not null
            ? Convert.ToBoolean(value, CultureInfo.InvariantCulture)
            : fallback;

    private static string GetString(SettingsDraft draft, string key) =>
        draft.Values.TryGetValue(key, out object? value) && value is not null
            ? value is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty
                : value.ToString() ?? string.Empty
            : string.Empty;
}
