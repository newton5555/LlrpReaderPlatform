namespace LlrpReaderPlatform.Services.Settings;

/// <summary>
/// 标准设置项稳定 Key。UI 与 Services 之间通过这些 Key 识别设置项。
/// 厂商扩展项由各自扩展模块定义（带前缀），不在此枚举。
/// </summary>
public static class SettingsKeys
{
    public const string Antenna = "antenna";
    public const string AntennaIds = "antenna-ids";
    public const string IndividualAntennaSettings = "individual-antenna-settings";
    public const string Session = "session";
    public const string TxPowerDbm = "tx-power-dbm";
    public const string RxSensitivityDb = "rx-sensitivity-db";
    public const string TagPopulation = "tag-population";
    public const string ReportEvery = "report-every";
    public const string RfMode = "rf-mode";
    public const string Tari = "tari";
    public const string StateAwareTarget = "state-aware-target";
    public const string StateAwareSelectedFlag = "state-aware-selected-flag";
    public const string StateAwareFiltersEnabled = "state-aware-filters-enabled";
    public const string StartGpiEnabled = "start-gpi-enabled";
    public const string StartGpiPort = "start-gpi-port";
    public const string StartGpiLevel = "start-gpi-level";
    public const string StopGpiEnabled = "stop-gpi-enabled";
    public const string StopGpiPort = "stop-gpi-port";
    public const string StopGpiLevel = "stop-gpi-level";
    public const string StopGpiTimeoutMs = "stop-gpi-timeout-ms";
    public const string ReportAntenna = "report-antenna";
    public const string ReportChannel = "report-channel";
    public const string ReportRssi = "report-rssi";
    public const string ReportFirstSeen = "report-first-seen";
    public const string ReportLastSeen = "report-last-seen";
    public const string ReportTagCount = "report-tag-count";
    public const string ReportPcBits = "report-pc-bits";

    public static string FilterEnabled(int index) => $"filter-{index}-enabled";
    public static string FilterMemoryBank(int index) => $"filter-{index}-memory-bank";
    public static string FilterOffset(int index) => $"filter-{index}-offset";
    public static string FilterBitLength(int index) => $"filter-{index}-bit-length";
    public static string FilterMask(int index) => $"filter-{index}-mask";
    public static string FilterMatchAction(int index) => $"filter-{index}-match-action";
    public static string FilterNonMatchAction(int index) => $"filter-{index}-non-match-action";
    public static string FilterStateTarget(int index) => $"filter-{index}-state-target";
    public static string FilterStateAction(int index) => $"filter-{index}-state-action";
    public static string AntennaTxPowerDbm(ushort antennaId) => $"antenna-{antennaId}-tx-power-dbm";
    public static string AntennaRxSensitivityDb(ushort antennaId) => $"antenna-{antennaId}-rx-sensitivity-db";
    public static string AntennaChannelIndex(ushort antennaId) => $"antenna-{antennaId}-channel-index";
}

/// <summary>
/// 编译后的设置 DTO（Services 内部，不向 UI 暴露）。由 ISettingsCompiler 从已校验的
/// SettingsDraft 组装；后续 Extension/基础设施将其映射为 SDK ReaderSettings 下发设备。
/// </summary>
public sealed class CompiledSettings
{
    public ushort? AntennaId { get; set; }
    public IReadOnlyList<ushort>? AntennaIds { get; set; }
    public bool? IndividualAntennaSettings { get; set; }
    public int? Session { get; set; }
    public decimal? TxPowerDbm { get; set; }
    public int? RxSensitivityDb { get; set; }
    public int? TagPopulation { get; set; }
    public int? ReportEvery { get; set; }
    public int? RfMode { get; set; }
    public int? Tari { get; set; }
    public IReadOnlyList<LlrpSdk.InventorySelectFilter>? Filters { get; set; }

    /// <summary>扩展模块贡献的额外值（Key→平台无关值），映射为厂商设置。</summary>
    public Dictionary<string, object?> VendorValues { get; } = new(StringComparer.Ordinal);
}
