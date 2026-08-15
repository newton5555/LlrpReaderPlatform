using LlrpReaderPlatform.Contracts.Settings;

namespace LlrpReaderPlatform.Extensions.Impinj;

/// <summary>
/// Impinj 能力标识由 Impinj 扩展拥有，避免通用 Contracts 为每个厂商持续增长。
/// </summary>
public static class ImpinjFeatures
{
    public static readonly Feature FastId = new("fast-id", "impinj", semanticId: SettingsSemantics.FastId);
    public static readonly Feature RfPhase = new("rf-phase", "impinj", semanticId: SettingsSemantics.PhaseReport);
    public static readonly Feature Doppler = new("doppler", "impinj", semanticId: SettingsSemantics.Doppler);
    public static readonly Feature SearchMode = new("search-mode", "impinj", semanticId: SettingsSemantics.SearchMode);
    public static readonly Feature LowDutyCycle = new("low-duty-cycle", "impinj", semanticId: SettingsSemantics.LowDutyCycle);
    public static readonly Feature FixedFrequency = new("fixed-frequency", "impinj", semanticId: SettingsSemantics.FixedFrequency);
    public static readonly Feature GpiDebounce = new("gpi-debounce", "impinj", semanticId: SettingsSemantics.GpiDebounce);
}
