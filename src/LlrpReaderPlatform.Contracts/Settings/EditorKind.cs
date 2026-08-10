namespace LlrpReaderPlatform.Contracts.Settings;

/// <summary>
/// UI 无关的设置项编辑类型。由 <c>EffectiveSettingsLayout</c> 在服务层生成，
/// 各 UI 框架将其映射为自己的控件（WPF 的 DataTemplate、其他框架各自的编辑器）。
/// 禁止使用 TextBox/ComboBox 等 WPF 控件名。
/// </summary>
public enum EditorKind
{
    Boolean,
    Choice,
    Integer,
    Decimal,
    Text,
    Collection,
}
