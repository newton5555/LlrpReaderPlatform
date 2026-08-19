using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Services.Settings;
using LlrpSdk;
using LlrpSdk.Extensions.Zebra;

namespace LlrpReaderPlatform.Extensions.Zebra;

/// <summary>
/// Zebra 设置贡献者（实验性）。只在标准 Probe 已识别为 Zebra（FX9600 画像）时，
/// 向能力驱动布局增加配置/报告开关字段，并在同一 Apply 租约内把平台值编译回
/// ZebraReaderConfiguration / ZebraInventoryReportOptions 扩展字典。
/// </summary>
public sealed class ZebraSettingsContributor : ISettingsExtensionContributor
{
    public const string RadioPowerState = "zebra.radio-power-state";
    public const string RadioTransmitDelay = "zebra.radio-transmit-delay";
    public const string AutonomousModeState = "zebra.autonomous-mode-state";
    public const string SaveConfiguration = "zebra.save-configuration";
    public const string SaveTagData = "zebra.save-tag-data";
    public const string SaveTagEventData = "zebra.save-tag-event-data";
    public const string EnableNxpQuietCommands = "zebra.enable-nxp-set-reset-quiet";
    public const string IncludePhase = "zebra.report-phase";
    public const string IncludeGps = "zebra.report-gps";
    public const string IncludeZoneId = "zebra.report-zone-id";
    public const string IncludeZoneName = "zebra.report-zone-name";
    public const string IncludeMltReport = "zebra.report-mlt";

    public string Id => "zebra-settings";

    public bool IsApplicable(ReaderRuntimeSnapshot reader) =>
        reader.FeatureCatalog.SupportedFeatures.Any(static feature =>
            feature.IsVendor && string.Equals(feature.Vendor, "zebra", StringComparison.Ordinal));

    public void ContributeLayout(
        IList<SettingsEntry> entries,
        ReaderRuntimeSnapshot reader,
        ReaderSettingsRuntimeSnapshot runtime)
    {
        bool supportsConfiguration = reader.FeatureCatalog.SupportsOrUnknown(ZebraFeatures.Configuration);
        bool supportsInventory = reader.FeatureCatalog.SupportsOrUnknown(ZebraFeatures.InventoryOptions);
        if (!supportsConfiguration && !supportsInventory)
        {
            return;
        }

        ZebraReaderConfiguration? configuration = runtime.Settings.Settings.Configuration.Extensions
            .TryGetValue(ZebraReaderConfiguration.ExtensionKey, out object? configurationValue)
            ? configurationValue as ZebraReaderConfiguration
            : null;
        ZebraInventoryReportOptions? report = null;
        if (runtime.Settings.Settings.Inventory?.Extensions?.TryGetValue(
                ZebraInventoryReportOptions.ExtensionKey, out object? reportValue) == true)
        {
            report = reportValue as ZebraInventoryReportOptions;
        }

        if (supportsConfiguration)
        {
            AddBool(entries, RadioPowerState, "Zebra radio power state", configuration?.RadioPowerState, groupKey: SettingsGroups.Other);
            AddByte(entries, RadioTransmitDelay, "Zebra radio transmit delay", configuration?.RadioTransmitDelay, groupKey: SettingsGroups.Other);
            AddBool(entries, AutonomousModeState, "Zebra autonomous mode state", configuration?.AutonomousModeState, groupKey: SettingsGroups.Other);
            AddBool(entries, SaveConfiguration, "Zebra save configuration", configuration?.SaveConfiguration, groupKey: SettingsGroups.Other, isOptional: true);
            AddBool(entries, SaveTagData, "Zebra save tag data", configuration?.SaveTagData, groupKey: SettingsGroups.Other, isOptional: true);
            AddBool(entries, SaveTagEventData, "Zebra save tag event data", configuration?.SaveTagEventData, groupKey: SettingsGroups.Other, isOptional: true);
            AddBool(entries, EnableNxpQuietCommands, "Zebra NXP set/reset-quiet (experimental)", configuration?.EnableNxpSetAndResetQuietCommands, groupKey: SettingsGroups.Other, isOptional: true);
        }

        if (supportsInventory)
        {
            // ADR-0013：报告类字段由寻卡页列开关联动控制，设置页只读并提示。
            AddBool(entries, IncludePhase, "Zebra report phase", report?.IncludePhase, "由寻卡页联动控制", SettingsSemantics.PhaseReport, SettingsGroups.Report);
            AddBool(entries, IncludeGps, "Zebra report GPS", report?.IncludeGps, "由寻卡页联动控制", SettingsSemantics.GpsReport, SettingsGroups.Report);
            AddBool(entries, IncludeZoneId, "Zebra report zone id", report?.IncludeZoneId, groupKey: SettingsGroups.Report);
            AddBool(entries, IncludeZoneName, "Zebra report zone name", report?.IncludeZoneName, groupKey: SettingsGroups.Report);
            AddBool(entries, IncludeMltReport, "Zebra report MLT (experimental)", report?.IncludeMltReport, groupKey: SettingsGroups.Report);
        }
    }

    public ReaderSettings Apply(
        SettingsDraft draft,
        EffectiveSettingsLayout layout,
        ReaderRuntimeSnapshot reader,
        ReaderSettingsRuntimeSnapshot runtime,
        ReaderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (reader.FeatureCatalog.SupportsOrUnknown(ZebraFeatures.Configuration))
        {
            var configurationExtensions = new Dictionary<string, object?>(settings.Configuration.Extensions, StringComparer.Ordinal);
            ZebraReaderConfiguration existingConfiguration = configurationExtensions.TryGetValue(
                ZebraReaderConfiguration.ExtensionKey, out object? configurationValue)
                && configurationValue is ZebraReaderConfiguration c ? c : new ZebraReaderConfiguration();
            configurationExtensions[ZebraReaderConfiguration.ExtensionKey] = existingConfiguration with
            {
                RadioPowerState = GetBool(draft, RadioPowerState) ?? existingConfiguration.RadioPowerState,
                RadioTransmitDelay = GetByte(draft, RadioTransmitDelay) ?? existingConfiguration.RadioTransmitDelay,
                AutonomousModeState = GetBool(draft, AutonomousModeState) ?? existingConfiguration.AutonomousModeState,
                SaveConfiguration = GetBool(draft, SaveConfiguration) ?? existingConfiguration.SaveConfiguration,
                SaveTagData = GetBool(draft, SaveTagData) ?? existingConfiguration.SaveTagData,
                SaveTagEventData = GetBool(draft, SaveTagEventData) ?? existingConfiguration.SaveTagEventData,
                EnableNxpSetAndResetQuietCommands = GetBool(draft, EnableNxpQuietCommands) ?? existingConfiguration.EnableNxpSetAndResetQuietCommands,
            };
            settings = settings with { Configuration = settings.Configuration with { Extensions = configurationExtensions } };
        }

        if (settings.Inventory is not null && reader.FeatureCatalog.SupportsOrUnknown(ZebraFeatures.InventoryOptions))
        {
            var inventoryExtensions = new Dictionary<string, object?>(settings.Inventory.Extensions, StringComparer.Ordinal);
            ZebraInventoryReportOptions existingReport = inventoryExtensions.TryGetValue(
                ZebraInventoryReportOptions.ExtensionKey, out object? reportValue)
                && reportValue is ZebraInventoryReportOptions r ? r : new ZebraInventoryReportOptions();
            inventoryExtensions[ZebraInventoryReportOptions.ExtensionKey] = existingReport with
            {
                IncludePhase = GetBool(draft, IncludePhase) ?? existingReport.IncludePhase,
                IncludeGps = GetBool(draft, IncludeGps) ?? existingReport.IncludeGps,
                IncludeZoneId = GetBool(draft, IncludeZoneId) ?? existingReport.IncludeZoneId,
                IncludeZoneName = GetBool(draft, IncludeZoneName) ?? existingReport.IncludeZoneName,
                IncludeMltReport = GetBool(draft, IncludeMltReport) ?? existingReport.IncludeMltReport,
            };
            settings = settings with { Inventory = settings.Inventory with { Extensions = inventoryExtensions } };
        }

        return settings;
    }

    private static void AddBool(
        IList<SettingsEntry> entries,
        string key,
        string title,
        bool? currentValue,
        string? readOnlyReason = null,
        string? semanticId = null,
        string? groupKey = null,
        bool isOptional = false)
    {
        if (currentValue is null)
        {
            return;
        }

        entries.Add(new SettingsEntry
        {
            Key = key,
            Title = title,
            EditorKind = EditorKind.Boolean,
            ValueType = typeof(bool),
            CurrentValue = currentValue,
            Source = SettingsSource.VendorExtension,
            ReadOnlyReason = readOnlyReason,
            SemanticId = semanticId,
            GroupKey = groupKey,
            IsOptional = isOptional,
        });
    }

    private static void AddByte(IList<SettingsEntry> entries, string key, string title, byte? currentValue, string? groupKey = null)
    {
        if (currentValue is null)
        {
            return;
        }

        entries.Add(new SettingsEntry
        {
            Key = key,
            Title = title,
            EditorKind = EditorKind.Integer,
            ValueType = typeof(byte),
            Range = new SettingsRange(0, byte.MaxValue),
            CurrentValue = currentValue,
            Source = SettingsSource.VendorExtension,
            GroupKey = groupKey,
        });
    }

    private static bool? GetBool(SettingsDraft draft, string key) =>
        draft.Values.TryGetValue(key, out object? value) && value is bool b ? b : null;

    private static byte? GetByte(SettingsDraft draft, string key)
    {
        if (!draft.Values.TryGetValue(key, out object? value))
        {
            return null;
        }

        return value switch
        {
            byte byteValue => byteValue,
            int intValue when intValue is >= byte.MinValue and <= byte.MaxValue => (byte)intValue,
            _ => null,
        };
    }
}
