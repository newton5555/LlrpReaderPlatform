using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderPlatform.Contracts.Errors;
using LlrpReaderPlatform.Contracts.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>
/// 标签映射维护页。UI 只呈现 EPC、标签名称和颜色；底层 TagListDefinition
/// 仅作为现有 SQLite/Contracts 的兼容存储容器，不再暴露多个 List 的概念。
/// </summary>
public partial class TagListsViewModel : ObservableObject, IPageOperationOwner, IDisposable
{
    private static readonly Guid SystemTagListId = Guid.Parse("7DE7A792-6233-4DF7-A874-1E2213091868");
    private const string SystemTagListName = "Tags of Interest";
    private const string DefaultColor = "#5EEAD4";

    private readonly ITagListStore store;
    private readonly ILogger<TagListsViewModel> logger;
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly CancellationToken lifetimeToken;
    private CancellationTokenSource? activeOperationCts;
    private IReadOnlyList<Guid> loadedListIds = [];
    private bool disposed;
    private int operationInFlight;

    public TagListsViewModel(
        ITagListStore store,
        ILogger<TagListsViewModel>? logger = null)
    {
        this.store = store;
        this.logger = logger ?? NullLogger<TagListsViewModel>.Instance;
        lifetimeToken = lifetimeCts.Token;
    }

    public ObservableCollection<TagListEntryEditorItem> Entries { get; } = [];

    /// <summary>标签映射保存后通知寻卡页立即重新投影现有行。</summary>
    public event EventHandler? Changed;

    [ObservableProperty]
    private TagListEntryEditorItem? selectedEntry;

    [ObservableProperty]
    private string entryEpc = string.Empty;

    [ObservableProperty]
    private string entryDisplayName = string.Empty;

    [ObservableProperty]
    private string entryColor = DefaultColor;

    [ObservableProperty]
    private string? status;

    [ObservableProperty]
    private bool isBusy;

    public int EntryCount => Entries.Count;

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (disposed || !TryBeginOperation())
        {
            if (!disposed)
            {
                Status = "标签操作进行中，请稍候。";
            }

            return;
        }

        CancellationTokenSource operationCts = BeginOperation();
        Guid operationId = Guid.NewGuid();
        logger.LogInformation("WPF operation {Operation} started: {OperationId}.", "LoadTagLists", operationId);
        try
        {
            IReadOnlyList<TagListDefinition> definitions = await store.GetAllAsync(operationCts.Token);
            if (disposed || operationCts.IsCancellationRequested)
            {
                return;
            }

            loadedListIds = definitions.Select(static list => list.Id).Distinct().ToArray();
            Entries.Clear();

            // 兼容之前已经创建的多个列表：读取时展开成一张标签映射表。相同 EPC
            // 优先使用启用列表中的第一条；下次保存时收敛为唯一系统容器。
            IEnumerable<(TagListDefinition List, TagListEntry Entry)> flattened = definitions
                .OrderByDescending(static list => list.IsEnabled)
                .SelectMany(static list => list.Entries.Select(entry => (list, entry)));
            foreach ((TagListDefinition list, TagListEntry entry) in flattened
                         .GroupBy(static item => NormalizeHex(item.Entry.EpcHex), StringComparer.OrdinalIgnoreCase)
                         .Where(static group => !string.IsNullOrWhiteSpace(group.Key))
                         .Select(static group => group.First()))
            {
                Entries.Add(new TagListEntryEditorItem(new TagListEntry
                {
                    Id = entry.Id,
                    TagListId = SystemTagListId,
                    EpcHex = NormalizeHex(entry.EpcHex),
                    DisplayName = entry.DisplayName,
                    ColorHex = NormalizeColor(entry.ColorHex ?? list.ColorHex),
                }));
            }

            SelectedEntry = Entries.FirstOrDefault();
            NotifyEntryCountChanged();
            Status = $"已加载 {Entries.Count} 个标签。";
            logger.LogInformation(
                "WPF operation {Operation} completed: {OperationId}, entries {EntryCount}.",
                "LoadTagLists",
                operationId,
                Entries.Count);
        }
        catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
        {
            // 页面切换或应用退出。
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WPF operation {Operation} failed: {OperationId}.", "LoadTagLists", operationId);
            if (!disposed)
            {
                Status = PlatformErrorDisplay.Failure("读取标签", PlatformErrorCode.PersistenceFailed, ex.Message);
            }
        }
        finally
        {
            EndOperation(operationCts);
        }
    }

    [RelayCommand]
    private void New()
    {
        SelectedEntry = null;
        EntryEpc = string.Empty;
        EntryDisplayName = string.Empty;
        EntryColor = DefaultColor;
        Status = "请输入 EPC、标签名称和颜色。";
    }

    [RelayCommand]
    private void AddEntry()
    {
        string epc = NormalizeHex(EntryEpc);
        if (!TryValidateEntry(epc, EntryDisplayName, EntryColor, out string color, out string? error))
        {
            Status = error;
            return;
        }

        if (Entries.Any(item => string.Equals(item.EpcHex, epc, StringComparison.OrdinalIgnoreCase)))
        {
            Status = "该 EPC 已存在，可以直接在表格中修改名称或颜色。";
            return;
        }

        var item = new TagListEntryEditorItem(new TagListEntry
        {
            Id = Guid.NewGuid(),
            TagListId = SystemTagListId,
            EpcHex = epc,
            DisplayName = EntryDisplayName.Trim(),
            ColorHex = color,
        });
        Entries.Add(item);
        SelectedEntry = item;
        NotifyEntryCountChanged();
        New();
        SelectedEntry = item;
        Status = "标签已添加；点击 SAVE CHANGES 写入数据库。";
    }

    [RelayCommand]
    private void RemoveEntry(TagListEntryEditorItem? item)
    {
        if (item is null || !Entries.Remove(item))
        {
            return;
        }

        SelectedEntry = Entries.FirstOrDefault();
        NotifyEntryCountChanged();
        Status = "标签已移除；点击 SAVE CHANGES 写入数据库。";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (disposed || !TryBeginOperation())
        {
            if (!disposed)
            {
                Status = "标签操作进行中，请稍候。";
            }

            return;
        }

        CancellationTokenSource operationCts = BeginOperation();
        Guid operationId = Guid.NewGuid();
        logger.LogInformation("WPF operation {Operation} started: {OperationId}.", "SaveTagLists", operationId);
        try
        {
            var normalizedEntries = new List<TagListEntry>(Entries.Count);
            var seenEpcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (TagListEntryEditorItem item in Entries)
            {
                string epc = NormalizeHex(item.EpcHex);
                if (!TryValidateEntry(epc, item.DisplayName, item.ColorHex, out string color, out string? error))
                {
                    Status = error;
                    return;
                }

                if (!seenEpcs.Add(epc))
                {
                    Status = $"EPC '{epc}' 重复。";
                    return;
                }

                normalizedEntries.Add(new TagListEntry
                {
                    Id = item.Id,
                    TagListId = SystemTagListId,
                    EpcHex = epc,
                    DisplayName = item.DisplayName.Trim(),
                    ColorHex = color,
                });
            }

            await store.SaveAsync(new TagListDefinition
            {
                Id = SystemTagListId,
                Name = SystemTagListName,
                IsEnabled = true,
                ColorHex = DefaultColor,
                Entries = normalizedEntries,
            }, operationCts.Token);

            foreach (Guid obsoleteListId in loadedListIds.Where(static id => id != SystemTagListId))
            {
                await store.DeleteAsync(obsoleteListId, operationCts.Token);
            }

            loadedListIds = [SystemTagListId];
            Status = $"已保存 {normalizedEntries.Count} 个标签。";
            Changed?.Invoke(this, EventArgs.Empty);
            logger.LogInformation(
                "WPF operation {Operation} completed: {OperationId}, entries {EntryCount}.",
                "SaveTagLists",
                operationId,
                normalizedEntries.Count);
        }
        catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
        {
            // 页面切换或应用退出。
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WPF operation {Operation} failed: {OperationId}.", "SaveTagLists", operationId);
            if (!disposed)
            {
                Status = PlatformErrorDisplay.Failure("保存标签", PlatformErrorCode.PersistenceFailed, ex.Message);
            }
        }
        finally
        {
            EndOperation(operationCts);
        }
    }

    private static bool TryValidateEntry(
        string epc,
        string displayName,
        string colorValue,
        out string color,
        out string? error)
    {
        color = NormalizeColor(colorValue);
        if (epc.Length == 0 || epc.Length % 4 != 0 || !IsHex(epc))
        {
            error = "EPC 必须是非空、完整 16-bit word 的十六进制字符串。";
            return false;
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            error = $"EPC '{epc}' 的 Tag Name 不能为空。";
            return false;
        }

        if (!IsColorHex(color))
        {
            error = $"EPC '{epc}' 的颜色必须是 #RRGGBB 或 #AARRGGBB。";
            return false;
        }

        error = null;
        return true;
    }

    private void NotifyEntryCountChanged() => OnPropertyChanged(nameof(EntryCount));

    private bool TryBeginOperation() =>
        Interlocked.CompareExchange(ref operationInFlight, 1, 0) == 0
        && SetBusyAndReturnTrue();

    private bool SetBusyAndReturnTrue()
    {
        IsBusy = true;
        return true;
    }

    private CancellationTokenSource BeginOperation()
    {
        CancellationTokenSource operationCts =
            CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        CancellationTokenSource? previous = Interlocked.Exchange(ref activeOperationCts, operationCts);
        CancelAndDispose(previous);
        return operationCts;
    }

    private void EndOperation(CancellationTokenSource operationCts)
    {
        Interlocked.CompareExchange(ref activeOperationCts, null, operationCts);
        operationCts.Dispose();
        IsBusy = false;
        Volatile.Write(ref operationInFlight, 0);
    }

    public void CancelPendingOperations()
    {
        try
        {
            Volatile.Read(ref activeOperationCts)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 操作完成和页面切换可能并发。
        }
    }

    private static void CancelAndDispose(CancellationTokenSource? operationCts)
    {
        if (operationCts is null)
        {
            return;
        }

        try
        {
            operationCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        operationCts.Dispose();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CancelPendingOperations();
        lifetimeCts.Cancel();
        lifetimeCts.Dispose();
    }

    private static string NormalizeHex(string? value) => (value ?? string.Empty).Trim()
        .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace(" ", string.Empty, StringComparison.Ordinal)
        .Replace("-", string.Empty, StringComparison.Ordinal)
        .Replace(":", string.Empty, StringComparison.Ordinal)
        .ToUpperInvariant();

    private static bool IsHex(string value) => value.All(static character =>
        character is >= '0' and <= '9'
            or >= 'A' and <= 'F');

    private static string NormalizeColor(string? value)
    {
        string normalized = string.IsNullOrWhiteSpace(value) ? DefaultColor : value.Trim().ToUpperInvariant();
        return normalized.StartsWith('#') ? normalized : $"#{normalized}";
    }

    private static bool IsColorHex(string value) =>
        value.Length is 7 or 9
        && value[0] == '#'
        && IsHex(value[1..]);
}

public sealed partial class TagListEntryEditorItem : ObservableObject
{
    public TagListEntryEditorItem(TagListEntry entry, string inheritedColorHex = "#5EEAD4")
    {
        Id = entry.Id;
        EpcHex = entry.EpcHex;
        DisplayName = entry.DisplayName;
        ColorHex = string.IsNullOrWhiteSpace(entry.ColorHex) ? inheritedColorHex : entry.ColorHex;
    }

    public Guid Id { get; }

    [ObservableProperty]
    private string epcHex;

    [ObservableProperty]
    private string displayName;

    [ObservableProperty]
    private string colorHex;
}
