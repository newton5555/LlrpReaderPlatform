namespace LlrpReaderPlatform.Contracts.Settings;

/// <summary>
/// 根据 ReaderFeatureCatalog 决定设置布局：哪些项显示/隐藏/只读/可选值/校验规则。
/// 语义化，UI 无关；由服务层生成，UI 只负责投影渲染与提交 Draft。
/// </summary>
public sealed class EffectiveSettingsLayout
{
    public required Guid ReaderId { get; init; }

    /// <summary>生成该布局对应的能力版本；UI 提交 Draft 时须携带此值供复核。</summary>
    public required long CapabilityRevision { get; init; }

    public required IReadOnlyList<SettingsEntry> Entries { get; init; }

    /// <summary>生成此布局时生效的能力目录，供其他 UI 消费者解释显示/只读原因。</summary>
    public ReaderFeatureCatalog FeatureCatalog { get; init; } = ReaderFeatureCatalog.Empty;

    /// <summary>此 Reader 是否有可编辑设置（区别于纯只读占位）。</summary>
    public bool HasEditableSettings => Entries.Any(static e => !e.IsReadOnly);
}
