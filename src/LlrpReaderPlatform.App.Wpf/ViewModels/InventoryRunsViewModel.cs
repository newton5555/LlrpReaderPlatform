using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderPlatform.Contracts.Errors;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Tagging;
using System.Windows.Threading;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>InventoryRun 历史页：只读展示服务层已完成的运行记录和日志路径。</summary>
public partial class InventoryRunsViewModel : ObservableObject, IPageOperationOwner, IDisposable
{
    private readonly IInventoryRunStore store;
    private readonly IInventoryService? inventory;
    private readonly Dispatcher dispatcher;
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly CancellationToken lifetimeToken;
    private readonly SemaphoreSlim loadGate = new(1, 1);
    private CancellationTokenSource? activeLoadCts;
    private long loadGeneration;
    private bool disposed;

    public InventoryRunsViewModel(IInventoryRunStore store, IInventoryService? inventory = null)
    {
        this.store = store;
        this.inventory = inventory;
        dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        lifetimeToken = lifetimeCts.Token;
        if (inventory is not null)
        {
            inventory.LifecycleChanged += OnInventoryLifecycleChanged;
        }
    }

    public ObservableCollection<InventoryRunRowViewModel> Runs { get; } = [];

    [ObservableProperty]
    private Guid? readerId;

    [ObservableProperty]
    private string? status;

    [ObservableProperty]
    private bool isBusy;

    public void SelectReader(Guid? id)
    {
        if (disposed)
        {
            return;
        }

        ReaderId = id;
        _ = StartLoadAsync(id);
    }

    [RelayCommand]
    private Task LoadAsync(Guid? id = null) => StartLoadAsync(id ?? ReaderId);

    private Task StartLoadAsync(Guid? id)
    {
        if (disposed)
        {
            return Task.CompletedTask;
        }

        long generation = Interlocked.Increment(ref loadGeneration);
        CancellationTokenSource loadCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        CancellationTokenSource? previous = Interlocked.Exchange(ref activeLoadCts, loadCts);
        previous?.Cancel();
        IsBusy = true;
        return LoadWithGateAsync(id, generation, loadCts);
    }

    private async Task LoadWithGateAsync(
        Guid? id,
        long generation,
        CancellationTokenSource loadCts)
    {
        try
        {
            await loadGate.WaitAsync(loadCts.Token);
            try
            {
                await LoadCoreAsync(id, generation, loadCts.Token);
            }
            finally
            {
                loadGate.Release();
            }
        }
        catch (OperationCanceledException) when (loadCts.IsCancellationRequested)
        {
            // Reader 切换会取消旧查询；旧查询不能再覆盖当前 Reader 的结果。
        }
        finally
        {
            if (ReferenceEquals(Interlocked.CompareExchange(ref activeLoadCts, null, loadCts), loadCts))
            {
                IsBusy = false;
            }

            loadCts.Dispose();
        }
    }

    private async Task LoadCoreAsync(Guid? id, long generation, CancellationToken ct)
    {
        Guid? target = id ?? ReaderId;
        if (target is null)
        {
            if (IsCurrentLoad(target, generation, ct))
            {
                Runs.Clear();
                Status = "请先在左侧选择 Reader。";
            }

            return;
        }

        try
        {
            IReadOnlyList<InventoryRunRecord> records = await store.GetForReaderAsync(target.Value, ct);
            if (!IsCurrentLoad(target, generation, ct))
            {
                return;
            }

            Runs.Clear();
            foreach (InventoryRunRecord record in records)
            {
                Runs.Add(new InventoryRunRowViewModel(record));
            }

            Status = $"已加载 {Runs.Count} 条运行记录。";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 页面销毁或 Reader 切换时取消运行记录读取。
        }
        catch (Exception ex)
        {
            if (IsCurrentLoad(target, generation, ct))
            {
                Status = PlatformErrorDisplay.Failure("读取运行记录", PlatformErrorCode.PersistenceFailed, ex.Message);
            }
        }
    }

    private bool IsCurrentLoad(Guid? id, long generation, CancellationToken ct) =>
        !disposed
        && !ct.IsCancellationRequested
        && ReaderId == id
        && Volatile.Read(ref loadGeneration) == generation;

    private void OnInventoryLifecycleChanged(object? sender, InventoryLifecycleChangedEventArgs args)
    {
        if (disposed
            || args.State != InventoryLifecycleState.Stopped
            || ReaderId != args.ReaderId)
        {
            return;
        }

        void ReloadCurrentReader()
        {
            if (!disposed && ReaderId == args.ReaderId)
            {
                _ = StartLoadAsync(args.ReaderId);
            }
        }

        if (dispatcher.CheckAccess())
        {
            ReloadCurrentReader();
        }
        else
        {
            TryPostToDispatcher(ReloadCurrentReader);
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
            // WPF shutdown can race the Dispatcher state check.
        }
    }

    public void CancelPendingOperations()
    {
        Interlocked.Increment(ref loadGeneration);
        CancellationTokenSource? loadCts = Volatile.Read(ref activeLoadCts);
        try
        {
            loadCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 页面切换与查询完成的释放可能并发发生。
        }
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
        if (inventory is not null)
        {
            inventory.LifecycleChanged -= OnInventoryLifecycleChanged;
        }
        // 不能在仍可能等待/占用 gate 的后台查询完成前 Dispose SemaphoreSlim；
        // 某些持久化实现可能晚于取消令牌才返回，提前释放会让其 finally
        // 中的 Release 产生未观察异常。ViewModel 随窗口一起释放，gate 本身无需
        // 再承担独立的长期资源生命周期。
    }
}

public sealed record InventoryRunRowViewModel(InventoryRunRecord Record)
{
    public DateTimeOffset StartedAtUtc => Record.StartedAtUtc;
    public DateTimeOffset? EndedAtUtc => Record.EndedAtUtc;
    public string StopReason => Record.StopReason;
    public long TotalReadCount => Record.TotalReadCount;
    public int UniqueTagCount => Record.UniqueTagCount;
    public string LogFilePath => Record.LogFilePath ?? string.Empty;
}
