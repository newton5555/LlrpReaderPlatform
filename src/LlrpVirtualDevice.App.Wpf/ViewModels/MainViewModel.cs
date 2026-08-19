using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpVirtualDevice.App.Wpf.Models;
using LlrpVirtualDevice.App.Wpf.Services;

namespace LlrpVirtualDevice.App.Wpf.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IVirtualDeviceManagerService _managerService;
    private readonly IDialogService _dialogService;

    [ObservableProperty]
    private DeviceItemViewModel? _selectedDevice;

    [ObservableProperty]
    private string _currentView = "Device"; // "Device" or "About"

    [ObservableProperty]
    private string _status = "就绪";

    [ObservableProperty]
    private bool _isAddDialogOpen;

    [ObservableProperty]
    private AddDeviceViewModel _addDialogVm = new();

    public AboutViewModel AboutVm { get; }

    public ObservableCollection<DeviceItemViewModel> DeviceList { get; } = [];

    public IDialogService DialogService => _dialogService;

    public MainViewModel(
        IVirtualDeviceManagerService managerService,
        IDialogService dialogService)
    {
        _managerService = managerService;
        _dialogService = dialogService;
        AboutVm = new AboutViewModel(dialogService);
        AboutVm.RequestReturnToDevices += OnReturnToDevices;
    }

    private void OnReturnToDevices()
    {
        CurrentView = "Device";
    }

    partial void OnSelectedDeviceChanged(DeviceItemViewModel? value)
    {
        if (value != null)
        {
            CurrentView = "Device";
        }
    }

    public async Task InitializeAsync()
    {
        Status = "正在加载虚拟设备配置...";
        await _managerService.LoadConfigsAsync();
        var configs = _managerService.GetAllConfigs();

        DeviceList.Clear();
        foreach (var config in configs)
        {
            var host = _managerService.GetHost(config.Id);
            var itemVm = new DeviceItemViewModel(config, host, _managerService, _dialogService);
            DeviceList.Add(itemVm);
        }

        SelectedDevice = DeviceList.FirstOrDefault();
        CurrentView = "Device";
        Status = $"就绪，已加载 {DeviceList.Count} 个虚拟读写器实例";
    }

    [RelayCommand]
    private void SelectDevice(DeviceItemViewModel? device)
    {
        if (device != null)
        {
            SelectedDevice = device;
            CurrentView = "Device";
        }
    }

    [RelayCommand]
    private void ShowAbout()
    {
        CurrentView = "About";
    }

    [RelayCommand]
    private void OpenAddDialog()
    {
        int nextPort = 5084;
        if (DeviceList.Count > 0)
        {
            nextPort = DeviceList.Max(d => d.Config.Port) + 1;
        }

        AddDialogVm = new AddDeviceViewModel
        {
            Name = $"Virtual-Reader-{DeviceList.Count + 1}",
            Port = nextPort,
        };

        AddDialogVm.RequestClose += OnAddDialogClosed;
        IsAddDialogOpen = true;
    }

    private async void OnAddDialogClosed(VirtualDeviceInstanceConfig? config)
    {
        IsAddDialogOpen = false;
        AddDialogVm.RequestClose -= OnAddDialogClosed;

        if (config != null)
        {
            Status = $"正在创建虚拟读写器 '{config.Name}'...";
            var host = await _managerService.CreateOrUpdateHostAsync(config);
            var itemVm = new DeviceItemViewModel(config, host, _managerService, _dialogService);
            DeviceList.Add(itemVm);
            SelectedDevice = itemVm;
            CurrentView = "Device";
            Status = $"虚拟读写器 '{config.Name}' (Port: {config.Port}) 创建成功";
            await _dialogService.ShowInfoAsync("创建成功", $"已成功创建虚拟读写器 '{config.Name}' (监听端点: {config.ListenAddress}:{config.Port})");
        }
    }

    [RelayCommand]
    private async Task DeleteDeviceAsync(DeviceItemViewModel? device)
    {
        device ??= SelectedDevice;
        if (device == null) return;

        if (device.IsRunning)
        {
            await _dialogService.ShowWarningAsync("无法删除", $"虚拟读写器 '{device.Config.Name}' 正在运行中，请先停止服务后再删除。");
            return;
        }

        var confirmed = await _dialogService.ShowConfirmAsync(
            "确认删除",
            $"确定要删除虚拟读写器 '{device.Config.Name}' (端口: {device.Config.Port}) 吗？删除后配置不可恢复。",
            confirmText: "删除",
            cancelText: "取消",
            isDanger: true);

        if (!confirmed) return;

        Status = $"正在删除虚拟读写器 '{device.Config.Name}'...";
        await _managerService.DeleteHostAsync(device.Config.Id);
        DeviceList.Remove(device);
        if (SelectedDevice == device)
        {
            SelectedDevice = DeviceList.FirstOrDefault();
        }
        Status = $"已删除虚拟读写器 '{device.Config.Name}'";
    }

    [RelayCommand]
    private async Task StartAllAsync()
    {
        Status = "正在启动所有虚拟读写器...";
        await _managerService.StartAllAsync();
        foreach (DeviceItemViewModel device in DeviceList)
        {
            device.RefreshHostBinding();
        }

        Status = "所有虚拟读写器已启动";
        await _dialogService.ShowInfoAsync("批量操作", "已向所有虚拟读写器发送启动指令。");
    }

    [RelayCommand]
    private async Task StopAllAsync()
    {
        Status = "正在停止所有虚拟读写器...";
        await _managerService.StopAllAsync();
        foreach (DeviceItemViewModel device in DeviceList)
        {
            device.RefreshHostBinding();
        }

        Status = "所有虚拟读写器已停止";
        await _dialogService.ShowInfoAsync("批量操作", "已向所有虚拟读写器发送停止指令。");
    }
}
