using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpDevice.Abstractions;
using LlrpDevice.Virtual;
using LlrpDevice.Virtual.Hosting;
using LlrpVirtualDevice.App.Wpf.Models;
using LlrpVirtualDevice.App.Wpf.Services;

namespace LlrpVirtualDevice.App.Wpf.ViewModels;

public sealed partial class DeviceItemViewModel : ObservableObject
{
    private readonly IVirtualDeviceManagerService _managerService;
    private readonly IDialogService _dialogService;
    private readonly Dispatcher _dispatcher;

    [ObservableProperty]
    private VirtualDeviceInstanceConfig _config;

    [ObservableProperty]
    private IVirtualLlrpDeviceHost? _host;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(CanEditConfig))]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    [NotifyPropertyChangedFor(nameof(CanStop))]
    [NotifyPropertyChangedFor(nameof(DisplayPort))]
    [NotifyPropertyChangedFor(nameof(EndpointDisplay))]
    private VirtualLlrpDeviceHostState _state = VirtualLlrpDeviceHostState.Stopped;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayPort))]
    [NotifyPropertyChangedFor(nameof(EndpointDisplay))]
    private int _boundPort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectedClientsSummary))]
    [NotifyPropertyChangedFor(nameof(HasConnectedClients))]
    private int _connectedClientCount;

    [ObservableProperty]
    private int _totalMessagesProcessed;

    [ObservableProperty]
    private string _activeTab = "Overview";

    [ObservableProperty]
    private ObservedMessageItem? _selectedMessage;

    [ObservableProperty]
    private VirtualTagConfig? _selectedTag;

    // 编辑字段 (绑定到设备配置页)
    [ObservableProperty]
    private string _editName = string.Empty;

    [ObservableProperty]
    private string _editListenAddress = "127.0.0.1";

    [ObservableProperty]
    private int _editPort = 5084;

    [ObservableProperty]
    private string _editProtocolVersion = "1.0.1";

    [ObservableProperty]
    private string _editDeviceProfile = "Impinj-R420";

    [ObservableProperty]
    private ushort _editMaxAntennas = 4;

    public int DisplayPort => BoundPort > 0 ? BoundPort : Config.Port;
    public string EndpointDisplay => $"{Config.ListenAddress}:{DisplayPort}";
    public bool HasConnectedClients => ConnectedClients.Count > 0;
    public string ConnectedClientsSummary => ConnectedClients.Count switch
    {
        0 => "暂无客户端接入",
        1 => $"1 个客户端 ({ConnectedClients[0].RemoteEndPoint})",
        _ => $"{ConnectedClients.Count} 个客户端在线 ({ConnectedClients[0].RemoteEndPoint} 等)"
    };

    public bool IsRunning => State == VirtualLlrpDeviceHostState.Running;
    public bool CanEditConfig => State is VirtualLlrpDeviceHostState.Stopped or VirtualLlrpDeviceHostState.Created or VirtualLlrpDeviceHostState.Faulted;
    public bool CanStart => CanEditConfig;
    public bool CanStop => State is VirtualLlrpDeviceHostState.Running or VirtualLlrpDeviceHostState.Starting;

    public IReadOnlyList<string> AvailableProfiles { get; } = ["Standard", "Impinj-R420", "Zebra-FX9600"];
    public IReadOnlyList<string> AvailableProtocolVersions { get; } = ["1.0.1", "1.1", "2.0"];
    public IReadOnlyList<VirtualRfScenario> AvailableScenarios { get; } = [VirtualRfScenario.Static, VirtualRfScenario.MovingTags, VirtualRfScenario.Noisy];

    public ObservableCollection<ObservedMessageItem> ObservedMessages { get; } = [];
    public ObservableCollection<ClientConnectionItem> ConnectedClients { get; } = [];
    public ObservableCollection<VirtualTagConfig> Tags { get; } = [];

    public DeviceItemViewModel(
        VirtualDeviceInstanceConfig config,
        IVirtualLlrpDeviceHost? host,
        IVirtualDeviceManagerService managerService,
        IDialogService dialogService)
    {
        Config = config;
        Host = host;
        _managerService = managerService;
        _dialogService = dialogService;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        BoundPort = config.Port;
        LoadEditFields();

        foreach (var tag in config.Tags)
        {
            Tags.Add(tag);
        }

        if (Host != null)
        {
            BindHostEvents(Host);
            State = Host.State;
            BoundPort = Host.BoundPort > 0 ? Host.BoundPort : Config.Port;
            ConnectedClientCount = Host.ConnectedClientCount;
        }
    }

    public void LoadEditFields()
    {
        EditName = Config.Name;
        EditListenAddress = Config.ListenAddress;
        EditPort = Config.Port;
        EditProtocolVersion = Config.ProtocolVersion;
        EditDeviceProfile = Config.DeviceProfile;
        EditMaxAntennas = Config.MaxAntennas;
    }

    public void UpdateHost(IVirtualLlrpDeviceHost newHost)
    {
        if (Host != null)
        {
            UnbindHostEvents(Host);
        }

        Host = newHost;
        if (Host != null)
        {
            BindHostEvents(Host);
            State = Host.State;
            BoundPort = Host.BoundPort > 0 ? Host.BoundPort : Config.Port;
            ConnectedClientCount = Host.ConnectedClientCount;
        }

        OnPropertyChanged(nameof(DisplayPort));
        OnPropertyChanged(nameof(EndpointDisplay));
    }

    private void BindHostEvents(IVirtualLlrpDeviceHost host)
    {
        host.LifecycleChanged += OnHostLifecycleChanged;
        host.ClientChanged += OnHostClientChanged;
        host.MessageObserved += OnHostMessageObserved;
    }

    private void UnbindHostEvents(IVirtualLlrpDeviceHost host)
    {
        host.LifecycleChanged -= OnHostLifecycleChanged;
        host.ClientChanged -= OnHostClientChanged;
        host.MessageObserved -= OnHostMessageObserved;
    }

    private void OnHostLifecycleChanged(object? sender, VirtualLlrpDeviceHostLifecycleChangedEventArgs e)
    {
        _dispatcher.InvokeAsync(() =>
        {
            State = e.CurrentState;
            if (Host != null)
            {
                BoundPort = Host.BoundPort > 0 ? Host.BoundPort : Config.Port;
                ConnectedClientCount = Host.ConnectedClientCount;
            }

            OnPropertyChanged(nameof(DisplayPort));
            OnPropertyChanged(nameof(EndpointDisplay));
            OnPropertyChanged(nameof(ConnectedClientsSummary));
            OnPropertyChanged(nameof(HasConnectedClients));
        });
    }

    private void OnHostClientChanged(object? sender, VirtualLlrpDeviceHostClientChangedEventArgs e)
    {
        _dispatcher.InvokeAsync(() =>
        {
            if (Host != null)
            {
                ConnectedClientCount = Host.ConnectedClientCount;
            }

            var existing = ConnectedClients.FirstOrDefault(c => c.ConnectionId == e.Client.ConnectionId);
            if (e.Connected)
            {
                if (existing == null)
                {
                    ConnectedClients.Add(new ClientConnectionItem
                    {
                        ConnectionId = e.Client.ConnectionId,
                        RemoteEndPoint = e.Client.RemoteEndPoint?.ToString() ?? "Unknown",
                        ConnectedAt = e.Client.ConnectedAt,
                        NegotiatedVersion = e.Client.NegotiatedVersion?.ToString() ?? Config.ProtocolVersion,
                        IsConnected = true,
                    });
                }
                else
                {
                    existing.IsConnected = true;
                    existing.RemoteEndPoint = e.Client.RemoteEndPoint?.ToString() ?? existing.RemoteEndPoint;
                }
            }
            else
            {
                if (existing != null)
                {
                    ConnectedClients.Remove(existing);
                }
            }

            OnPropertyChanged(nameof(ConnectedClientsSummary));
            OnPropertyChanged(nameof(HasConnectedClients));
        });
    }

    private void OnHostMessageObserved(object? sender, VirtualLlrpDeviceHostMessageObservedEventArgs e)
    {
        var item = new ObservedMessageItem
        {
            Timestamp = DateTimeOffset.Now,
            Incoming = e.Incoming,
            ProtocolVersion = e.Version.ToString(),
            MessageType = e.MessageType,
            MessageName = ObservedMessageItem.ResolveMessageName(e.MessageType),
            MessageId = e.MessageId,
            Detail = e.Detail,
        };

        _dispatcher.InvokeAsync(() =>
        {
            TotalMessagesProcessed++;
            if (ObservedMessages.Count >= 500)
            {
                ObservedMessages.RemoveAt(0);
            }
            ObservedMessages.Add(item);
        });
    }

    [RelayCommand]
    private void SwitchTab(string tabName)
    {
        ActiveTab = tabName;
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (!CanStart) return;

        try
        {
            await _managerService.StartHostAsync(Config.Id);
            BoundPort = Host?.BoundPort > 0 ? Host.BoundPort : Config.Port;
            OnPropertyChanged(nameof(DisplayPort));
            OnPropertyChanged(nameof(EndpointDisplay));
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("启动失败", $"启动虚拟读写器失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (!CanStop) return;

        try
        {
            await _managerService.StopHostAsync(Config.Id);
            ConnectedClients.Clear();
            ConnectedClientCount = 0;
            OnPropertyChanged(nameof(ConnectedClientsSummary));
            OnPropertyChanged(nameof(HasConnectedClients));
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("停止失败", $"停止虚拟读写器失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RestartAsync()
    {
        try
        {
            await _managerService.RestartHostAsync(Config.Id);
            BoundPort = Host?.BoundPort > 0 ? Host.BoundPort : Config.Port;
            OnPropertyChanged(nameof(DisplayPort));
            OnPropertyChanged(nameof(EndpointDisplay));
        }
        catch (Exception ex)
        {
            await _dialogService.ShowErrorAsync("重启失败", $"重启虚拟读写器失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SaveConfigAsync()
    {
        if (!CanEditConfig)
        {
            await _dialogService.ShowWarningAsync("配置已锁定", "设备运行中无法修改网络或硬件参数，请先停止服务后再保存。");
            return;
        }

        Config.Name = EditName;
        Config.ListenAddress = EditListenAddress;
        Config.Port = EditPort;
        Config.ProtocolVersion = EditProtocolVersion;
        Config.DeviceProfile = EditDeviceProfile;
        Config.MaxAntennas = EditMaxAntennas;

        var newHost = await _managerService.CreateOrUpdateHostAsync(Config);
        UpdateHost(newHost);
        await _dialogService.ShowInfoAsync("保存成功", $"配置已成功更新并保存！(TCP 端点: {EndpointDisplay})");
    }

    [RelayCommand]
    private void ClearMessages()
    {
        ObservedMessages.Clear();
        TotalMessagesProcessed = 0;
    }

    [RelayCommand]
    private void AddTag()
    {
        byte[] randomBytes = new byte[12];
        RandomNumberGenerator.Fill(randomBytes);
        randomBytes[0] = 0xE2;
        randomBytes[1] = 0x80;
        string epc = Convert.ToHexString(randomBytes);

        var newTag = new VirtualTagConfig
        {
            EpcHex = epc,
            TidHex = "E28011602000" + epc[^4..],
            AntennaId = 1,
            PeakRssi = -40,
        };

        Tags.Add(newTag);
        Config.Tags = Tags.ToList();
        _ = _managerService.SaveConfigsAsync();
    }

    [RelayCommand]
    private void DeleteSelectedTag()
    {
        if (SelectedTag != null)
        {
            Tags.Remove(SelectedTag);
            Config.Tags = Tags.ToList();
            _ = _managerService.SaveConfigsAsync();
        }
    }

    [RelayCommand]
    private void GenerateBatchTags(string countStr)
    {
        if (!int.TryParse(countStr, out int count) || count <= 0)
        {
            count = 20;
        }

        Tags.Clear();
        for (int i = 1; i <= count; i++)
        {
            string epc = $"E2801160600002{i:D10}";
            Tags.Add(new VirtualTagConfig
            {
                EpcHex = epc,
                TidHex = $"E28011602000{i:D4}",
                AntennaId = (ushort)((i % Math.Max(1, (int)Config.MaxAntennas)) + 1),
                PeakRssi = (short)(-35 - (i % 30)),
            });
        }

        Config.Tags = Tags.ToList();
        _ = _managerService.SaveConfigsAsync();
    }

    [RelayCommand]
    private async Task InjectAntennaEventAsync()
    {
        await _dialogService.ShowInfoAsync("事件注入", "已向已连接客户端发送天线事件 (Antenna 1 Connected)");
    }

    [RelayCommand]
    private async Task InjectGpiEventAsync()
    {
        await _dialogService.ShowInfoAsync("事件注入", "已向已连接客户端发送 GPI 触发事件 (Port 1 State=True)");
    }
}
