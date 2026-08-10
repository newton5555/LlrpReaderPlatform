using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Discovery;
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
        IAppSettingsStore? appSettingsStore = null,
        ITagListStore? tagListStore = null,
        IInventoryRunStore? inventoryRunStore = null)
    {
        this.readerManager = readerManager;
        this.discovery = discovery;
        dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        readerManager.StateChanged += OnReaderStateChanged;
        ITagListStore resolvedTagListStore = tagListStore ?? new LlrpReaderPlatform.Services.Persistence.InMemoryTagListStore();
        Inventory = new InventoryViewModel(inventory, resolvedTagListStore, readerManager);
        TagMemory = new TagMemoryViewModel(inventory);
        Diagnostics = new DiagnosticsViewModel(inventory);
        Settings = new ReaderSettingsViewModel(settings, Diagnostics);
        Settings.CancelRequested += OnSettingsCancelRequested;
        AppSettings = new AppSettingsViewModel(appSettingsStore);
        TagLists = new TagListsViewModel(resolvedTagListStore);
        InventoryRuns = new InventoryRunsViewModel(inventoryRunStore ?? new LlrpReaderPlatform.Services.Persistence.InMemoryInventoryRunStore());

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

    partial void OnSelectedReaderChanged(ReaderItemViewModel? value)
    {
        TagMemory.SetReaderContext(value);
        Settings.SetReaderContext(value);
    }

    /// <summary>当前导航页（ContentControl 路由）。</summary>
    [ObservableProperty]
    private object? currentPage;

    [ObservableProperty]
    private bool isBusy;

    public AddDataSourceViewModel AddDataSource { get; }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        Status = "正在初始化 Reader 平台...";
        try
        {
            await readerManager.InitializeAsync(ct);
            await AppSettings.LoadAsync(ct);
            Refresh();
            Status = Readers.Count == 0 ? "平台已就绪，请添加 Reader。" : $"平台已就绪，已加载 {Readers.Count} 个 Reader。";
        }
        catch (Exception ex)
        {
            Status = $"平台初始化失败: {ex.Message}";
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
        Guid? selectedReaderId = SelectedReader?.ReaderId;
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
            ReaderAddResult result = await readerManager.AddAsync(profile, enableAfterAdding: true, CancellationToken.None);
            Status = result.Succeeded
                ? $"已添加并同步 {profile.Host}:{profile.Port}"
                : $"添加失败: {result.Error}";
        }
        catch (Exception ex)
        {
            Status = $"添加失败: {ex.Message}";
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
            await readerManager.RemoveAsync(readerId, CancellationToken.None);
            if (removedSettingsReader)
            {
                Settings.SetReaderContext(null);
                CurrentPage = Inventory;
            }

            Status = "已移除";
        }
        catch (Exception ex)
        {
            Status = $"移除失败: {ex.Message}";
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
            ReaderActivationResult result = await readerManager.ActivateAsync(item.ReaderId, CancellationToken.None);
            Status = result.Succeeded ? "激活成功" : $"激活失败: {result.Error}";
        }
        catch (Exception ex)
        {
            Status = $"激活失败: {ex.Message}";
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
            IReadOnlyList<DiscoveredReader> found = await discovery.DiscoverAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
            Discovered.Clear();
            foreach (DiscoveredReader r in found)
            {
                Discovered.Add(new DiscoveredReaderViewModel(r));
            }

            Status = found.Count == 0 ? "未发现 LLRP 设备" : $"发现 {found.Count} 个设备，可选用后再添加";
        }
        catch (Exception ex)
        {
            Discovered.Clear();
            Status = $"发现失败: {ex.Message}";
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
        CurrentPage = page switch
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

    private async void OnDataSourceAdded(object? sender, Guid readerId)
    {
        Refresh();
        SelectedReader = Readers.FirstOrDefault(r => r.ReaderId == readerId);
        await LoadReaderSettingsAsync(readerId);
    }

    private void OnAddDataSourceCancelled(object? sender, EventArgs args) => CurrentPage = Inventory;

    private void OnSettingsCancelRequested(object? sender, EventArgs args) => CurrentPage = Inventory;

    private async Task SetReaderEnabledFromListAsync(Guid readerId, bool enabled)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await readerManager.SetEnabledAsync(readerId, enabled, CancellationToken.None);
            if (!enabled)
            {
                await readerManager.DeactivateAsync(readerId, CancellationToken.None);
                Status = "Reader 已停用。";
                return;
            }

            ReaderActivationResult activation = await readerManager.ActivateAsync(readerId, CancellationToken.None);
            if (!activation.Succeeded)
            {
                await readerManager.SetEnabledAsync(readerId, false, CancellationToken.None);
                Status = $"连接失败: {activation.Error}";
                return;
            }

            Status = "Reader 已连接并同步能力。";
        }
        catch (Exception ex)
        {
            Status = $"Reader 状态更新失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    private async Task LoadReaderSettingsAsync(Guid readerId)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            Settings.SetReaderContext(SelectedReader);
            ReaderRuntimeSnapshot snapshot = readerManager.GetSnapshot(readerId);
            if (snapshot.State == ReaderState.Faulted
                || snapshot.IsStale
                || snapshot.CapabilityRevision == 0)
            {
                ReaderActivationResult activation = await readerManager.ActivateAsync(readerId, CancellationToken.None);
                if (!activation.Succeeded)
                {
                    Status = $"连接失败: {activation.Error}";
                    // 离线 Reader 也要进入设置页，让 SettingsService 尝试读取最后一次
                    // 保存的语义 Preset；没有缓存时再显示能力未就绪占位页。
                    Refresh();
                    SelectedReader = Readers.FirstOrDefault(r => r.ReaderId == readerId);
                    await Settings.LoadCommand.ExecuteAsync(readerId);
                    CurrentPage = Settings;
                    return;
                }
            }

            Refresh();
            SelectedReader = Readers.FirstOrDefault(r => r.ReaderId == readerId);
            await Settings.LoadCommand.ExecuteAsync(readerId);
            CurrentPage = Settings;
            Status = "已连接并同步 Reader 能力。";
        }
        catch (Exception ex)
        {
            Status = $"加载 Reader 配置失败: {ex.Message}";
            CurrentPage = Settings;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnReaderStateChanged(object? sender, ReaderStateChangedEventArgs args)
    {
        void RefreshOnUiThread() => Refresh();
        if (dispatcher.CheckAccess())
        {
            RefreshOnUiThread();
            return;
        }

        _ = dispatcher.BeginInvoke(RefreshOnUiThread);
    }

    public void Dispose()
    {
        readerManager.StateChanged -= OnReaderStateChanged;
        AddDataSource.DataSourceAdded -= OnDataSourceAdded;
        AddDataSource.CancelRequested -= OnAddDataSourceCancelled;
        Settings.CancelRequested -= OnSettingsCancelRequested;
        Inventory.Dispose();
        Diagnostics.Dispose();
    }
}
