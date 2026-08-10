using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Tagging;
using System.Windows.Threading;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>
/// 寻卡页 ViewModel：启动/停止标准盘存并展示聚合标签。显式刷新，不直接碰协议。
/// </summary>
public partial class InventoryViewModel : ObservableObject, IDisposable
{
    private const int MaxPendingTags = 20_000;
    private const int MaxDisplayedTagObservations = 10_000;
    private readonly IInventoryService inventory;
    private readonly ITagListStore? tagListStore;
    private readonly IReaderManager? readerManager;
    private IReadOnlyDictionary<string, string> tagLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, byte> activeReaderIds = new();
    private readonly Queue<PendingTag> pendingTags = new();
    private readonly object pendingTagsGate = new();
    // UI-thread-only latest aggregate per Reader/EPC. Services emit a cumulative
    // per-reader observation, so the WPF layer must replace that component before
    // merging multiple Readers; summing every event would double-count reads.
    private readonly Dictionary<(Guid ReaderId, string Epc), TagObservation> latestObservations = [];
    private readonly Dispatcher dispatcher;
    private readonly bool hasWpfApplication;
    private readonly DispatcherTimer refreshTimer;
    private bool disposed;
    private int lifecycleOperationInFlight;
    private readonly Stopwatch stopwatch = new();
    private long reportedTagCount;
    private long droppedUiTagCount;
    private bool runUsedMultipleReaders;

    [ObservableProperty]
    private Guid? readerId;

    [ObservableProperty]
    private string? status;

    [ObservableProperty]
    // 空集合表示使用 Reader 当前配置的全部天线；旧项目默认不会悄悄限制到天线 1。
    private InventorySpec spec = new();

    [ObservableProperty]
    private int uniqueTagCount;

    [ObservableProperty]
    private bool isInventoryRunning;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string elapsed = "00:00:00";

    [ObservableProperty]
    private string tagRate = "0 tags/s";

    [ObservableProperty]
    private long droppedTagReportCount;

    [ObservableProperty]
    private bool showAntennaColumn = true;

    [ObservableProperty]
    private bool showChannelColumn = true;

    [ObservableProperty]
    private bool showRssiColumn = true;

    [ObservableProperty]
    private bool showFirstSeenColumn = true;

    [ObservableProperty]
    private bool showLastSeenColumn = true;

    [ObservableProperty]
    private bool showCountColumn = true;

    [ObservableProperty]
    private bool showPcBitsColumn = true;

    [ObservableProperty]
    private bool showTidColumn = true;

    [ObservableProperty]
    private bool showReaderColumn = true;

    [ObservableProperty]
    private bool showIndexColumn = true;

    public InventoryViewModel(
        IInventoryService inventory,
        ITagListStore? tagListStore = null,
        IReaderManager? readerManager = null)
    {
        this.inventory = inventory;
        this.tagListStore = tagListStore;
        this.readerManager = readerManager;
        hasWpfApplication = System.Windows.Application.Current is not null;
        dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, OnRefreshTimerTick, dispatcher);
        inventory.TagObserved += OnTagObserved;
        inventory.LifecycleChanged += OnInventoryLifecycleChanged;
    }

    public ObservableCollection<TagRowViewModel> Tags { get; } = [];

    [RelayCommand]
    private async Task StartAsync(Guid id)
    {
        if (!TryBeginLifecycleOperation())
        {
            return;
        }

        try
        {
            await StartCoreAsync(id);
        }
        finally
        {
            EndLifecycleOperation();
        }
    }

    private async Task StartCoreAsync(Guid id)
    {
        if (activeReaderIds.Count > 0)
        {
            Status = "盘存已在运行，请先停止当前盘存。";
            return;
        }

        ReaderId = id;
        await LoadTagLabelsAsync();
        ResetForRun([id]);
        activeReaderIds.TryAdd(id, 0);

        InventorySpec startSpec = BuildStartSpec();
        StartInventoryResult result;
        try
        {
            result = await inventory.StartInventoryAsync(id, startSpec, CancellationToken.None);
        }
        catch (Exception ex)
        {
            activeReaderIds.TryRemove(id, out _);
            IsInventoryRunning = false;
            StopRunUi();
            Status = $"启动失败: {ex.Message}";
            return;
        }

        Status = result.Succeeded
            ? "盘存已启动"
            : $"启动失败: {result.Message} ({(result.Error == InventoryError.ReaderBusy ? "Reader 忙碌" : "设备错误")})";
        if (!result.Succeeded)
        {
            activeReaderIds.TryRemove(id, out _);
            StopRunUi();
        }

        IsInventoryRunning = activeReaderIds.Count > 0;
        if (result.Succeeded && IsInventoryRunning)
        {
            stopwatch.Restart();
            refreshTimer.Start();
            Refresh();
        }
    }

    /// <summary>
    /// 对齐旧 WPF 的全局 Start：所有启用 Reader 各自通过平台服务建立独立
    /// Inventory 长连接，任何一个 Reader 失败都不会抢占或破坏其它 Reader 的租约。
    /// </summary>
    [RelayCommand]
    private async Task StartAllAsync()
    {
        if (!TryBeginLifecycleOperation())
        {
            return;
        }

        try
        {
            await StartAllCoreAsync();
        }
        finally
        {
            EndLifecycleOperation();
        }
    }

    private async Task StartAllCoreAsync()
    {
        if (activeReaderIds.Count > 0)
        {
            Status = "盘存已在运行，请先停止当前盘存。";
            return;
        }

        if (readerManager is null)
        {
            if (ReaderId is { } selected)
            {
                await StartCoreAsync(selected);
            }
            else
            {
                Status = "请先选择 Reader。";
            }

            return;
        }

        ReaderRuntimeSnapshot[] targets = readerManager.Readers
            .Where(static reader => reader.IsEnabled)
            .ToArray();
        if (targets.Length == 0)
        {
            Status = "请先添加并启用 Reader。";
            return;
        }

        await LoadTagLabelsAsync();
        ResetForRun(targets.Select(static reader => reader.ReaderId));
        foreach (ReaderRuntimeSnapshot target in targets)
        {
            activeReaderIds.TryAdd(target.ReaderId, 0);
        }

        InventorySpec startSpec = BuildStartSpec();
        (ReaderRuntimeSnapshot Target, StartInventoryResult Result)[] results = await Task.WhenAll(
            targets.Select(async target =>
            {
                try
                {
                    return (target, await inventory.StartInventoryAsync(target.ReaderId, startSpec, CancellationToken.None));
                }
                catch (Exception ex)
                {
                    return (target, new StartInventoryResult(false, InventoryError.DeviceFailed, ex.Message));
                }
            }));

        foreach ((ReaderRuntimeSnapshot target, StartInventoryResult result) in results)
        {
            if (!result.Succeeded)
            {
                activeReaderIds.TryRemove(target.ReaderId, out _);
            }
        }

        IsInventoryRunning = activeReaderIds.Count > 0;
        if (!IsInventoryRunning)
        {
            StopRunUi();
            Status = "没有 Reader 能够启动盘存。";
            return;
        }

        stopwatch.Restart();
        refreshTimer.Start();
        Refresh();
        int failed = results.Count(static item => !item.Result.Succeeded);
        Status = failed == 0
            ? $"已启动 {results.Length} 个 Reader 的盘存"
            : $"已启动 {activeReaderIds.Count} 个 Reader，{failed} 个 Reader 启动失败。";
    }

    [RelayCommand]
    private async Task StopAsync(Guid id)
    {
        if (!TryBeginLifecycleOperation())
        {
            return;
        }

        try
        {
            await StopCoreAsync(id);
        }
        finally
        {
            EndLifecycleOperation();
        }
    }

    private async Task StopCoreAsync(Guid id)
    {
        try
        {
            await inventory.StopInventoryAsync(id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Status = $"停止 Reader {ResolveReaderName(id)} 失败: {ex.Message}";
            return;
        }

        // ReaderManager 会在完成 Stop、排空报告、落库并断开 Session 后发布
        // LifecycleChanged。UI 状态统一由该事件收敛，避免按钮路径和设备事件各自维护一份状态。
    }

    [RelayCommand]
    private async Task StopAllAsync()
    {
        if (!TryBeginLifecycleOperation())
        {
            return;
        }

        try
        {
            await StopAllCoreAsync();
        }
        finally
        {
            EndLifecycleOperation();
        }
    }

    private async Task StopAllCoreAsync()
    {
        Guid[] ids = activeReaderIds.Keys.ToArray();
        if (ids.Length == 0)
        {
            StopRunUi();
            Status = "当前没有运行中的盘存。";
            return;
        }

        Exception?[] errors = await Task.WhenAll(ids.Select(async id =>
        {
            try
            {
                await inventory.StopInventoryAsync(id, CancellationToken.None);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }));

        int errorCount = errors.Count(static error => error is not null);
        if (errorCount > 0)
        {
            Status = $"有 {errorCount} 个 Reader 停止失败，等待平台生命周期事件收敛。";
        }
    }

    [RelayCommand]
    private async Task ToggleAsync(Guid id)
    {
        if (IsInventoryRunning)
        {
            await StopAsync(id);
        }
        else
        {
            await StartAsync(id);
        }
    }

    [RelayCommand]
    private async Task ToggleAllAsync()
    {
        if (activeReaderIds.Count > 0)
        {
            await StopAllAsync();
        }
        else
        {
            await StartAllAsync();
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        Guid[] ids = GetVisibleReaderIds().ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        latestObservations.Clear();
        Tags.Clear();
        foreach (Guid id in ids)
        {
            foreach (TagObservation tag in inventory.GetTags(id))
            {
                StoreObservation(id, tag);
            }
        }

        RebuildRows();

        UniqueTagCount = Tags.Count;
        UpdateMetrics();
    }

    [RelayCommand]
    private void Clear()
    {
        foreach (Guid id in GetVisibleReaderIds())
        {
            inventory.ClearTags(id);
        }

        ClearPendingTags();

        latestObservations.Clear();
        Tags.Clear();
        UniqueTagCount = 0;
        Interlocked.Exchange(ref reportedTagCount, 0);
        Interlocked.Exchange(ref droppedUiTagCount, 0);
        if (IsInventoryRunning)
        {
            stopwatch.Restart();
        }
        else
        {
            stopwatch.Reset();
        }

        Elapsed = "00:00:00";
        TagRate = "0 tags/s";
    }

    private void OnTagObserved(object? sender, TagObservedEventArgs args)
    {
        if (activeReaderIds.ContainsKey(args.ReaderId))
        {
            Interlocked.Increment(ref reportedTagCount);
            lock (pendingTagsGate)
            {
                if (pendingTags.Count >= MaxPendingTags)
                {
                    pendingTags.Dequeue();
                    Interlocked.Increment(ref droppedUiTagCount);
                }

                pendingTags.Enqueue(new PendingTag(args.ReaderId, args.Tag));
            }
        }
    }

    private void OnInventoryLifecycleChanged(object? sender, InventoryLifecycleChangedEventArgs args)
    {
        void ApplyLifecycleState()
        {
            if (args.State == InventoryLifecycleState.Started)
            {
                activeReaderIds.TryAdd(args.ReaderId, 0);
                IsInventoryRunning = true;
                return;
            }

            if (!activeReaderIds.TryRemove(args.ReaderId, out _))
            {
                return;
            }

            if (activeReaderIds.Count == 0)
            {
                StopRunUi();
            }
            else
            {
                IsInventoryRunning = true;
            }

            string readerName = ResolveReaderName(args.ReaderId);
            string suffix = string.IsNullOrWhiteSpace(args.Error) ? string.Empty : $": {args.Error}";
            bool isConnectionFailure = args.StopReason is InventoryStopReason.DeviceDisconnected
                or InventoryStopReason.ConnectionFaulted
                or InventoryStopReason.ReaderException;
            string reason = args.StopReason switch
            {
                InventoryStopReason.Gpi => "GPI 触发",
                InventoryStopReason.Duration => "达到时长",
                InventoryStopReason.DeviceDisconnected => "设备断开",
                InventoryStopReason.ConnectionFaulted => "连接故障",
                InventoryStopReason.ReaderException => "Reader 异常",
                InventoryStopReason.Removed => "Reader 已移除",
                InventoryStopReason.Deactivated => "Reader 已停用",
                InventoryStopReason.ApplicationExit => "应用退出",
                InventoryStopReason.StopFailed => "停止失败",
                _ => "手动停止",
            };
            if (activeReaderIds.Count == 0 && args.StopReason == InventoryStopReason.Manual)
            {
                Status = runUsedMultipleReaders ? "已停止盘存并断开所有 Reader。" : "已停止盘存";
            }
            else if (activeReaderIds.Count == 0 && isConnectionFailure)
            {
                Status = $"Reader {readerName} 连接异常，盘存已停止{suffix}";
            }
            else
            {
                Status = activeReaderIds.Count == 0
                    ? $"Reader {readerName} 已停止盘存（{reason}）{suffix}"
                    : $"Reader {readerName} 已停止盘存（{reason}），仍有 {activeReaderIds.Count} 个 Reader 运行中{suffix}";
            }
        }

        if (!hasWpfApplication || dispatcher.CheckAccess())
        {
            ApplyLifecycleState();
        }
        else
        {
            _ = dispatcher.BeginInvoke(ApplyLifecycleState);
        }
    }

    private void OnRefreshTimerTick(object? sender, EventArgs args)
    {
        if (disposed)
        {
            return;
        }

        // 服务层已经按 EPC 聚合；UI 只消费有界批次，避免高频 TagReport 阻塞 Dispatcher。
        int drained = 0;
        while (drained++ < 500)
        {
            PendingTag pending;
            lock (pendingTagsGate)
            {
                if (pendingTags.Count == 0)
                {
                    break;
                }

                pending = pendingTags.Dequeue();
            }

            StoreObservation(pending.ReaderId, pending.Tag);
            RefreshMergedRow(pending.Tag.Epc);
        }

        UniqueTagCount = Tags.Count;
        UpdateMetrics();
    }

    private void UpdateMetrics()
    {
        DroppedTagReportCount = inventory.DroppedTagReportCount + Interlocked.Read(ref droppedUiTagCount);
        TimeSpan value = stopwatch.Elapsed;
        Elapsed = value.ToString(value.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
        double seconds = Math.Max(value.TotalSeconds, 0.001);
        TagRate = $"{reportedTagCount / seconds:0.0} tags/s";
    }

    private async Task LoadTagLabelsAsync()
    {
        if (tagListStore is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<TagListDefinition> lists = await tagListStore.GetAllAsync(CancellationToken.None);
            tagLabels = lists.Where(static list => list.IsEnabled)
                .SelectMany(static list => list.Entries.Select(entry => new
                {
                    entry.EpcHex,
                    Label = string.IsNullOrWhiteSpace(entry.DisplayName) ? list.Name : $"{list.Name}: {entry.DisplayName}",
                }))
                .GroupBy(static x => x.EpcHex, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.First().Label, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Status = $"Tag List 加载失败：{ex.Message}";
        }
    }

    private string ResolveTagLabel(string epc) =>
        tagLabels.TryGetValue(epc, out string? label) ? label : string.Empty;

    private InventorySpec BuildStartSpec() => Spec with
    {
        Report = new InventoryReportSpec
        {
            IncludeAntennaId = ShowAntennaColumn,
            IncludeChannelIndex = ShowChannelColumn,
            IncludePeakRssi = ShowRssiColumn,
            IncludeFirstSeenTimestamp = ShowFirstSeenColumn,
            IncludeLastSeenTimestamp = ShowLastSeenColumn,
            IncludeTagSeenCount = ShowCountColumn,
            IncludePcBits = ShowPcBitsColumn,
        },
    };

    private void ResetForRun(IEnumerable<Guid> ids)
    {
        runUsedMultipleReaders = ids.Distinct().Skip(1).Any();
        activeReaderIds.Clear();
        ClearPendingTags();

        foreach (Guid id in ids.Distinct())
        {
            inventory.ClearTags(id);
        }

        latestObservations.Clear();
        Tags.Clear();
        UniqueTagCount = 0;
        Interlocked.Exchange(ref reportedTagCount, 0);
        Interlocked.Exchange(ref droppedUiTagCount, 0);
        stopwatch.Reset();
        Elapsed = "00:00:00";
        TagRate = "0 tags/s";
    }

    private void StopRunUi()
    {
        IsInventoryRunning = false;
        stopwatch.Stop();
        refreshTimer.Stop();
        UpdateMetrics();
    }

    private bool TryBeginLifecycleOperation()
    {
        if (Interlocked.CompareExchange(ref lifecycleOperationInFlight, 1, 0) != 0)
        {
            Status = "盘存操作进行中，请稍候。";
            return false;
        }

        IsBusy = true;
        return true;
    }

    private void EndLifecycleOperation()
    {
        IsBusy = false;
        Interlocked.Exchange(ref lifecycleOperationInFlight, 0);
    }

    private IEnumerable<Guid> GetVisibleReaderIds()
    {
        if (activeReaderIds.Count > 0)
        {
            return activeReaderIds.Keys;
        }

        return ReaderId is { } selected ? [selected] : [];
    }

    private string ResolveReaderName(Guid id) => readerManager?.Readers
        .FirstOrDefault(reader => reader.ReaderId == id)?.Profile.Name
        ?? (ReaderId == id ? "Reader" : id.ToString());

    private void StoreObservation(Guid id, TagObservation tag)
    {
        (Guid ReaderId, string Epc) key = (id, NormalizeEpc(tag.Epc));
        if (!latestObservations.ContainsKey(key)
            && latestObservations.Count >= MaxDisplayedTagObservations)
        {
            (Guid ReaderId, string Epc) oldest = latestObservations.Keys.First();
            latestObservations.Remove(oldest);
            Interlocked.Increment(ref droppedUiTagCount);
        }

        latestObservations[key] = tag;
    }

    private void ClearPendingTags()
    {
        lock (pendingTagsGate)
        {
            pendingTags.Clear();
        }
    }

    private void RebuildRows()
    {
        Tags.Clear();
        int index = 0;
        foreach (IGrouping<string, KeyValuePair<(Guid ReaderId, string Epc), TagObservation>> group in
            latestObservations.GroupBy(static item => item.Key.Epc, StringComparer.OrdinalIgnoreCase))
        {
            if (Tags.Count >= 10_000)
            {
                break;
            }

            Tags.Add(CreateMergedRow(group.Key, group) with { Index = ++index });
        }
    }

    private void RefreshMergedRow(string epc)
    {
        string normalizedEpc = NormalizeEpc(epc);
        IGrouping<string, KeyValuePair<(Guid ReaderId, string Epc), TagObservation>>? group =
            latestObservations
                .Where(item => string.Equals(item.Key.Epc, normalizedEpc, StringComparison.OrdinalIgnoreCase))
                .GroupBy(static item => item.Key.Epc, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        if (group is null)
        {
            return;
        }

        int index = -1;
        for (int i = 0; i < Tags.Count; i++)
        {
            if (string.Equals(Tags[i].Epc, normalizedEpc, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }

        TagRowViewModel row = CreateMergedRow(normalizedEpc, group) with
        {
            Index = index >= 0 ? Tags[index].Index : Tags.Count + 1,
        };

        if (index >= 0)
        {
            Tags[index] = row;
        }
        else if (Tags.Count < 10_000)
        {
            Tags.Add(row);
        }
    }

    private TagRowViewModel CreateMergedRow(
        string epc,
        IEnumerable<KeyValuePair<(Guid ReaderId, string Epc), TagObservation>> observations)
    {
        KeyValuePair<(Guid ReaderId, string Epc), TagObservation>[] values = observations.ToArray();
        TagObservation merged = MergeObservations(epc, values.Select(static value => value.Value));
        string readers = string.Join(", ", values
            .Select(value => ResolveReaderName(value.Key.ReaderId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase));
        return new TagRowViewModel(Guid.Empty, readers, merged, ResolveTagLabel(epc));
    }

    private static TagObservation MergeObservations(string epc, IEnumerable<TagObservation> observations)
    {
        TagObservation[] values = observations.ToArray();
        TagObservation latest = values.OrderByDescending(static value => value.LastSeen).First();
        return latest with
        {
            Epc = epc,
            ReadCount = values.Sum(static value => value.ReadCount),
            FirstSeen = values.Min(static value => value.FirstSeen),
            LastSeen = values.Max(static value => value.LastSeen),
        };
    }

    private static string NormalizeEpc(string epc) => epc.ToUpperInvariant();

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        inventory.TagObserved -= OnTagObserved;
        inventory.LifecycleChanged -= OnInventoryLifecycleChanged;
        activeReaderIds.Clear();
        refreshTimer.Stop();
        ClearPendingTags();
    }

    private readonly record struct PendingTag(Guid ReaderId, TagObservation Tag);
}
