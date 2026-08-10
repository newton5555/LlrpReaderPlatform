using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Contracts.Tagging;
using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>设备设置 Tab2 的 GPI/GPO 控制：短操作由 IInventoryService 统一串行化并在完成后断开。</summary>
public partial class DiagnosticsViewModel : ObservableObject, IDisposable
{
    private readonly IInventoryService inventory;
    private readonly Dispatcher dispatcher;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private Guid? selectedReaderId;
    private int operationQueueDepth;

    [ObservableProperty]
    private ushort portNumber = 1;

    [ObservableProperty]
    private bool outputState;

    [ObservableProperty]
    private bool gpo1;

    [ObservableProperty]
    private bool gpo2;

    [ObservableProperty]
    private bool gpo3;

    [ObservableProperty]
    private bool gpo4;

    [ObservableProperty]
    private string? status;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isGpoAvailable = true;

    private ushort? gpoCount;

    public bool IsGpo1Available => IsGpoAvailable && (gpoCount is null || gpoCount >= 1);
    public bool IsGpo2Available => IsGpoAvailable && (gpoCount is null || gpoCount >= 2);
    public bool IsGpo3Available => IsGpoAvailable && (gpoCount is null || gpoCount >= 3);
    public bool IsGpo4Available => IsGpoAvailable && (gpoCount is null || gpoCount >= 4);

    public DiagnosticsViewModel(IInventoryService inventory)
    {
        this.inventory = inventory;
        dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        inventory.GpiChanged += OnGpiChanged;
    }

    public ObservableCollection<GpiPortStatus> Gpis { get; } = [];

    /// <summary>设备设置页切换 Reader 时同步诊断 Tab 的目标 Reader。</summary>
    public void SelectReader(Guid? readerId)
        => SelectReader(readerId, featureCatalog: null);

    /// <summary>选择 Reader 并同步已知能力；null 能力表示离线/未知，保留可操作回退。</summary>
    public void SelectReader(Guid? readerId, ReaderFeatureCatalog? featureCatalog)
        => SelectReader(readerId, featureCatalog, gpoCount: null);

    /// <summary>选择 Reader 并同步能力目录和已知 GPO 数量。</summary>
    public void SelectReader(Guid? readerId, ReaderFeatureCatalog? featureCatalog, ushort? gpoCount)
    {
        selectedReaderId = readerId;
        this.gpoCount = gpoCount;
        IsGpoAvailable = featureCatalog?.SupportsOrUnknown(ReaderFeatures.StandardGpo) ?? true;
        OnPropertyChanged(nameof(IsGpo1Available));
        OnPropertyChanged(nameof(IsGpo2Available));
        OnPropertyChanged(nameof(IsGpo3Available));
        OnPropertyChanged(nameof(IsGpo4Available));
        suppressGpoUpdate = true;
        try
        {
            Gpo1 = false;
            Gpo2 = false;
            Gpo3 = false;
            Gpo4 = false;
            Gpis.Clear();
        }
        finally
        {
            suppressGpoUpdate = false;
        }
    }

    [RelayCommand]
    private async Task SetGpoAsync(Guid? id)
    {
        if (id is not { } readerId)
        {
            Status = "请先从左侧选择 Reader。";
            return;
        }

        if (!CanUseGpoPort(PortNumber))
        {
            Status = GetGpoUnavailableMessage(PortNumber);
            return;
        }

        await BeginOperationAsync();
        try
        {
            await inventory.SetGpoAsync(readerId, new GpioCommand
            {
                PortNumber = PortNumber,
                State = OutputState,
            }, CancellationToken.None);
            Status = $"GPO {PortNumber} 已设置为 {(OutputState ? "ON" : "OFF")}。";
        }
        catch (Exception ex)
        {
            Status = $"GPO 操作失败: {ex.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand]
    private async Task RefreshGpioAsync(Guid? id)
    {
        if (id is not { } readerId)
        {
            Status = "请先从左侧选择 Reader。";
            return;
        }

        await BeginOperationAsync();
        try
        {
            selectedReaderId = readerId;
            GpioStatusSnapshot gpio = await inventory.GetGpioStatusAsync(readerId, CancellationToken.None);
            Gpis.Clear();
            foreach (GpiPortStatus value in gpio.Gpis)
            {
                Gpis.Add(value);
            }

            suppressGpoUpdate = true;
            try
            {
                Gpo1 = false;
                Gpo2 = false;
                Gpo3 = false;
                Gpo4 = false;
                foreach (GpoPortStatus value in gpio.Gpos)
                {
                    SetGpo(value.PortNumber, value.State);
                }
            }
            finally
            {
                suppressGpoUpdate = false;
            }

            Status = $"已读取 {Gpis.Count} 个 GPI、{gpio.Gpos.Count} 个 GPO 状态。";
        }
        catch (Exception ex)
        {
            Status = $"读取 GPI/GPO 失败：{ex.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    partial void OnGpo1Changed(bool oldValue, bool newValue) => _ = SetGpoFromSwitchAsync(1, oldValue, newValue);
    partial void OnGpo2Changed(bool oldValue, bool newValue) => _ = SetGpoFromSwitchAsync(2, oldValue, newValue);
    partial void OnGpo3Changed(bool oldValue, bool newValue) => _ = SetGpoFromSwitchAsync(3, oldValue, newValue);
    partial void OnGpo4Changed(bool oldValue, bool newValue) => _ = SetGpoFromSwitchAsync(4, oldValue, newValue);

    private async Task SetGpoFromSwitchAsync(ushort portNumber, bool oldValue, bool newValue)
    {
        if (suppressGpoUpdate)
        {
            return;
        }

        if (selectedReaderId is not Guid id)
        {
            RevertGpo(portNumber, oldValue);
            Status = "请先从左侧选择 Reader。";
            return;
        }

        if (!CanUseGpoPort(portNumber))
        {
            RevertGpo(portNumber, oldValue);
            Status = GetGpoUnavailableMessage(portNumber);
            return;
        }

        await BeginOperationAsync();
        try
        {
            await inventory.SetGpoAsync(id, new GpioCommand
            {
                PortNumber = portNumber,
                State = newValue,
            }, CancellationToken.None);
            Status = $"GPO {portNumber} 已设置为 {(newValue ? "ON" : "OFF")}。";
        }
        catch (Exception ex)
        {
            RevertGpo(portNumber, oldValue);
            Status = $"GPO {portNumber} 操作失败: {ex.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task BeginOperationAsync()
    {
        Interlocked.Increment(ref operationQueueDepth);
        await operationGate.WaitAsync(CancellationToken.None);
        IsBusy = true;
    }

    private void EndOperation()
    {
        operationGate.Release();
        if (Interlocked.Decrement(ref operationQueueDepth) == 0)
        {
            IsBusy = false;
        }
    }

    private void RevertGpo(ushort portNumber, bool value)
    {
        suppressGpoUpdate = true;
        try
        {
            SetGpo(portNumber, value);
        }
        finally
        {
            suppressGpoUpdate = false;
        }
    }

    private void SetGpo(ushort portNumber, bool value)
    {
        switch (portNumber)
        {
            case 1:
                Gpo1 = value;
                break;
            case 2:
                Gpo2 = value;
                break;
            case 3:
                Gpo3 = value;
                break;
            case 4:
                Gpo4 = value;
                break;
        }
    }

    private bool suppressGpoUpdate;

    private bool CanUseGpoPort(ushort portNumber) =>
        IsGpoAvailable && (gpoCount is null || portNumber <= gpoCount);

    private string GetGpoUnavailableMessage(ushort portNumber) =>
        !IsGpoAvailable
            ? "当前 Reader 未声明标准 GPO 能力。"
            : $"当前 Reader 只有 {gpoCount} 个 GPO，端口 {portNumber} 不可用。";

    private void OnGpiChanged(object? sender, GpiObservedEventArgs args)
    {
        if (selectedReaderId != args.ReaderId)
        {
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            _ = dispatcher.BeginInvoke(() => OnGpiChanged(sender, args));
            return;
        }

        int index = -1;
        for (int i = 0; i < Gpis.Count; i++)
        {
            if (Gpis[i].PortNumber == args.Status.PortNumber)
            {
                index = i;
                break;
            }
        }

        if (index >= 0)
        {
            Gpis[index] = args.Status;
        }
        else
        {
            Gpis.Add(args.Status);
        }
    }

    public void Dispose() => inventory.GpiChanged -= OnGpiChanged;
}
