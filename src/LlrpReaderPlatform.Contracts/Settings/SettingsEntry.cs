namespace LlrpReaderPlatform.Contracts.Settings;

/// <summary>设置项来源：标准 LLRP 或厂商扩展模块。</summary>
public enum SettingsSource
{
    Standard = 0,
    VendorExtension = 1,
}

/// <summary>一个可选值项（下拉选项等）。</summary>
public sealed record SettingsOption(object? Value, string? Display = null);

/// <summary>数值/可比较范围的限定。</summary>
public sealed record SettingsRange(decimal Min, decimal Max);

/// <summary>
/// 能力驱动的单个设置项描述。携带稳定 Key、标题、编辑类型、当前/默认值、
/// 可选值/范围、只读原因、显示条件与来源。UI 无关。
/// </summary>
public sealed record SettingsEntry
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required EditorKind EditorKind { get; init; }

    /// <summary>值的 CLR 类型（如 typeof(bool)、typeof(string)、typeof(int)）。</summary>
    public required Type ValueType { get; init; }

    public object? CurrentValue { get; init; }
    public object? DefaultValue { get; init; }

    /// <summary>Choice 编辑器的可选值。</summary>
    public IReadOnlyList<SettingsOption> Options { get; init; } = [];

    /// <summary>Integer/Decimal 编辑器的取值范围。</summary>
    public SettingsRange? Range { get; init; }

    /// <summary>此项被 UI 隐藏的条件；null 表示始终可见。</summary>
    public string? VisibleWhen { get; init; }

    /// <summary>只读原因（能力不支持、需要连接等）；null 表示可编辑。</summary>
    public string? ReadOnlyReason { get; init; }

    public SettingsSource Source { get; init; } = SettingsSource.Standard;

    public bool IsReadOnly => ReadOnlyReason is not null;
}
