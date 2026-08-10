using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderPlatform.Contracts.Persistence;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>
/// Tag List 管理页。列表、条目和保存全部通过 Contracts store 完成，WPF 不接触 EF/SQLite。
/// </summary>
public partial class TagListsViewModel : ObservableObject
{
    private readonly ITagListStore store;
    private bool loading;

    public TagListsViewModel(ITagListStore store)
    {
        this.store = store;
    }

    public ObservableCollection<TagListEditorItem> Lists { get; } = [];
    public ObservableCollection<TagListEntryEditorItem> Entries { get; } = [];

    [ObservableProperty]
    private TagListEditorItem? selectedList;

    [ObservableProperty]
    private TagListEntryEditorItem? selectedEntry;

    [ObservableProperty]
    private string listName = string.Empty;

    [ObservableProperty]
    private string listColor = "#5EEAD4";

    [ObservableProperty]
    private bool listEnabled = true;

    [ObservableProperty]
    private string entryEpc = string.Empty;

    [ObservableProperty]
    private string entryDisplayName = string.Empty;

    [ObservableProperty]
    private string? status;

    [ObservableProperty]
    private bool isBusy;

    private int operationInFlight;

    partial void OnSelectedListChanged(TagListEditorItem? value)
    {
        if (value is null || loading)
        {
            return;
        }

        ListName = value.Name;
        ListColor = value.ColorHex;
        ListEnabled = value.IsEnabled;
        Entries.Clear();
        foreach (TagListEntry entry in value.Entries)
        {
            Entries.Add(new TagListEntryEditorItem(entry));
        }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (!TryBeginOperation())
        {
            Status = "Tag List 操作进行中，请稍候。";
            return;
        }

        try
        {
            await LoadCoreAsync();
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task LoadCoreAsync()
    {
        try
        {
            Guid? selectedId = SelectedList?.Id;
            loading = true;
            IReadOnlyList<TagListDefinition> definitions = await store.GetAllAsync(CancellationToken.None);
            Lists.Clear();
            foreach (TagListDefinition definition in definitions)
            {
                Lists.Add(new TagListEditorItem(definition));
            }

            Status = $"已加载 {Lists.Count} 个 Tag List。";

            // 选择变更回调在 loading 期间被抑制；在列表完成重建后重新选择，
            // 确保 Entries 不会继续显示上一个 Tag List 的条目。
            loading = false;
            if (selectedId is { } id)
            {
                SelectedList = Lists.FirstOrDefault(x => x.Id == id);
            }
        }
        catch (Exception ex)
        {
            Status = $"读取 Tag List 失败：{ex.Message}";
        }
        finally
        {
            loading = false;
        }
    }

    [RelayCommand]
    private void New()
    {
        SelectedList = null;
        ListName = "New Tag List";
        ListColor = "#5EEAD4";
        ListEnabled = true;
        Entries.Clear();
        EntryEpc = string.Empty;
        EntryDisplayName = string.Empty;
        Status = "已创建未保存的 Tag List。";
    }

    [RelayCommand]
    private void AddEntry()
    {
        string epc = NormalizeHex(EntryEpc);
        if (epc.Length == 0 || epc.Length % 4 != 0 || !IsHex(epc))
        {
            Status = "EPC 必须是非空、偶数个 16-bit word 的十六进制字符串。";
            return;
        }

        if (Entries.Any(x => string.Equals(x.EpcHex, epc, StringComparison.OrdinalIgnoreCase)))
        {
            Status = "该 EPC 已在当前 Tag List 中。";
            return;
        }

        Entries.Add(new TagListEntryEditorItem(new TagListEntry
        {
            Id = Guid.NewGuid(),
            TagListId = SelectedList?.Id ?? Guid.Empty,
            EpcHex = epc,
            DisplayName = EntryDisplayName.Trim(),
        }));
        EntryEpc = string.Empty;
        EntryDisplayName = string.Empty;
    }

    [RelayCommand]
    private void RemoveEntry(TagListEntryEditorItem? item)
    {
        if (item is not null)
        {
            Entries.Remove(item);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(ListName))
        {
            Status = "Tag List 名称不能为空。";
            return;
        }

        if (!TryBeginOperation())
        {
            Status = "Tag List 操作进行中，请稍候。";
            return;
        }

        Guid listId = SelectedList?.Id ?? Guid.NewGuid();
        try
        {
            var normalizedEntries = new List<TagListEntry>(Entries.Count);
            var seenEpcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TagListEntryEditorItem item in Entries)
            {
                string normalizedEpc = NormalizeHex(item.EpcHex);
                if (normalizedEpc.Length == 0 || normalizedEpc.Length % 4 != 0 || !IsHex(normalizedEpc))
                {
                    Status = $"EPC '{item.EpcHex}' 必须是非空、完整 16-bit word 的十六进制字符串。";
                    return;
                }

                if (!seenEpcs.Add(normalizedEpc))
                {
                    Status = $"EPC '{normalizedEpc}' 在当前 Tag List 中重复。";
                    return;
                }

                normalizedEntries.Add(item.ToRecord(listId) with { EpcHex = normalizedEpc });
            }

            var definition = new TagListDefinition
            {
                Id = listId,
                Name = ListName.Trim(),
                ColorHex = string.IsNullOrWhiteSpace(ListColor) ? "#5EEAD4" : ListColor.Trim(),
                IsEnabled = ListEnabled,
                Entries = normalizedEntries,
            };
            await store.SaveAsync(definition, CancellationToken.None);
            await LoadCoreAsync();
            SelectedList = Lists.FirstOrDefault(x => x.Id == listId);
            Status = $"Tag List “{definition.Name}” 已保存。";
        }
        catch (Exception ex)
        {
            Status = $"保存 Tag List 失败：{ex.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedList is null)
        {
            Status = "请先选择 Tag List。";
            return;
        }

        if (!TryBeginOperation())
        {
            Status = "Tag List 操作进行中，请稍候。";
            return;
        }

        Guid id = SelectedList.Id;
        try
        {
            await store.DeleteAsync(id, CancellationToken.None);
            SelectedList = null;
            Entries.Clear();
            await LoadCoreAsync();
            Status = "Tag List 已删除。";
        }
        catch (Exception ex)
        {
            Status = $"删除 Tag List 失败：{ex.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private bool TryBeginOperation() =>
        Interlocked.CompareExchange(ref operationInFlight, 1, 0) == 0
        && SetBusyAndReturnTrue();

    private bool SetBusyAndReturnTrue()
    {
        IsBusy = true;
        return true;
    }

    private void EndOperation()
    {
        IsBusy = false;
        Volatile.Write(ref operationInFlight, 0);
    }

    private static string NormalizeHex(string value) => value.Trim()
        .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace(" ", string.Empty, StringComparison.Ordinal)
        .Replace("-", string.Empty, StringComparison.Ordinal)
        .Replace(":", string.Empty, StringComparison.Ordinal)
        .ToUpperInvariant();

    private static bool IsHex(string value) => value.All(static character =>
        character is >= '0' and <= '9'
            or >= 'A' and <= 'F');
}

public sealed partial class TagListEditorItem : ObservableObject
{
    public TagListEditorItem(TagListDefinition definition)
    {
        Id = definition.Id;
        Name = definition.Name;
        IsEnabled = definition.IsEnabled;
        ColorHex = definition.ColorHex;
        Entries = definition.Entries;
    }

    public Guid Id { get; }
    public string Name { get; }
    public bool IsEnabled { get; }
    public string ColorHex { get; }
    public IReadOnlyList<TagListEntry> Entries { get; }
}

public sealed partial class TagListEntryEditorItem : ObservableObject
{
    public TagListEntryEditorItem(TagListEntry entry)
    {
        Id = entry.Id;
        EpcHex = entry.EpcHex;
        DisplayName = entry.DisplayName;
        ColorHex = entry.ColorHex ?? string.Empty;
    }

    public Guid Id { get; }

    [ObservableProperty]
    private string epcHex;

    [ObservableProperty]
    private string displayName;

    [ObservableProperty]
    private string colorHex;

    public TagListEntry ToRecord(Guid listId) => new()
    {
        Id = Id,
        TagListId = listId,
        EpcHex = EpcHex,
        DisplayName = DisplayName,
        ColorHex = string.IsNullOrWhiteSpace(ColorHex) ? null : ColorHex,
    };
}
