namespace LlrpReaderPlatform.Contracts.Settings;

/// <summary>
/// 用户编辑的设置草稿。Values 可变以支持 UI 双向编辑；
/// 保存前必须携带 <see cref="CapabilityRevision"/> 供服务层复核能力是否过期。
/// </summary>
public sealed class SettingsDraft
{
    public required Guid ReaderId { get; init; }

    /// <summary>生成时的能力版本；保存前由服务层复核，过期则拒绝。</summary>
    public required long CapabilityRevision { get; init; }

    public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);
}
