using LlrpReaderPlatform.Contracts.Persistence;

namespace LlrpReaderPlatform.Infrastructure.Data;

/// <summary>
/// 从应用设置读取盘存数据策略。旧版本只保存布尔 Tag Logging 时，
/// True 兼容映射为 RawReports；没有旧设置时采用 FinalSnapshot。
/// </summary>
public sealed class AppSettingsInventoryLoggingPolicy(IAppSettingsStore appSettings)
    : IInventoryLoggingPolicy
{
    public async Task<InventoryLoggingMode> GetModeAsync(CancellationToken ct = default)
    {
        string? configured = await appSettings
            .GetAsync(InventoryLoggingSettings.ModeKey, ct)
            .ConfigureAwait(false);
        if (Enum.TryParse(configured, ignoreCase: true, out InventoryLoggingMode mode)
            && Enum.IsDefined(mode))
        {
            return mode;
        }

        string? legacy = await appSettings
            .GetAsync(InventoryLoggingSettings.LegacyEnabledKey, ct)
            .ConfigureAwait(false);
        return bool.TryParse(legacy, out bool enabled) && enabled
            ? InventoryLoggingMode.RawReports
            : InventoryLoggingMode.FinalSnapshot;
    }
}
