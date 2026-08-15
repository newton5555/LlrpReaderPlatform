using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using LlrpReaderPlatform.Contracts.Settings;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>
/// 设置页的一行（UI 无关语义）。按 <see cref="EditorKind"/> 提供对应编辑器：
/// Choice → 下拉，Boolean → 开关，Text/数值 → 文本框。三者统一回写 <see cref="ValueText"/>，
/// 使 ReaderSettingsViewModel.SaveAsync 无需区分编辑器类型。
/// </summary>
public partial class SettingsEntryRowViewModel : ObservableObject
{
    private readonly IReadOnlyList<object?> choiceValues;

    public SettingsEntryRowViewModel(SettingsEntry entry)
    {
        Entry = entry;
        ValueText = FormatValue(entry.CurrentValue);
        choiceValues = entry.Options.Select(static o => o.Value).ToArray();
        ChoiceDisplays = entry.Options
            .Select(o => o.Display ?? FormatValue(o.Value))
            .ToArray();

        if (entry.Options.Count > 0 && entry.CurrentValue is not null)
        {
            SelectedChoiceIndex = IndexOfCurrent();
        }

        if (entry.EditorKind == EditorKind.Boolean)
        {
            BooleanValue = entry.CurrentValue is bool b && b;
        }

        if (entry.EditorKind == EditorKind.Collection)
        {
            HashSet<string> selected = (entry.CurrentValue?.ToString() ?? string.Empty)
                .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (SettingsOption option in entry.Options)
            {
                string value = FormatValue(option.Value);
                var item = new SettingsCollectionItemViewModel(
                    value,
                    option.Display ?? value,
                    selected.Contains(value));
                item.PropertyChanged += OnCollectionItemPropertyChanged;
                CollectionItems.Add(item);
            }

            SyncCollection();
        }
    }

    public SettingsEntry Entry { get; }

    public string Key => Entry.Key;
    public string Title => Entry.Title;
    public EditorKind EditorKind => Entry.EditorKind;
    public string ValueTypeName => Entry.ValueType.Name;
    public bool IsReadOnly => Entry.IsReadOnly;
    public bool IsEditable => !IsReadOnly;
    public string? ReadOnlyReason => Entry.ReadOnlyReason;
    public bool IsChoice => Entry.EditorKind == EditorKind.Choice;
    public bool IsBoolean => Entry.EditorKind == EditorKind.Boolean;
    public bool IsCollection => Entry.EditorKind == EditorKind.Collection;
    public bool IsText => !IsChoice && !IsBoolean && !IsCollection;

    /// <summary>Choice 编辑器的展示文本列表（与可选值一一对应）。</summary>
    public IReadOnlyList<string> ChoiceDisplays { get; }

    public ObservableCollection<SettingsCollectionItemViewModel> CollectionItems { get; } = [];

    [ObservableProperty]
    private string valueText;

    [ObservableProperty]
    private int selectedChoiceIndex = -1;

    [ObservableProperty]
    private bool booleanValue;

    partial void OnSelectedChoiceIndexChanged(int value) => SyncChoice();

    partial void OnBooleanValueChanged(bool value) => ValueText = FormatValue(value);

    /// <summary>Choice 选中项对应的原始值。</summary>
    public object? SelectedChoiceValue => SelectedChoiceIndex >= 0 && SelectedChoiceIndex < choiceValues.Count
        ? choiceValues[SelectedChoiceIndex]
        : null;

    private int IndexOfCurrent()
    {
        for (int i = 0; i < choiceValues.Count; i++)
        {
            if (Equals(choiceValues[i], Entry.CurrentValue))
            {
                return i;
            }
        }

        return -1;
    }

    private void SyncChoice()
    {
        if (SelectedChoiceIndex >= 0 && SelectedChoiceIndex < choiceValues.Count)
        {
            // The editor displays the capability-table label, while
            // SelectedChoiceValue remains the original value for saving.
            ValueText = ChoiceDisplays[SelectedChoiceIndex];
        }
    }

    private static string FormatValue(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty,
    };

    private void OnCollectionItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SettingsCollectionItemViewModel.IsSelected))
        {
            SyncCollection();
        }
    }

    private void SyncCollection() => ValueText = string.Join(",", CollectionItems
        .Where(static item => item.IsSelected)
        .Select(static item => item.Value));
}

public sealed partial class SettingsCollectionItemViewModel : ObservableObject
{
    public SettingsCollectionItemViewModel(string value, string display, bool isSelected)
    {
        Value = value;
        Display = display;
        IsSelected = isSelected;
    }

    public string Value { get; }
    public string Display { get; }

    [ObservableProperty]
    private bool isSelected;
}
