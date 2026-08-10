namespace LlrpReaderPlatform.Contracts.Settings;

/// <summary>
/// 能力驱动的设置服务。UI 只调用此接口：Query 生成编辑器数据，Validate 同步校验，
/// Apply 负责连接租约、能力复核、编译与设备 I/O。SDK 设置类型始终留在 Services 内部。
/// </summary>
public interface IReaderSettingsService
{
    Task<SettingsEditorModel> QueryAsync(Guid readerId, CancellationToken ct = default);

    /// <summary>从设备读取 SDK 根据能力计算的默认设置，并投影为同一套编辑模型。</summary>
    Task<SettingsEditorModel> GetDefaultsAsync(Guid readerId, CancellationToken ct = default);

    SettingsValidationResult Validate(SettingsDraft draft);

    Task<SettingsApplyResult> ApplyAsync(Guid readerId, SettingsDraft draft, CancellationToken ct = default);
}
