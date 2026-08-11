namespace LlrpReaderPlatform.Services.Settings;

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
