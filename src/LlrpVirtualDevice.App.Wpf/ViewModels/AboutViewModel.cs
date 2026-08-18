using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpVirtualDevice.App.Wpf.Services;

namespace LlrpVirtualDevice.App.Wpf.ViewModels;

public sealed partial class AboutViewModel : ObservableObject
{
    private readonly IDialogService? _dialogService;

    public event Action? RequestReturnToDevices;

    public string AppTitle => "LLRP 虚拟设备管理中心";
    public string AppEnglishTitle => "LLRP Virtual Device Center (Studio)";
    public string Version => "v1.5.0";
    public string BuildDate => "2026-08";
    public string EngineVersion => "LLRPCSharp Protocol Engine v1.5.0";
    public string Description => "本软件是专用于 RFID 上位机开发与测试的独立虚拟设备管理平台。基于 EPCglobal LLRP 标准协议与 LLRPCSharp 核心引擎构建，支持多端口 TCP 报文级仿真、协议版本协商（LLRP 1.0.1 / 1.1 / 2.0）、虚拟标签库管理、RF 场景模拟以及全流程实时报文监控与故障注入。";

    public string DotNetVersion => RuntimeInformation.FrameworkDescription;
    public string OsVersion => $"{RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture})";
    public string ProcessPath => Environment.ProcessPath ?? "N/A";
    public string ConfigDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LlrpVirtualDeviceStudio");
    public string ConfigFilePath => Path.Combine(ConfigDirectory, "virtual-devices.json");

    public AboutViewModel()
    {
    }

    public AboutViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
    }

    [RelayCommand]
    private void ReturnToDevices()
    {
        RequestReturnToDevices?.Invoke();
    }

    [RelayCommand]
    private void OpenConfigFolder()
    {
        try
        {
            if (!Directory.Exists(ConfigDirectory))
            {
                Directory.CreateDirectory(ConfigDirectory);
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = ConfigDirectory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _dialogService?.ShowToast("打开目录失败", ex.Message, Models.DialogType.Error);
        }
    }

    [RelayCommand]
    private void CopyDiagnostics()
    {
        try
        {
            string diagText = $"""
                =======================================================
                LLRP 虚拟设备管理中心 (LlrpVirtualDeviceStudio) 诊断信息
                =======================================================
                软件版本: {Version}
                协议引擎: {EngineVersion}
                运行时: {DotNetVersion}
                操作系统: {OsVersion}
                进程路径: {ProcessPath}
                配置目录: {ConfigDirectory}
                配置文件: {ConfigFilePath}
                支持协议: LLRP 1.0.1, LLRP 1.1, LLRP 2.0 (Security & Protocol Extension)
                支持画像: Standard, Impinj Speedway R420, Zebra FX9600
                =======================================================
                """;

            Clipboard.SetText(diagText);
            _dialogService?.ShowToast("已复制到剪贴板", "诊断与系统环境信息已成功复制！", Models.DialogType.Success);
        }
        catch (Exception ex)
        {
            _dialogService?.ShowToast("复制失败", ex.Message, Models.DialogType.Error);
        }
    }
}

