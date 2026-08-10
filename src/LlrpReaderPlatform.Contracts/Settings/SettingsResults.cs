using LlrpReaderPlatform.Contracts.Errors;

namespace LlrpReaderPlatform.Contracts.Settings;

/// <summary>单个设置项的校验问题。</summary>
public sealed record SettingsEntryIssue(string Key, string Message);

/// <summary>整份 Draft 的校验结果。</summary>
public sealed record SettingsValidationResult(
    bool IsValid,
    string? Message = null,
    IReadOnlyList<SettingsEntryIssue>? Issues = null)
{
    public bool IsValid { get; } = IsValid;
    public string? Message { get; } = Message ?? (IsValid ? null : "设置校验失败。");
    public IReadOnlyList<SettingsEntryIssue> Issues { get; } = Issues ?? [];
}

/// <summary>设置应用结果。</summary>
public sealed record SettingsApplyResult(bool Succeeded, string? Error = null)
{
    public bool Succeeded { get; } = Succeeded;
    public string? Error { get; } = Error;
    public PlatformErrorCode ErrorCode { get; init; } = Succeeded
        ? PlatformErrorCode.None
        : PlatformErrorCode.DeviceFailed;
}

/// <summary>设置编辑器的完整数据：能力驱动的布局 + 当前值快照。</summary>
public sealed record SettingsEditorModel(
    EffectiveSettingsLayout Layout,
    SettingsSnapshot Snapshot);
