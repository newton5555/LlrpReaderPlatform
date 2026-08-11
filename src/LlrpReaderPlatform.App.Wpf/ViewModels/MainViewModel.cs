using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Discovery;
using LlrpReaderPlatform.Contracts.Errors;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Contracts.Tagging;
using System.Windows.Threading;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>
/// 设备管理（DataSources）：列表、状态、增删与 Enable 开关。只消费 IReaderManager，
/// 不直接碰 SDK 或厂商类型。刷新采用显式刷新。
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly IReaderManager readerManager;
    private readonly IReaderDiscoveryService discovery;
    private readonly Dispatcher dispatcher;
    private readonly CancellationTokenSource discoveryCts = new();
    private readonly CancellationToken lifetimeToken;
    private CancellationTokenSource? activeSettingsLoadCts;
    private bool disposed;

    [ObservableProperty]
    private string host = "192.0.2.1";

    [ObservableProperty]
    private ushort port = 5084;

    [ObservableProperty]
    private string readerName = "Reader";

    [ObservableProperty]
    private string? status;

    public MainViewModel(
        IReaderManager readerManager,
        IReaderSettingsService settings,
        IInventoryService inventory,
        IReaderDiscoveryService discovery,
        IAppSettingsStore appSettingsStore,
        ITagListStore tagListStore,
        IInventoryRunStore inventoryRunStore)
    {
        this.readerManager = readerManager;
        this.discovery = discovery;
        dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        lifetimeToken = discoveryCts.Token;
        readerManager.StateChanged += OnReaderStateChanged;
        Inventory = new InventoryViewModel(inventory, tagListStore, readerManager);
        TagMemory = new TagMemoryViewModel(inventory);
        Diagnostics = new DiagnosticsViewModel(inventory);
        Settings = new ReaderSettingsViewModel(settings, Diagnostics, readerManager);
        Settings.CancelRequested += OnSettingsCancelRequested;
        AppSettings = new AppSettingsViewModel(appSettingsStore);
        TagLists = new TagListsViewModel(tagListStore);
        InventoryRuns = new InventoryRunsViewModel(inventoryRunStore, inventory);

        AddDataSource = new AddDataSourceViewModel(readerManager, discovery);
        AddDataSource.DataSourceAdded += OnDataSourceAdded;
        AddDataSource.CancelRequested += OnAddDataSourceCancelled;
        CurrentPage = Inventory;
    }

    public ReaderSettingsViewModel Settings { get; }
    public InventoryViewModel Inventory { get; }
    public TagMemoryViewModel TagMemory { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public AboutViewModel About { get; } = new();
    public AppSettingsViewModel AppSettings { get; } = null!;
    public TagListsViewModel TagLists { get; }
    public InventoryRunsViewModel InventoryRuns { get; }

    public ObservableCollection<ReaderItemViewModel> Readers { get; } = [];

    /// <summary>mDNS 扫描到的 Reader。</summary>
    public ObservableCollection<DiscoveredReaderViewModel> Discovered { get; } = [];

    [ObservableProperty]
    private ReaderItemViewModel? selectedReader;

    // Refresh() rebuilds the ListBox items after a Reader state event. WPF can
    // transiently write SelectedReader = null while the old item is removed;
    // that transient value must not cancel an in-flight settings load.
    private bool refreshingReaderList;

    partial void OnSelectedReaderChanged(ReaderItemViewModel? value)
    {
        if (refreshingReaderList)
        {
            return;
        }

        ApplySelectedReaderContext(value);
    }

    private void ApplySelectedReaderContext(ReaderItemViewModel? value)
    {
        Inventory.SetReaderContext(value);
        TagMemory.SetReaderContext(value);
        Settings.SetReaderContext(value);
        if (ReferenceEquals(CurrentPage, InventoryRuns)
            && InventoryRuns.ReaderId != value?.ReaderId)
        {
            InventoryRuns.SelectReader(value?.ReaderId);
        }
    }

    /// <summary>当前导航页（ContentControl 路由）。</summary>
    [ObservableProperty]
    private object? currentPage;

    [ObservableProperty]
    private bool isBusy;

    public AddDataSourceViewModel AddDataSource { get; }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (disposed)
        {
            return;
        }

        IsBusy = true;
        Status = "正在初始化 Reader 平台...";
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetimeToken);
            await readerManager.InitializeAsync(linked.Token);
            await AppSettings.LoadAsync(linked.Token);
            Refresh();
            Status = Readers.Count == 0 ? "平台已就绪，请添加 Reader。" : $"平台已就绪，已加载 {Readers.Count} 个 Reader。";
        }
        catch (OperationCanceledException) when (discoveryCts.IsCancellationRequested)
        {
            // 窗口退出时取消启动恢复。
        }
        catch (Exception ex)
        {
            Status = PlatformErrorDisplay.Failure("平台初始化", ex);
            throw;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public void Refresh()
    {
        if (disposed)
        {
            return;
        }

        Guid? selectedReaderId = SelectedReader?.ReaderId;
        refreshingReaderList = true;
        try
        {
            Readers.Clear();
            foreach (ReaderRuntimeSnapshot snapshot in readerManager.Readers)
            {
                Readers.Add(new ReaderItemViewModel(snapshot, enabled =>
                {
                    _ = SetReaderEnabledFromListAsync(snapshot.ReaderId, enabled);
                }));
            }

            SelectedReader = selectedReaderId is { } id
                ? Readers.FirstOrDefault(reader => reader.ReaderId == id)
                : Readers.FirstOrDefault();
        }
        finally
        {
            refreshingReaderList = false;
        }

        // Apply the final item once, after the ListBox's transient null selection
        // has been suppressed. This keeps settings/inventory/tag-memory context
        // aligned with the rebuilt ReaderItemViewModel instance.
        ApplySelectedReaderContext(SelectedReader);
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        var profile = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(ReaderName) ? Host : ReaderName,
            Host = Host,
            Port = Port,
            IsEnabled = true,
        };

        try
        {
            ReaderAddResult result = await readerManager.AddAsync(profile, enableAfterAdding: true, lifetimeToken);
            Status = result.Succeeded
                ? $"已添加并同步 {profile.Host}:{profile.Port}"
                : PlatformErrorDisplay.Failure("添加", result.ErrorCode, result.Error);
        }
        catch (OperationCanceledException) when (discoveryCts.IsCancellationRequested)
        {
            // 窗口退出时取消添加 Reader。
        }
        catch (Exception ex)
        {
            if (!disposed)
            {
                Status = PlatformErrorDisplay.Failure("添加", ex);
            }
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task RemoveAsync(Guid readerId)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            bool removedSettingsReader = Settings.ReaderId == readerId;
            await readerManager.RemoveAsync(readerId, lifetimeToken);
            if (removedSettingsReader)
            {
                Settings.SetReaderContext(null);
                CurrentPage = Inventory;
            }

            Status = "已移除";
        }
        catch (OperationCanceledException) when (discoveryCts.IsCancellationRequested)
        {
            // 窗口退出时取消移除 Reader。
        }
        catch (Exception ex)
        {
            if (!disposed)
            {
                Status = PlatformErrorDisplay.Failure("移除", ex);
            }
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task SetEnabledAsync(ReaderItemViewModel item)
    {
        await SetReaderEnabledFromListAsync(item.ReaderId, !item.IsEnabled);
    }

    [RelayCommand]
    private async Task ActivateAsync(ReaderItemViewModel item)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            ReaderActivationResult result = await readerManager.ActivateAsync(item.ReaderId, lifetimeToken);
            Status = result.Succeeded
                ? "激活成功"
                : PlatformErrorDisplay.Failure("激活", result.ErrorCode, result.Error);
        }
        catch (OperationCanceledException) when (discoveryCts.IsCancellationRequested)
        {
            // 窗口退出时取消 Reader 激活。
        }
        catch (Exception ex)
        {
            if (!disposed)
            {
                Status = PlatformErrorDisplay.Failure("激活", ex);
            }
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task DiscoverAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        Status = "正在扫描 _llrp._tcp...";
        try
        {
            IReadOnlyList<DiscoveredReader> found = await discovery.DiscoverAsync(TimeSpan.FromSeconds(3), lifetimeToken);
            IReadOnlyList<DiscoveredReader> normalized = DiscoveredReaderNormalization.Normalize(found);
            Discovered.Clear();
            foreach (DiscoveredReader r in normalized)
            {
                Discovered.Add(new DiscoveredReaderViewModel(r));
            }

            Status = normalized.Count == 0 ? "未发现 LLRP 设备" : $"发现 {normalized.Count} 个设备，可选用后再添加";
        }
        catch (OperationCanceledException) when (discoveryCts.IsCancellationRequested)
        {
            if (!disposed)
            {
                Status = "发现已取消。";
            }
        }
        catch (Exception ex)
        {
            if (!disposed)
            {
                Discovered.Clear();
                Status = PlatformErrorDisplay.Failure("发现", ex);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void UseDiscovered(DiscoveredReaderViewModel item)
    {
        Host = item.IpAddress;
        ReaderName = item.DisplayName;
        Port = (ushort)Math.Clamp(item.Port, 1, 65535);
        Status = $"已选用 {item.DisplayName}，可点击 添加";
    }

    /// <summary>左栏导航：切换到对应页面。</summary>
    [RelayCommand]
    private void Navigate(string page)
    {
        object nextPage = page switch
        {
            "Inventory" => Inventory,
            "TagMemory" => TagMemory,
            // GPI/GPO 属于旧 WPF 设备设置页的 Tab2，不再作为独立页面。
            "Diagnostics" => Settings,
            "TagLists" => TagLists,
            "InventoryRuns" => InventoryRuns,
            "AppSettings" => AppSettings,
            "About" => About,
            "AddDataSource" => AddDataSource,
            _ => Settings,
        };

        if (!ReferenceEquals(CurrentPage, nextPage))
        {
            CancelPendingPageOperations(CurrentPage);
        }

        CurrentPage = nextPage;

        if (string.Equals(page, "TagLists", StringComparison.Ordinal))
        {
            _ = TagLists.LoadCommand.ExecuteAsync(null);
        }
        else if (string.Equals(page, "InventoryRuns", StringComparison.Ordinal))
        {
            InventoryRuns.SelectReader(SelectedReader?.ReaderId);
        }
    }

    /// <summary>点击设备列表项：选中并打开设置页（对齐旧项目 OpenDataSourceSettings）。</summary>
    [RelayCommand]
    private async Task OpenReaderSettingsAsync(ReaderItemViewModel item)
    {
        SelectedReader = item;
        await LoadReaderSettingsAsync(item.ReaderId);
    }

    private void OnDataSourceAdded(object? sender, Guid readerId)
    {
        _ = HandleDataSourceAddedAsync(readerId);
    }

    private async Task HandleDataSourceAddedAsync(Guid readerId)
    {
        if (disposed)
        {
            return;
        }

        try
        {
            Refresh();
            SelectedReader = Readers.FirstOrDefault(r => r.ReaderId == readerId);
            await LoadReaderSettingsAsync(readerId);
        }
        catch (OperationCanceledException) when (discoveryCts.IsCancellationRequested)
        {
            // 窗口退出时取消添加完成后的设置加载。
        }
        catch (Exception ex)
        {
            if (!disposed)
            {
                Status = PlatformErrorDisplay.Failure("加载 Reader 配置", ex);
                CurrentPage = Settings;
            }
        }
    }

    private void OnAddDataSourceCancelled(object? sender, EventArgs args)
    {
        AddDataSource.CancelPendingOperations();
        CurrentPage = Inventory;
    }

    private void OnSettingsCancelRequested(object? sender, EventArgs args)
    {
        Settings.CancelPendingOperations();
        CurrentPage = Inventory;
    }

    private async Task SetReaderEnabledFromListAsync(Guid readerId, bool enabled)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await readerManager.SetEnabledAsync(readerId, enabled, lifetimeToken);
            if (!enabled)
            {
                Status = "Reader 已停用。";
                return;
            }

            ReaderActivationResult activation = await readerManager.ActivateAsync(readerId, lifetimeToken);
            if (!activation.Succeeded)
            {
                await readerManager.SetEnabledAsync(readerId, false, lifetimeToken);
                Status = PlatformErrorDisplay.Failure("连接", activation.ErrorCode, activation.Error);
                return;
            }

            Status = "Reader 已连接并同步能力。";
        }
        catch (OperationCanceledException) when (discoveryCts.IsCancellationRequested)
        {
            // 窗口退出时取消 Reader 状态更新。
        }
        catch (Exception ex)
        {
            if (!disposed)
            {
                Status = PlatformErrorDisplay.Failure("Reader 状态更新", ex);
            }
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private async Task LoadReaderSettingsAsync(Guid readerId)
    {
        // 允许新的设置加载取消并替换旧的设置加载；其它主窗口操作仍保持独占。
        if (IsBusy && !Settings.IsBusy)
        {
            return;
        }

        int generation = Interlocked.Increment(ref settingsLoadGeneration);
        CancellationTokenSource settingsLoadCts =
            CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        CancellationTokenSource? previousLoadCts = Interlocked.Exchange(
            ref activeSettingsLoadCts,
            settingsLoadCts);
        CancelAndDispose(previousLoadCts);
        IsBusy = true;
        try
        {
            if (!IsCurrentSettingsLoad(generation))
            {
                return;
            }

            Settings.SetReaderContext(SelectedReader);
            ReaderRuntimeSnapshot snapshot = readerManager.GetSnapshot(readerId);
            if (snapshot.State == ReaderState.Faulted
                || snapshot.IsStale
                || snapshot.CapabilityRevision == 0)
            {
                ReaderActivationResult activation = await readerManager.ActivateAsync(
                    readerId,
                    settingsLoadCts.Token);
                if (!IsCurrentSettingsLoad(generation))
                {
                    return;
                }

                if (!activation.Succeeded)
                {
                    Status = PlatformErrorDisplay.Failure("连接", activation.ErrorCode, activation.Error);
                    // 离线 Reader 也要进入设置页，让 SettingsService 尝试读取最后一次
                    // 保存的语义 Preset；没有缓存时再显示能力未就绪占位页。
                    Refresh();
                    SelectedReader = Readers.FirstOrDefault(r => r.ReaderId == readerId);
                    await Settings.LoadForNavigationAsync(readerId, settingsLoadCts.Token);
                    if (!IsCurrentSettingsLoad(generation))
                    {
                        return;
                    }

                    CurrentPage = Settings;
                    return;
                }
            }

            Refresh();
            SelectedReader = Readers.FirstOrDefault(r => r.ReaderId == readerId);
            bool settingsLoadedFromReader = await Settings.LoadForNavigationAsync(
                readerId,
                settingsLoadCts.Token);
            if (!IsCurrentSettingsLoad(generation))
            {
                return;
            }

            CurrentPage = Settings;
            Status = settingsLoadedFromReader
                ? "已连接并同步 Reader 能力。"
                : "Reader 能力已同步，但设置回读未成功，当前显示只读内容。";
        }
        catch (OperationCanceledException) when (
            discoveryCts.IsCancellationRequested
            || settingsLoadCts.IsCancellationRequested
            || generation != Volatile.Read(ref settingsLoadGeneration))
        {
            // 窗口退出时取消设置加载。
        }
        catch (Exception ex)
        {
            if (!disposed && IsCurrentSettingsLoad(generation))
            {
                Status = PlatformErrorDisplay.Failure("加载 Reader 配置", ex);
                CurrentPage = Settings;
            }
        }
        finally
        {
            if (ReferenceEquals(
                Interlocked.CompareExchange(ref activeSettingsLoadCts, null, settingsLoadCts),
                settingsLoadCts))
            {
                IsBusy = false;
            }

            settingsLoadCts.Dispose();
        }
    }

    private int settingsLoadGeneration;

    private bool IsCurrentSettingsLoad(int generation) =>
        !disposed && generation == Volatile.Read(ref settingsLoadGeneration);

    private void CancelPendingPageOperations(object? page)
    {
        if (page is IPageOperationOwner pageOperations)
        {
            pageOperations.CancelPendingOperations();
        }

        Interlocked.Increment(ref settingsLoadGeneration);
        CancellationTokenSource? settingsLoadCts = Volatile.Read(ref activeSettingsLoadCts);
        try
        {
            settingsLoadCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 侧栏切换与设置加载完成的释放可能并发发生。
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

    private void OnReaderStateChanged(object? sender, ReaderStateChangedEventArgs args)
    {
        if (disposed)
        {
            return;
        }

        void RefreshOnUiThread()
        {
            if (!disposed)
            {
                Refresh();
            }
        }
        if (dispatcher.CheckAccess())
        {
            RefreshOnUiThread();
            return;
        }

        if (disposed || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _ = dispatcher.BeginInvoke(RefreshOnUiThread);
        }
        catch (InvalidOperationException)
        {
            // WPF shutdown may race the Dispatcher state check.
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CancelPendingPageOperations(CurrentPage);
        discoveryCts.Cancel();
        discoveryCts.Dispose();
        Interlocked.Increment(ref settingsLoadGeneration);
        readerManager.StateChanged -= OnReaderStateChanged;
        AddDataSource.DataSourceAdded -= OnDataSourceAdded;
        AddDataSource.CancelRequested -= OnAddDataSourceCancelled;
        Settings.CancelRequested -= OnSettingsCancelRequested;
        AddDataSource.Dispose();
        Settings.Dispose();
        Inventory.Dispose();
        TagMemory.Dispose();
        Diagnostics.Dispose();
        AppSettings.Dispose();
        TagLists.Dispose();
        InventoryRuns.Dispose();
    }
}
