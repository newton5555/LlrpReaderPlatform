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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ILogger<MainViewModel> logger;
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

    [ObservableProperty]
    private string busyMessage = "正在处理...";

    [ObservableProperty]
    private string contentBusyMessage = "正在加载...";

    public MainViewModel(
        IReaderManager readerManager,
        IReaderDiscoveryService discovery,
        ReaderSettingsViewModel settings,
        InventoryViewModel inventory,
        TagMemoryViewModel tagMemory,
        DiagnosticsViewModel diagnostics,
        AboutViewModel about,
        AppSettingsViewModel appSettings,
        TagListsViewModel tagLists,
        InventoryRunsViewModel inventoryRuns,
        AddDataSourceViewModel addDataSource,
        ILogger<MainViewModel>? logger = null)
    {
        this.readerManager = readerManager;
        this.discovery = discovery;
        this.logger = logger ?? NullLogger<MainViewModel>.Instance;
        dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        lifetimeToken = discoveryCts.Token;
        readerManager.StateChanged += OnReaderStateChanged;
        Inventory = inventory;
        TagMemory = tagMemory;
        Diagnostics = diagnostics;
        Settings = settings;
        Settings.CancelRequested += OnSettingsCancelRequested;
        AppSettings = appSettings;
        TagLists = tagLists;
        TagLists.Changed += OnTagListsChanged;
        InventoryRuns = inventoryRuns;

        About = about;
        AddDataSource = addDataSource;
        AddDataSource.DataSourceAdded += OnDataSourceAdded;
        AddDataSource.CancelRequested += OnAddDataSourceCancelled;
        CurrentPage = Inventory;
    }

    public ReaderSettingsViewModel Settings { get; }
    public InventoryViewModel Inventory { get; }
    public TagMemoryViewModel TagMemory { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public AboutViewModel About { get; }
    public AppSettingsViewModel AppSettings { get; }
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

    private void ApplySelectedReaderContext(
        ReaderItemViewModel? value,
        bool updateTagMemorySelection = true)
    {
        Inventory.SetReaderContext(value);
        if (updateTagMemorySelection)
        {
            TagMemory.SelectReaderFromSidebar(value);
        }
        Settings.SetReaderContext(value);
        if (ReferenceEquals(CurrentPage, InventoryRuns)
            && InventoryRuns.ReaderId != value?.ReaderId)
        {
            InventoryRuns.SelectReader(value?.ReaderId, value?.Name);
        }
    }

    /// <summary>当前导航页（ContentControl 路由）。</summary>
    [ObservableProperty]
    private object? currentPage;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isSidebarBusy;

    [ObservableProperty]
    private bool isContentBusy;

    public AddDataSourceViewModel AddDataSource { get; }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (disposed)
        {
            return;
        }

        SetBusy("正在初始化 Reader 平台...");
        Guid operationId = Guid.NewGuid();
        logger.LogInformation("WPF operation {Operation} started: {OperationId}.", "InitializePlatform", operationId);
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetimeToken);
            await readerManager.InitializeAsync(linked.Token);
            await AppSettings.LoadAsync(linked.Token);
            Refresh();
            Status = Readers.Count == 0 ? "平台已就绪，请添加 Reader。" : $"平台已就绪，已加载 {Readers.Count} 个 Reader。";
            logger.LogInformation(
                "WPF operation {Operation} completed: {OperationId}, readers {ReaderCount}.",
                "InitializePlatform",
                operationId,
                Readers.Count);
        }
        catch (OperationCanceledException) when (discoveryCts.IsCancellationRequested)
        {
            // 窗口退出时取消启动恢复。
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WPF operation {Operation} failed: {OperationId}.", "InitializePlatform", operationId);
            Status = PlatformErrorDisplay.Failure("平台初始化", ex);
            throw;
        }
        finally
        {
            EndSidebarBusy();
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

        TagMemory.UpdateAvailableReaders(Readers, SelectedReader?.ReaderId);
        logger.LogTrace("WPF reader list refreshed: {ReaderCount}, selected {ReaderId}.", Readers.Count, SelectedReader?.ReaderId);

        // Apply the final item once, after the ListBox's transient null selection
        // has been suppressed. This keeps settings/inventory/tag-memory context
        // aligned with the rebuilt ReaderItemViewModel instance.
        ApplySelectedReaderContext(SelectedReader, updateTagMemorySelection: false);
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var profile = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(ReaderName) ? Host : ReaderName,
            Host = Host,
            Port = Port,
            IsEnabled = true,
        };
        SetBusy($"正在添加 Reader「{profile.Name}」...");
        Guid operationId = Guid.NewGuid();
        logger.LogInformation(
            "WPF operation {Operation} started: {OperationId}, reader {ReaderId}, host {Host}, port {Port}.",
            "AddReaderFromShell",
            operationId,
            profile.Id,
            profile.Host,
            profile.Port);

        try
        {
            ReaderAddResult result = await readerManager.AddAsync(profile, enableAfterAdding: true, lifetimeToken);
            string actualName = result.Succeeded
                ? readerManager.GetSnapshot(profile.Id).Profile.Name
                : profile.Name;
            Status = result.Succeeded
                ? $"已添加 {actualName} 并同步 {profile.Host}:{profile.Port}"
                : PlatformErrorDisplay.Failure("添加", result.ErrorCode, result.Error);
            logger.LogInformation(
                "WPF operation {Operation} completed: {OperationId}, reader {ReaderId}, succeeded {Succeeded}, error code {ErrorCode}.",
                "AddReaderFromShell",
                operationId,
                profile.Id,
                result.Succeeded,
                result.ErrorCode);
        }
        catch (OperationCanceledException) when (discoveryCts.IsCancellationRequested)
        {
            // 窗口退出时取消添加 Reader。
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WPF operation {Operation} failed: {OperationId}, reader {ReaderId}.", "AddReaderFromShell", operationId, profile.Id);
            if (!disposed)
            {
                Status = PlatformErrorDisplay.Failure("添加", ex);
            }
        }
        finally
        {
            EndSidebarBusy();
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

        string readerName = Readers.FirstOrDefault(reader => reader.ReaderId == readerId)?.Name ?? "Reader";
        SetBusy($"正在删除 Reader「{readerName}」...");
        Guid operationId = Guid.NewGuid();
        logger.LogInformation("WPF operation {Operation} started: {OperationId}, reader {ReaderId}.", "RemoveReader", operationId, readerId);
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
            logger.LogInformation("WPF operation {Operation} completed: {OperationId}, reader {ReaderId}.", "RemoveReader", operationId, readerId);
        }
        catch (OperationCanceledException) when (discoveryCts.IsCancellationRequested)
        {
            // 窗口退出时取消移除 Reader。
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WPF operation {Operation} failed: {OperationId}, reader {ReaderId}.", "RemoveReader", operationId, readerId);
            if (!disposed)
            {
                Status = PlatformErrorDisplay.Failure("移除", ex);
            }
        }
        finally
        {
            EndSidebarBusy();
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

        SetBusy($"正在激活 Reader「{item.Name}」...");
        Guid operationId = Guid.NewGuid();
        logger.LogInformation("WPF operation {Operation} started: {OperationId}, reader {ReaderId}.", "ActivateReader", operationId, item.ReaderId);
        try
        {
            ReaderActivationResult result = await readerManager.ActivateAsync(item.ReaderId, lifetimeToken);
            Status = result.Succeeded
                ? "激活成功"
                : PlatformErrorDisplay.Failure("激活", result.ErrorCode, result.Error);
            logger.LogInformation(
                "WPF operation {Operation} completed: {OperationId}, reader {ReaderId}, succeeded {Succeeded}, error code {ErrorCode}.",
                "ActivateReader",
                operationId,
                item.ReaderId,
                result.Succeeded,
                result.ErrorCode);
        }
        catch (OperationCanceledException) when (discoveryCts.IsCancellationRequested)
        {
            // 窗口退出时取消 Reader 激活。
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WPF operation {Operation} failed: {OperationId}, reader {ReaderId}.", "ActivateReader", operationId, item.ReaderId);
            if (!disposed)
            {
                Status = PlatformErrorDisplay.Failure("激活", ex);
            }
        }
        finally
        {
            EndSidebarBusy();
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

        SetBusy("正在扫描 _llrp._tcp...");
        Guid operationId = Guid.NewGuid();
        logger.LogInformation("WPF operation {Operation} started: {OperationId}.", "DiscoverReadersFromShell", operationId);
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
            logger.LogInformation(
                "WPF operation {Operation} completed: {OperationId}, discovered {Count} readers.",
                "DiscoverReadersFromShell",
                operationId,
                normalized.Count);
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
            logger.LogError(ex, "WPF operation {Operation} failed: {OperationId}.", "DiscoverReadersFromShell", operationId);
            if (!disposed)
            {
                Discovered.Clear();
                Status = PlatformErrorDisplay.Failure("发现", ex);
            }
        }
        finally
        {
            EndSidebarBusy();
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

        logger.LogDebug("WPF navigation requested: {Page}.", page);

        CurrentPage = nextPage;

        if (string.Equals(page, "TagLists", StringComparison.Ordinal))
        {
            _ = TagLists.LoadCommand.ExecuteAsync(null);
        }
        else if (string.Equals(page, "InventoryRuns", StringComparison.Ordinal))
        {
            InventoryRuns.SelectReader(SelectedReader?.ReaderId, SelectedReader?.Name);
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

    private void OnTagListsChanged(object? sender, EventArgs args)
    {
        if (disposed)
        {
            return;
        }

        // Tag List persistence is independent from the Reader lifecycle. Refresh
        // only the existing WPF projection; never stop/restart an active Inventory.
        _ = Inventory.RefreshTagLabelsAsync(lifetimeToken);
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

        string readerName = Readers.FirstOrDefault(reader => reader.ReaderId == readerId)?.Name ?? "Reader";
        SetBusy(enabled
            ? $"正在连接 Reader「{readerName}」..."
            : $"正在停用 Reader「{readerName}」...");
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
            EndSidebarBusy();
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
        string readerName = Readers.FirstOrDefault(reader => reader.ReaderId == readerId)?.Name
            ?? readerManager.Readers.FirstOrDefault(reader => reader.ReaderId == readerId)?.Profile.Name
            ?? "Reader";
        SetContentBusy($"正在加载 Reader「{readerName}」设置...");
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
                SetContentBusy($"正在连接 Reader「{readerName}」并加载设置...");
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
                EndContentBusy();
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

    private void SetBusy(string message)
    {
        BusyMessage = message;
        Status = message;
        IsBusy = true;
        IsSidebarBusy = true;
    }

    private void SetContentBusy(string message)
    {
        ContentBusyMessage = message;
        Status = message;
        IsBusy = true;
        IsContentBusy = true;
    }

    private void EndSidebarBusy()
    {
        IsSidebarBusy = false;
        IsBusy = false;
    }

    private void EndContentBusy()
    {
        IsContentBusy = false;
        IsBusy = false;
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
        TagLists.Changed -= OnTagListsChanged;
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
