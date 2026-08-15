using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;

namespace LlrpReaderPlatform.Extensions.Zebra;

/// <summary>
/// Zebra 能力标识由 Zebra 扩展拥有；Contracts 只保存厂商无关的 Feature 载体。
/// </summary>
public static class ZebraFeatures
{
    public static readonly Feature Configuration = new("configuration", "zebra", semanticId: "zebra-configuration");
    public static readonly Feature ReportPhase = new("report-phase", "zebra", semanticId: SettingsSemantics.PhaseReport);
    public static readonly Feature ReportGps = new("report-gps", "zebra", semanticId: SettingsSemantics.GpsReport);
    public static readonly Feature ReportXpc = new("report-xpc", "zebra", semanticId: SettingsSemantics.XpcReport, standardizedSince: LlrpProtocolVersion.Version20);
    public static readonly Feature InventoryOptions = new("inventory-options", "zebra", semanticId: "zebra-inventory-options");
}
