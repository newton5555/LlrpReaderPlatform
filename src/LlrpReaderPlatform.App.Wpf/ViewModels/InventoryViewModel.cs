using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderPlatform.Contracts.Errors;
using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Tagging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Windows.Threading;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>
/// 寻卡页 ViewModel：启动/停止标准盘存并展示聚合标签。显式刷新，不直接碰协议。
/// </summary>
public partial class InventoryViewModel : ObservableObject, IDisposable
{
    private const string ZeroElapsedText = "0.00 s";
    private const int MaxPendingTags = 2_000;
    private const int MaxDisplayedTags = 1_000;
    private const int MaxTrackedTagObservations = 2_000;
    private const int MaxDrainPerTick = 25;
    private readonly IInventoryService inventory;
    private readonly ILogger<InventoryViewModel> logger;
    private readonly ITagListStore? tagListStore;
    private readonly IReaderManager? readerManager;
    private IReadOnlyDictionary<string, TagLabelMetadata> tagLabels =
        new Dictionary<string, TagLabelMetadata>(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, byte> activeReaderIds = new();
    private readonly ConcurrentDictionary<Guid, byte> startingReaderIds = new();
    private readonly object startBatchGate = new();
    private CancellationTokenSource? activeStartBatchCts;
    private TaskCompletionSource? activeStartBatchCompleted;
    private readonly Dictionary<(Guid ReaderId, string Epc), PendingTag> pendingTags = [];
    private readonly Queue<(Guid ReaderId, string Epc)> pendingTagOrder = [];
    private readonly object pendingTagsGate = new();
    // UI-thread-only latest aggregate per Reader/EPC. Services emit a cumulative
    // per-reader observation, so the WPF layer must replace that component before
    // merging multiple Readers; summing every event would double-count reads.
    private readonly Dictionary<(Guid ReaderId, string Epc), TagObservation> latestObservations = [];
    private readonly Dictionary<string, TagRowViewModel> tagRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dispatcher dispatcher;
    private readonly bool hasWpfApplication;
    private readonly DispatcherTimer refreshTimer;
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly CancellationToken lifetimeToken;
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
    private string durationSecondsText = "0";

    [ObservableProperty]
    private string durationModeText = "Continuous Mode - Runs Forever";

    [ObservableProperty]
    private int uniqueTagCount;

    [ObservableProperty]
    private bool isInventoryRunning;

    [ObservableProperty]
    private bool isInventoryStarting;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string elapsed = ZeroElapsedText;

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
    // 对齐旧 Reader Studio：PC Bits 是可选诊断列，默认不请求，避免给高频
    // TagReport 增加无谓字段；用户可从列头菜单打开后在下一次 Start 生效。
    private bool showPcBitsColumn;

    [ObservableProperty]
    // TID 由 FastID/扩展报告提供，旧页面默认隐藏，避免标准 Reader 上显示
    // 一列长期为空的字段；打开列只改变展示，TID 是否产生仍由设备设置决定。
    private bool showTidColumn;

    [ObservableProperty]
    private bool showReaderColumn = true;

    [ObservableProperty]
    private bool showIndexColumn = true;

    [ObservableProperty]
    private bool showEpcColumn = true;

    public InventoryViewModel(
        IInventoryService inventory,
        ITagListStore? tagListStore = null,
        IReaderManager? readerManager = null,
        ILogger<InventoryViewModel>? logger = null)
    {
        this.inventory = inventory;
        this.logger = logger ?? NullLogger<InventoryViewModel>.Instance;
        this.tagListStore = tagListStore;
        this.readerManager = readerManager;
        hasWpfApplication = System.Windows.Application.Current is not null;
        dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        lifetimeToken = lifetimeCts.Token;
        refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, OnRefreshTimerTick, dispatcher);
        inventory.TagObserved += OnTagObserved;
        inventory.LifecycleChanged += OnInventoryLifecycleChanged;
    }

    public ObservableCollection<TagRowViewModel> Tags { get; } = [];

    /// <summary>
    /// 重新读取 Tag List 映射并刷新当前表格中的标签名称。
    /// Tag List 是应用级数据，保存后不应要求用户停止并重新开始一次 Inventory
    /// 才能看到新的显示名称；该操作只更新 UI 投影，不触碰 Reader Session。
    /// </summary>
    public async Task RefreshTagLabelsAsync(CancellationToken ct = default)
    {
        if (disposed || tagListStore is null)
        {
            return;
        }

        await LoadTagLabelsAsync(ct).ConfigureAwait(true);
        if (disposed || ct.IsCancellationRequested)
        {
            return;
        }

        string[] epcs = Tags.Select(static row => row.Epc).ToArray();
        foreach (string epc in epcs)
        {
            RefreshMergedRow(epc);
        }
    }

    partial void OnDurationSecondsTextChanged(string value) => UpdateDurationModeText(value);

    /// <summary>同步左侧当前 Reader；运行中的全局盘存仍以 activeReaderIds 为准。</summary>
    public void SetReaderContext(ReaderItemViewModel? reader)
    {
        if (disposed)
        {
            return;
        }

        Guid? nextReaderId = reader?.ReaderId;
        bool contextChanged = ReaderId != nextReaderId;
        ReaderId = nextReaderId;

        // 运行中的全局盘存继续展示所有 activeReaderIds 的合并结果；
        // 非运行态则必须把左侧 Reader 的切换/移除投影到表格，否则旧 Reader
        // 的标签会在当前 Reader 已为空或已更换后继续留在页面上。
        if (contextChanged && activeReaderIds.IsEmpty)
        {
            ClearPendingTags();
            latestObservations.Clear();
            tagRows.Clear();
            Tags.Clear();
            Refresh();
        }
    }

    [RelayCommand]
    private async Task StartAsync(Guid id)
    {
        if (!TryBeginLifecycleOperation())
        {
            return;
        }

        try
        {
            await StartCoreAsync(id, lifetimeToken);
        }
        finally
        {
            EndLifecycleOperation();
        }
    }

    private async Task StartCoreAsync(Guid id, CancellationToken operationToken)
    {
        if (disposed)
        {
            return;
        }

        if (activeReaderIds.Count > 0)
        {
            Status = "盘存已在运行，请先停止当前盘存。";
            return;
        }

        ReaderId = id;
        Status = $"正在启动 Reader {ResolveReaderName(id)} 的盘存...";
        Guid operationId = Guid.NewGuid();
        logger.LogInformation(
            "WPF operation {Operation} started: {OperationId}, reader {ReaderId}, duration {DurationSeconds}.",
            "StartInventory",
            operationId,
            id,
            DurationSecondsText);
        await LoadTagLabelsAsync();
        if (disposed)
        {
            return;
        }

        if (!TryBuildStartSpec(out InventorySpec startSpec))
        {
            return;
        }
        ResetForRun([id]);
        startingReaderIds.TryAdd(id, 0);
        StartInventoryResult result;
        try
        {
            result = await inventory.StartInventoryAsync(id, startSpec, operationToken);
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            startingReaderIds.TryRemove(id, out _);
            StopRunUi();
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WPF operation {Operation} failed: {OperationId}, reader {ReaderId}.", "StartInventory", operationId, id);
            startingReaderIds.TryRemove(id, out _);
            IsInventoryRunning = false;
            StopRunUi();
            if (!disposed)
            {
                Status = PlatformErrorDisplay.Failure(
                    "启动",
                    PlatformErrorCode.DeviceFailed,
                    ex.Message);
            }
            return;
        }
        finally
        {
            startingReaderIds.TryRemove(id, out _);
        }

        if (disposed)
        {
            return;
        }

        // A Reader may emit GPI Stop immediately after ROSpec start and before the
        // Start call returns. In that case LifecycleChanged has already converged
        // the UI to the final stop reason; do not overwrite it with a stale
        // "started" acknowledgement.
        bool startIsStillActive = result.Succeeded && activeReaderIds.ContainsKey(id);
        Status = !result.Succeeded
            ? PlatformErrorDisplay.Failure("启动", result.ErrorCode, result.Message)
            : startIsStillActive
                ? "盘存已启动"
                : Status;
        logger.LogInformation(
            "WPF operation {Operation} completed: {OperationId}, reader {ReaderId}, succeeded {Succeeded}, error {Error}.",
            "StartInventory",
            operationId,
            id,
            result.Succeeded,
            result.Error);
        if (!result.Succeeded)
        {
            activeReaderIds.TryRemove(id, out _);
            StopRunUi();
        }

        IsInventoryRunning = activeReaderIds.Count > 0;
        if (result.Succeeded && IsInventoryRunning)
        {
            EnsureRunUiStarted();
        }
    }

    /// <summary>
    /// 对齐旧 WPF 的全局 Start：所有启用 Reader 各自通过平台服务建立独立
    /// Inventory 长连接，任何一个 Reader 失败都不会抢占或破坏其它 Reader 的租约。
    /// </summary>
    [RelayCommand]
    private async Task StartAllAsync()
    {
        CancellationTokenSource batchCts;
        TaskCompletionSource batchCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (startBatchGate)
        {
            if (activeStartBatchCts is not null)
            {
                Status = "Reader 启动操作已在进行中。";
                return;
            }

            batchCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            activeStartBatchCts = batchCts;
            activeStartBatchCompleted = batchCompleted;
        }

        IsInventoryStarting = true;
        try
        {
            await StartAllCoreAsync(batchCts.Token);
        }
        finally
        {
            lock (startBatchGate)
            {
                if (ReferenceEquals(activeStartBatchCts, batchCts))
                {
                    activeStartBatchCts = null;
                    activeStartBatchCompleted = null;
                }
            }

            IsInventoryStarting = false;
            batchCompleted.TrySetResult();
            batchCts.Dispose();
        }
    }

    private async Task StartAllCoreAsync(CancellationToken operationToken)
    {
        if (disposed)
        {
            return;
        }

        if (activeReaderIds.Count > 0)
        {
            Status = "盘存已在运行，请先停止当前盘存。";
            return;
        }

        Status = "正在启动所有启用 Reader 的盘存...";

        if (readerManager is null)
        {
            if (ReaderId is { } selected)
            {
                await StartCoreAsync(selected, operationToken);
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
        if (disposed)
        {
            return;
        }

        if (!TryBuildStartSpec(out InventorySpec startSpec))
        {
            return;
        }
        ResetForRun(targets.Select(static reader => reader.ReaderId));
        foreach (ReaderRuntimeSnapshot target in targets)
        {
            startingReaderIds.TryAdd(target.ReaderId, 0);
        }
        (ReaderRuntimeSnapshot Target, StartInventoryResult Result)[] results = await Task.WhenAll(
            targets.Select(async target =>
            {
                try
                {
                    return (target, await inventory.StartInventoryAsync(target.ReaderId, startSpec, operationToken));
                }
                catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
                {
                    return (target, new StartInventoryResult(false, InventoryError.DeviceFailed, "盘存启动已取消。"));
                }
                catch (Exception ex)
                {
                    return (target, new StartInventoryResult(false, InventoryError.DeviceFailed, ex.Message));
                }
                finally
                {
                    startingReaderIds.TryRemove(target.ReaderId, out _);
                }
            }));

        if (disposed)
        {
            return;
        }

        foreach ((ReaderRuntimeSnapshot target, StartInventoryResult result) in results)
        {
            if (!result.Succeeded)
            {
                activeReaderIds.TryRemove(target.ReaderId, out _);
            }
        }

        IsInventoryRunning = activeReaderIds.Count > 0;
        int successfulStarts = results.Count(static item => item.Result.Succeeded);
        if (!IsInventoryRunning)
        {
            StopRunUi();
            if (successfulStarts == 0)
            {
                Status = $"没有 Reader 能够启动盘存；失败设备：{FormatStartFailures(results)}";
            }

            // A Reader can publish a terminal lifecycle event while another
            // StartAll operation is still completing. The event has already
            // converged the final reason and drained the UI queue; do not
            // restart the stopwatch/timer after that run has ended.
            return;
        }

        EnsureRunUiStarted();
        int failed = results.Length - successfulStarts;
        // If a successful Reader has already published a terminal lifecycle event
        // while StartAll was awaiting the other Readers, keep that event's reason
        // visible instead of replacing it with a startup summary.
        if (activeReaderIds.Count == successfulStarts)
        {
            Status = failed == 0
                ? $"已启动 {results.Length} 个 Reader 的盘存"
                : $"已启动 {activeReaderIds.Count} 个 Reader；失败设备：{FormatStartFailures(results)}";
        }
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
        if (disposed)
        {
            return;
        }

        try
        {
            Status = $"正在停止 Reader {ResolveReaderName(id)} 的盘存...";
            logger.LogInformation("WPF operation {Operation} requested: reader {ReaderId}.", "StopInventory", id);
            await inventory.StopInventoryAsync(id, lifetimeToken);
        }
        catch (OperationCanceledException) when (lifetimeCts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WPF operation {Operation} failed: reader {ReaderId}.", "StopInventory", id);
            if (!disposed)
            {
                Status = PlatformErrorDisplay.Failure(
                    $"停止 Reader {ResolveReaderName(id)}",
                    ex);
            }
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
        if (disposed)
        {
            return;
        }

        Task? startBatch = CancelActiveStartBatch();
        if (startBatch is not null)
        {
            try
            {
                await startBatch.WaitAsync(lifetimeToken);
            }
            catch (OperationCanceledException) when (lifetimeCts.IsCancellationRequested)
            {
                return;
            }
        }

        Guid[] ids = activeReaderIds.Keys.ToArray();
        if (ids.Length == 0)
        {
            StopRunUi();
            Status = "当前没有运行中的盘存。";
            return;
        }

        (Guid ReaderId, Exception? Error)[] errors = await Task.WhenAll(ids.Select(async id =>
        {
            try
            {
                await inventory.StopInventoryAsync(id, lifetimeToken);
                return (id, (Exception?)null);
            }
            catch (OperationCanceledException) when (lifetimeCts.IsCancellationRequested)
            {
                return (id, (Exception?)null);
            }
            catch (Exception ex)
            {
                return (id, ex);
            }
        }));

        if (disposed)
        {
            return;
        }

        int errorCount = errors.Count(static item => item.Error is not null);
        if (errorCount > 0)
        {
            string failedReaders = string.Join(
                "、",
                errors
                    .Where(static item => item.Error is not null)
                    .Select(item => $"{ResolveReaderName(item.ReaderId)}（{item.Error!.Message}）"));
            Status = $"有 {errorCount} 个 Reader 停止失败：{failedReaders}；等待平台生命周期事件收敛。";
        }
    }

    private string FormatStartFailures(
        IEnumerable<(ReaderRuntimeSnapshot Target, StartInventoryResult Result)> results)
    {
        string summary = string.Join(
            "、",
            results
                .Where(static item => !item.Result.Succeeded)
                .Select(item =>
                {
                    string detail = string.IsNullOrWhiteSpace(item.Result.Message)
                        ? item.Result.Error.ToString()
                        : item.Result.Message!;
                    return $"{item.Target.Profile.Name}（{detail}）";
                }));
        return string.IsNullOrWhiteSpace(summary) ? "未知设备" : summary;
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

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ToggleAllAsync()
    {
        if (activeReaderIds.Count > 0 || IsInventoryStarting)
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
            UniqueTagCount = 0;
            UpdateMetrics();
            return;
        }

        ClearPendingTags();
        latestObservations.Clear();
        tagRows.Clear();
        Tags.Clear();
        Interlocked.Exchange(ref reportedTagCount, 0);
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

    private void OnTagObserved(object? sender, TagObservedEventArgs args)
    {
        if (disposed)
        {
            return;
        }

        if (activeReaderIds.ContainsKey(args.ReaderId))
        {
            (Guid ReaderId, string Epc) key = (args.ReaderId, NormalizeEpc(args.Tag.Epc));
            lock (pendingTagsGate)
            {
                if (!pendingTags.ContainsKey(key))
                {
                    while (pendingTags.Count >= MaxPendingTags && pendingTagOrder.Count > 0)
                    {
                        if (pendingTags.Remove(pendingTagOrder.Dequeue()))
                        {
                            Interlocked.Increment(ref droppedUiTagCount);
                        }
                    }

                    pendingTagOrder.Enqueue(key);
                }

                // Keep only the newest cumulative observation for this Reader/EPC.
                // Intermediate reports add no display information and must not create
                // a Dispatcher backlog during a high-rate inventory.
                pendingTags[key] = new PendingTag(args.ReaderId, args.Tag);
            }
        }
    }

    private void OnInventoryLifecycleChanged(object? sender, InventoryLifecycleChangedEventArgs args)
    {
        if (disposed)
        {
            return;
        }

        void ApplyLifecycleState()
        {
            if (disposed)
            {
                return;
            }

            if (args.State == InventoryLifecycleState.Started)
            {
                startingReaderIds.TryRemove(args.ReaderId, out _);
                activeReaderIds.TryAdd(args.ReaderId, 0);
                EnsureRunUiStarted();
                return;
            }

            bool wasStarting = startingReaderIds.TryRemove(args.ReaderId, out _);
            bool wasActive = activeReaderIds.TryRemove(args.ReaderId, out _);
            if (!wasStarting && !wasActive)
            {
                return;
            }

            logger.LogInformation(
                "WPF inventory lifecycle stopped: reader {ReaderId}, reason {StopReason}, error {Error}, displayed tags {DisplayedTagCount}, dropped reports {DroppedTagReportCount}.",
                args.ReaderId,
                args.StopReason,
                args.Error,
                Tags.Count,
                DroppedTagReportCount);

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
            TryPostToDispatcher(ApplyLifecycleState);
        }
    }

    private void TryPostToDispatcher(Action action)
    {
        if (disposed || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _ = dispatcher.BeginInvoke(action);
        }
        catch (InvalidOperationException)
        {
            // WPF may close the Dispatcher between the state check and BeginInvoke
            // during application shutdown. The page is already being disposed, so
            // there is no remaining UI state to project.
        }
    }

    private void OnRefreshTimerTick(object? sender, EventArgs args)
    {
        if (disposed)
        {
            return;
        }

        // 服务层已经按 EPC 聚合；UI 只消费有界批次，避免高频 TagReport 阻塞 Dispatcher。
        var batch = new List<PendingTag>(MaxDrainPerTick);
        lock (pendingTagsGate)
        {
            while (batch.Count < MaxDrainPerTick && pendingTagOrder.Count > 0)
            {
                (Guid ReaderId, string Epc) key = pendingTagOrder.Dequeue();
                if (pendingTags.Remove(key, out PendingTag pending))
                {
                    batch.Add(pending);
                }
            }
        }

        var changedEpcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PendingTag pending in batch)
        {
            StoreObservation(pending.ReaderId, pending.Tag);
            changedEpcs.Add(pending.Tag.Epc);
        }

        foreach (string epc in changedEpcs)
        {
            RefreshMergedRow(epc);
        }

        UniqueTagCount = Tags.Count;
        UpdateMetrics();

        // ReaderManager publishes Stopped only after the service-side report
        // queue is drained. The WPF event handler can still have a bounded set
        // of TagObserved items waiting here, so keep the timer alive until the
        // final UI batch has been projected instead of dropping the last rows.
        if (activeReaderIds.IsEmpty && !HasPendingTags())
        {
            refreshTimer.Stop();
        }
    }

    private void UpdateMetrics()
    {
        DroppedTagReportCount = inventory.DroppedTagReportCount + Interlocked.Read(ref droppedUiTagCount);
        TimeSpan value = stopwatch.Elapsed;
        Elapsed = $"{value.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s";
        double seconds = Math.Max(value.TotalSeconds, 0.001);
        TagRate = $"{reportedTagCount / seconds:0.0} tags/s";
    }

    private async Task LoadTagLabelsAsync(CancellationToken externalToken = default)
    {
        if (tagListStore is null)
        {
            return;
        }

        CancellationToken ct = externalToken.CanBeCanceled ? externalToken : lifetimeToken;

        try
        {
            IReadOnlyList<TagListDefinition> lists = await tagListStore.GetAllAsync(ct);
            tagLabels = lists.Where(static list => list.IsEnabled)
                .SelectMany(static list => list.Entries.Select(entry => new
                {
                    entry.EpcHex,
                    Label = entry.DisplayName,
                    ColorHex = string.IsNullOrWhiteSpace(entry.ColorHex) ? list.ColorHex : entry.ColorHex!,
                }))
                .GroupBy(static x => x.EpcHex, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    static group => group.Key,
                    static group => new TagLabelMetadata(group.First().Label, group.First().ColorHex),
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 页面销毁时取消 Tag List 读取。
        }
        catch (Exception ex)
        {
            if (!disposed)
            {
                Status = PlatformErrorDisplay.Failure("Tag List 加载", PlatformErrorCode.PersistenceFailed, ex.Message);
            }
        }
    }

    private TagLabelMetadata ResolveTagLabel(string epc) =>
        tagLabels.TryGetValue(epc, out TagLabelMetadata label) ? label : TagLabelMetadata.Empty;

    private bool TryBuildStartSpec(out InventorySpec startSpec)
    {
        if (!TryParseDurationSeconds(DurationSecondsText, out int? durationSeconds))
        {
            startSpec = new InventorySpec();
            Status = "寻卡时长必须是 0～86400 的整数秒；0 表示持续运行。";
            return false;
        }

        startSpec = Spec with
        {
            DurationSeconds = durationSeconds,
            Report = new InventoryReportSpec
            {
                // Report configuration is derived from the Inventory table columns;
                // keep device reports granular so the UI and counters stay realtime.
                ReportEveryNTags = 1,
                IncludeAntennaId = ShowAntennaColumn,
                IncludeChannelIndex = ShowChannelColumn,
                IncludePeakRssi = ShowRssiColumn,
                IncludeFirstSeenTimestamp = ShowFirstSeenColumn,
                IncludeLastSeenTimestamp = ShowLastSeenColumn,
                IncludeTagSeenCount = ShowCountColumn,
                IncludePcBits = ShowPcBitsColumn,
            },
        };
        return true;
    }

    private void UpdateDurationModeText(string? text)
    {
        if (!TryParseDurationSeconds(text, out int? durationSeconds))
        {
            DurationModeText = "Invalid Duration";
            return;
        }

        DurationModeText = durationSeconds is null
            ? "Continuous Mode - Runs Forever"
            : $"Duration Mode - {durationSeconds.Value} seconds";
    }

    private static bool TryParseDurationSeconds(string? text, out int? durationSeconds)
    {
        durationSeconds = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            || value is < 0 or > 86_400)
        {
            return false;
        }

        durationSeconds = value == 0 ? null : value;
        return true;
    }

    private void ResetForRun(IEnumerable<Guid> ids)
    {
        runUsedMultipleReaders = ids.Distinct().Skip(1).Any();
        activeReaderIds.Clear();
        startingReaderIds.Clear();
        ClearPendingTags();

        foreach (Guid id in ids.Distinct())
        {
            inventory.ClearTags(id);
        }

        latestObservations.Clear();
        tagRows.Clear();
        Tags.Clear();
        UniqueTagCount = 0;
        Interlocked.Exchange(ref reportedTagCount, 0);
        Interlocked.Exchange(ref droppedUiTagCount, 0);
        stopwatch.Reset();
        Elapsed = ZeroElapsedText;
        TagRate = "0 tags/s";
    }

    private void EnsureRunUiStarted()
    {
        IsInventoryRunning = activeReaderIds.Count > 0;
        if (!IsInventoryRunning || stopwatch.IsRunning)
        {
            return;
        }

        stopwatch.Restart();
        refreshTimer.Start();
        Refresh();
    }

    private Task? CancelActiveStartBatch()
    {
        lock (startBatchGate)
        {
            activeStartBatchCts?.Cancel();
            return activeStartBatchCompleted?.Task;
        }
    }

    private void StopRunUi()
    {
        IsInventoryRunning = false;
        stopwatch.Stop();
        if (hasWpfApplication && HasPendingTags())
        {
            // Drain final TagObserved notifications on the UI dispatcher before
            // stopping the timer. This is deliberately bounded by the existing
            // queue and per-tick batch limit; Stop must not freeze the UI while
            // rendering a high-frequency final report burst.
            refreshTimer.Start();
        }
        else
        {
            refreshTimer.Stop();
        }

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
        latestObservations.TryGetValue(key, out TagObservation? previous);
        if (!latestObservations.ContainsKey(key)
            && latestObservations.Count >= MaxTrackedTagObservations)
        {
            (Guid ReaderId, string Epc) oldest = latestObservations.Keys.First();
            latestObservations.Remove(oldest);
            if (!latestObservations.Keys.Any(key =>
                    string.Equals(key.Epc, oldest.Epc, StringComparison.OrdinalIgnoreCase))
                && tagRows.Remove(oldest.Epc, out TagRowViewModel? removedRow))
            {
                Tags.Remove(removedRow);
            }
            Interlocked.Increment(ref droppedUiTagCount);
        }

        latestObservations[key] = tag;
        long delta = previous is null ? tag.ReadCount : Math.Max(0, tag.ReadCount - previous.ReadCount);
        Interlocked.Add(ref reportedTagCount, delta);
    }

    private void ClearPendingTags()
    {
        lock (pendingTagsGate)
        {
            pendingTags.Clear();
            pendingTagOrder.Clear();
        }
    }

    private bool HasPendingTags()
    {
        lock (pendingTagsGate)
        {
            return pendingTags.Count > 0;
        }
    }

    private void RebuildRows()
    {
        tagRows.Clear();
        Tags.Clear();
        int index = 0;
        foreach (IGrouping<string, KeyValuePair<(Guid ReaderId, string Epc), TagObservation>> group in
            latestObservations.GroupBy(static item => item.Key.Epc, StringComparer.OrdinalIgnoreCase))
        {
            if (Tags.Count >= MaxDisplayedTags)
            {
                break;
            }

            TagRowViewModel row = CreateMergedRow(group.Key, group);
            row.Index = ++index;
            tagRows.Add(group.Key, row);
            Tags.Add(row);
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

        MergedTagProjection projection = CreateMergedProjection(normalizedEpc, group);
        if (tagRows.TryGetValue(normalizedEpc, out TagRowViewModel? existing))
        {
            existing.Update(
                projection.ReaderName,
                projection.Tag,
                projection.TagList.Name,
                projection.TagList.ColorHex);
            return;
        }

        Status = "正在停止所有 Reader 的盘存...";

        if (Tags.Count >= MaxDisplayedTags)
        {
            TagRowViewModel removed = Tags[0];
            Tags.RemoveAt(0);
            tagRows.Remove(removed.Epc);
        }

        if (Tags.Count < MaxDisplayedTags)
        {
            var row = new TagRowViewModel(
                Guid.Empty,
                projection.ReaderName,
                projection.Tag,
                projection.TagList.Name,
                projection.TagList.ColorHex)
            {
                Index = Tags.Count + 1,
            };
            tagRows.Add(normalizedEpc, row);
            Tags.Add(row);
        }
    }

    private TagRowViewModel CreateMergedRow(
        string epc,
        IEnumerable<KeyValuePair<(Guid ReaderId, string Epc), TagObservation>> observations)
    {
        MergedTagProjection projection = CreateMergedProjection(epc, observations);
        return new TagRowViewModel(
            Guid.Empty,
            projection.ReaderName,
            projection.Tag,
            projection.TagList.Name,
            projection.TagList.ColorHex);
    }

    private MergedTagProjection CreateMergedProjection(
        string epc,
        IEnumerable<KeyValuePair<(Guid ReaderId, string Epc), TagObservation>> observations)
    {
        KeyValuePair<(Guid ReaderId, string Epc), TagObservation>[] values = observations.ToArray();
        TagObservation merged = MergeObservations(epc, values.Select(static value => value.Value));
        string readers = string.Join(", ", values
            .Select(value => ResolveReaderName(value.Key.ReaderId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase));
        return new MergedTagProjection(readers, merged, ResolveTagLabel(epc));
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
        lifetimeCts.Cancel();
        lifetimeCts.Dispose();
        inventory.TagObserved -= OnTagObserved;
        inventory.LifecycleChanged -= OnInventoryLifecycleChanged;
        activeReaderIds.Clear();
        startingReaderIds.Clear();
        refreshTimer.Stop();
        ClearPendingTags();
    }

    private readonly record struct PendingTag(Guid ReaderId, TagObservation Tag);
    private readonly record struct MergedTagProjection(
        string ReaderName,
        TagObservation Tag,
        TagLabelMetadata TagList);

    private readonly record struct TagLabelMetadata(string Name, string ColorHex)
    {
        public static TagLabelMetadata Empty { get; } = new(string.Empty, string.Empty);
    }
}
