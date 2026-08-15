using LlrpReaderPlatform.Contracts.Tagging;

namespace LlrpReaderPlatform.Contracts.Settings;

/// <summary>
/// 跨扩展的设置语义键。设置的实际 <see cref="SettingsEntry.Key"/> 仍由扩展拥有，
/// UI 只使用这里的稳定语义和分组元数据定位公共布局。
/// </summary>
public static class SettingsSemantics
{
    public const string SearchMode = "inventory-search-mode";
    public const string FastId = "serialized-tid";
    public const string Doppler = "doppler-report";
    public const string FixedFrequency = "fixed-frequency";
    public const string FixedFrequencyChannels = "fixed-frequency-channels";
    public const string LowDutyCycle = "low-duty-cycle";
    public const string EmptyFieldTimeout = "empty-field-timeout";
    public const string FieldPingInterval = "field-ping-interval";
    public const string GpiDebounce = "gpi-debounce";

    // 报告类设置和寻卡报告列共享同一稳定语义键。
    public const string PhaseReport = ReportFieldSemantics.Phase;
    public const string GpsReport = ReportFieldSemantics.Gps;
    public const string XpcReport = ReportFieldSemantics.Xpc;
}

/// <summary>WPF 以外也可复用的设置布局分组语义。</summary>
public static class SettingsGroups
{
    public const string Manual = "manual";
    public const string Power = "power";
    public const string Gpi = "gpi";
    public const string Filter = "filter";
    public const string StateAware = "state-aware";
    public const string Frequency = "frequency";
    public const string LowDuty = "low-duty";
    public const string Report = "report";
    public const string Antenna = "antenna";
    public const string Other = "other";
}
