namespace LlrpReaderPlatform.Contracts.Tagging;

/// <summary>
/// 寻卡上报扩展字段的稳定语义键（UI 无关；ADR-0013）。
/// 语义键对应"报告里多不多一个字段"，由寻卡页列开关通过
/// <see cref="InventoryReportSpec.ExtensionReportFields"/> 请求，服务层按当前激活扩展编译到厂商参数。
/// 同一语义键可能由多个厂商/标准路径贡献，UI 只认键不认厂商 Key。
/// </summary>
public static class ReportFieldSemantics
{
    /// <summary>RF 相位。Impinj（RF phase angle）与 Zebra（report phase）共用。</summary>
    public const string Phase = "phase-report";

    /// <summary>GPS 坐标。当前由 Zebra report-gps 贡献。</summary>
    public const string Gps = "gps-report";

    /// <summary>XPC/XPCW1/W2。当前 Zebra report-xpc 贡献（LLRP 2.0 标准吸收为 C1G2_XPC）。</summary>
    public const string Xpc = "xpc-report";
}
