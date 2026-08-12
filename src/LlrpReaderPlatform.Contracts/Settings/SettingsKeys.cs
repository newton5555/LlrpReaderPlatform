namespace LlrpReaderPlatform.Contracts.Settings;

/// <summary>
/// 标准设置项稳定 Key。UI 与平台服务之间通过这些 Key 识别设置项。
/// 厂商扩展项由各自扩展模块定义（带前缀），不在此枚举。
/// </summary>
public static class SettingsKeys
{
    public const string Antenna = "antenna";
    public const string AntennaIds = "antenna-ids";
    public const string IndividualAntennaSettings = "individual-antenna-settings";
    public const string Session = "session";
    public const string TxPowerIndex = "tx-power-index";
    public const string RxSensitivityIndex = "rx-sensitivity-index";
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
    public static string AntennaTxPowerIndex(ushort antennaId) => $"antenna-{antennaId}-tx-power-index";
    public static string AntennaRxSensitivityIndex(ushort antennaId) => $"antenna-{antennaId}-rx-sensitivity-index";
    public static string AntennaChannelIndex(ushort antennaId) => $"antenna-{antennaId}-channel-index";

    // Source-compatible aliases for consumers compiled against the earlier semantic names.
    // The values of these settings are now table indices; the old names must not be used for
    // new persisted data or UI labels.
    public const string TxPowerDbm = TxPowerIndex;
    public const string RxSensitivityDb = RxSensitivityIndex;
    public static string AntennaTxPowerDbm(ushort antennaId) => AntennaTxPowerIndex(antennaId);
    public static string AntennaRxSensitivityDb(ushort antennaId) => AntennaRxSensitivityIndex(antennaId);
}
