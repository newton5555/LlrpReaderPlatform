using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderPlatform.Contracts.Errors;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Contracts.Tagging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>设备设置 Tab2 的 GPI/GPO 控制：短操作由 IInventoryService 统一串行化并在完成后断开。</summary>
public partial class DiagnosticsViewModel : ObservableObject, IPageOperationOwner, IDisposable
{
    private readonly IInventoryService inventory;
    private readonly ILogger<DiagnosticsViewModel> logger;
    private readonly Dispatcher dispatcher;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly CancellationToken lifetimeToken;
    private readonly object operationScopeSync = new();
    private CancellationTokenSource operationScopeCts;
    private Guid? selectedReaderId;
    private int operationQueueDepth;
    private long readerContextVersion;
    private bool capabilitySnapshotFresh = true;
    private ReaderFeatureCatalog? featureCatalog;
    private ushort? gpiCount;
    // A GPO switch is optimistic while its short-session operation is queued.
    // Keep the last confirmed device value separately so a failed older request
    // cannot roll back a newer user intent to the older operation's oldValue.
    private readonly long[] gpoIntentVersions = new long[5];
    private readonly bool[] gpoConfirmedStates = new bool[5];
    private bool disposed;

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
    public bool IsGpoControlVisible => IsGpo1Available
        || IsGpo2Available
        || IsGpo3Available
        || IsGpo4Available;
    public bool IsGpiStatusAvailable => capabilitySnapshotFresh
        && (featureCatalog?.SupportsOrUnknown(ReaderFeatures.StandardGpi) ?? true)
        && (gpiCount is null || gpiCount > 0);
    public bool IsGpioRefreshAvailable => IsGpiStatusAvailable || IsGpoControlVisible;

    public DiagnosticsViewModel(
        IInventoryService inventory,
        ILogger<DiagnosticsViewModel>? logger = null)
    {
        this.inventory = inventory;
        this.logger = logger ?? NullLogger<DiagnosticsViewModel>.Instance;
        dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        lifetimeToken = lifetimeCts.Token;
        operationScopeCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
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
    public void SelectReader(
        Guid? readerId,
        ReaderFeatureCatalog? featureCatalog,
        ushort? gpoCount,
        bool capabilitiesCurrent = true)
        => SelectReader(
            readerId,
            featureCatalog,
            gpiCount: null,
            gpoCount: gpoCount,
            capabilitiesCurrent: capabilitiesCurrent);

    /// <summary>选择 Reader 并同步能力目录和已知 GPI/GPO 数量。</summary>
    public void SelectReader(
        Guid? readerId,
        ReaderFeatureCatalog? featureCatalog,
        ushort? gpiCount,
        ushort? gpoCount,
        bool capabilitiesCurrent = true)
    {
        bool nextGpoAvailable = capabilitiesCurrent
            && (featureCatalog?.SupportsOrUnknown(ReaderFeatures.StandardGpo) ?? true);
        bool nextGpiStatusAvailable = capabilitiesCurrent
            && (featureCatalog?.SupportsOrUnknown(ReaderFeatures.StandardGpi) ?? true)
            && (gpiCount is null || gpiCount > 0);
        bool gpioContextChanged = selectedReaderId != readerId
            || this.gpiCount != gpiCount
            || this.gpoCount != gpoCount
            || capabilitySnapshotFresh != capabilitiesCurrent
            || IsGpoAvailable != nextGpoAvailable
            || IsGpiStatusAvailable != nextGpiStatusAvailable;

        if (gpioContextChanged)
        {
            CancelPendingOperations();
            Interlocked.Increment(ref readerContextVersion);
        }

        selectedReaderId = readerId;
        this.featureCatalog = featureCatalog;
        this.gpiCount = gpiCount;
        this.gpoCount = gpoCount;
        capabilitySnapshotFresh = capabilitiesCurrent;
        IsGpoAvailable = nextGpoAvailable;
        if (gpioContextChanged)
        {
            ResetGpoTracking();
        }

        OnPropertyChanged(nameof(IsGpo1Available));
        OnPropertyChanged(nameof(IsGpo2Available));
        OnPropertyChanged(nameof(IsGpo3Available));
        OnPropertyChanged(nameof(IsGpo4Available));
        OnPropertyChanged(nameof(IsGpoControlVisible));
        OnPropertyChanged(nameof(IsGpiStatusAvailable));
        OnPropertyChanged(nameof(IsGpioRefreshAvailable));
        if (gpioContextChanged)
        {
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
    }

    [RelayCommand]
    private async Task SetGpoAsync(Guid? id)
    {
        if (disposed)
        {
            return;
        }

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

        CancellationToken operationToken = GetOperationToken();
        if (!await BeginOperationAsync(operationToken))
        {
            return;
        }

        long contextVersion = Volatile.Read(ref readerContextVersion);
        Guid operationId = Guid.NewGuid();
        logger.LogInformation(
            "WPF operation {Operation} started: {OperationId}, reader {ReaderId}, port {PortNumber}, state {State}.",
            "SetGpo",
            operationId,
            readerId,
            PortNumber,
            OutputState);
        try
        {
            if (!IsCurrentReaderContext(readerId, contextVersion))
            {
                return;
            }

            await inventory.SetGpoAsync(readerId, new GpioCommand
            {
                PortNumber = PortNumber,
                State = OutputState,
            }, operationToken);
            if (IsCurrentReaderContext(readerId, contextVersion))
            {
                Status = $"GPO {PortNumber} 已设置为 {(OutputState ? "ON" : "OFF")}。";
                logger.LogInformation(
                    "WPF operation {Operation} completed: {OperationId}, reader {ReaderId}, port {PortNumber}.",
                    "SetGpo",
                    operationId,
                    readerId,
                    PortNumber);
            }
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            // 窗口退出时取消排队或进行中的 GPO 操作。
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WPF operation {Operation} failed: {OperationId}, reader {ReaderId}.", "SetGpo", operationId, readerId);
            if (!disposed && IsCurrentReaderContext(readerId, contextVersion))
            {
                Status = PlatformErrorDisplay.Failure("GPO 操作", ex);
            }
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand]
    private async Task RefreshGpioAsync(Guid? id)
    {
        if (disposed)
        {
            return;
        }

        if (id is not { } readerId)
        {
            Status = "请先从左侧选择 Reader。";
            return;
        }

        CancellationToken operationToken = GetOperationToken();
        if (!await BeginOperationAsync(operationToken))
        {
            return;
        }

        long contextVersion = Volatile.Read(ref readerContextVersion);
        Guid operationId = Guid.NewGuid();
        logger.LogInformation(
            "WPF operation {Operation} started: {OperationId}, reader {ReaderId}.",
            "RefreshGpio",
            operationId,
            readerId);
        try
        {
            if (!IsCurrentReaderContext(readerId, contextVersion))
            {
                return;
            }

            GpioStatusSnapshot gpio = await inventory.GetGpioStatusAsync(readerId, operationToken);
            if (!IsCurrentReaderContext(readerId, contextVersion))
            {
                return;
            }

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
                    SetConfirmedGpoState(value.PortNumber, value.State);
                }
            }
            finally
            {
                suppressGpoUpdate = false;
            }

            if (IsCurrentReaderContext(readerId, contextVersion))
            {
                Status = $"已读取 {Gpis.Count} 个 GPI、{gpio.Gpos.Count} 个 GPO 状态。";
                logger.LogInformation(
                    "WPF operation {Operation} completed: {OperationId}, reader {ReaderId}, gpi {GpiCount}, gpo {GpoCount}.",
                    "RefreshGpio",
                    operationId,
                    readerId,
                    gpio.Gpis.Count,
                    gpio.Gpos.Count);
            }
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            // 窗口退出时取消 GPI/GPO 状态读取。
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WPF operation {Operation} failed: {OperationId}, reader {ReaderId}.", "RefreshGpio", operationId, readerId);
            if (!disposed && IsCurrentReaderContext(readerId, contextVersion))
            {
                Status = PlatformErrorDisplay.Failure("读取 GPI/GPO", ex);
            }
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
        if (disposed || suppressGpoUpdate)
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

        long contextVersion = Volatile.Read(ref readerContextVersion);
        long intentVersion = RegisterGpoIntent(portNumber);
        Guid operationId = Guid.NewGuid();
        logger.LogInformation(
            "WPF operation {Operation} started: {OperationId}, reader {ReaderId}, port {PortNumber}, state {State}.",
            "SetGpoFromSwitch",
            operationId,
            id,
            portNumber,
            newValue);
        CancellationToken operationToken = GetOperationToken();
        if (!await BeginOperationAsync(operationToken))
        {
            if (IsCurrentReaderContext(id, contextVersion)
                && IsCurrentGpoIntent(portNumber, intentVersion))
            {
                RevertGpoToConfirmed(portNumber);
            }

            return;
        }

        try
        {
            if (!IsCurrentReaderContext(id, contextVersion))
            {
                return;
            }

            await inventory.SetGpoAsync(id, new GpioCommand
            {
                PortNumber = portNumber,
                State = newValue,
            }, operationToken);
            if (IsCurrentReaderContext(id, contextVersion))
            {
                SetConfirmedGpoState(portNumber, newValue);
                Status = $"GPO {portNumber} 已设置为 {(newValue ? "ON" : "OFF")}。";
                logger.LogInformation(
                    "WPF operation {Operation} completed: {OperationId}, reader {ReaderId}, port {PortNumber}.",
                    "SetGpoFromSwitch",
                    operationId,
                    id,
                    portNumber);
            }
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            // 窗口退出时取消排队或进行中的 GPO 操作。
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WPF operation {Operation} failed: {OperationId}, reader {ReaderId}, port {PortNumber}.", "SetGpoFromSwitch", operationId, id, portNumber);
            if (!disposed && IsCurrentReaderContext(id, contextVersion))
            {
                if (IsCurrentGpoIntent(portNumber, intentVersion))
                {
                    RevertGpoToConfirmed(portNumber);
                }

                Status = PlatformErrorDisplay.Failure($"GPO {portNumber} 操作", ex);
            }
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task<bool> BeginOperationAsync(CancellationToken operationToken)
    {
        if (disposed)
        {
            return false;
        }

        Interlocked.Increment(ref operationQueueDepth);
        try
        {
            await operationGate.WaitAsync(operationToken);
        }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
        {
            if (Interlocked.Decrement(ref operationQueueDepth) == 0)
            {
                IsBusy = false;
            }

            return false;
        }

        if (disposed)
        {
            operationGate.Release();
            if (Interlocked.Decrement(ref operationQueueDepth) == 0)
            {
                IsBusy = false;
            }

            return false;
        }

        IsBusy = true;
        return true;
    }

    private void EndOperation()
    {
        operationGate.Release();
        if (Interlocked.Decrement(ref operationQueueDepth) == 0)
        {
            IsBusy = false;
        }
    }

    public void CancelPendingOperations()
    {
        CancellationTokenSource operationCts;
        lock (operationScopeSync)
        {
            operationCts = operationScopeCts;
        }

        try
        {
            operationCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 页面切换与设置操作完成的释放可能并发发生。
        }
    }

    private CancellationToken GetOperationToken()
    {
        lock (operationScopeSync)
        {
            if (operationScopeCts.IsCancellationRequested)
            {
                CancellationTokenSource previous = operationScopeCts;
                operationScopeCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
                previous.Dispose();
            }

            return operationScopeCts.Token;
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

    private long RegisterGpoIntent(ushort portNumber) =>
        IsTrackedGpoPort(portNumber)
            ? Interlocked.Increment(ref gpoIntentVersions[portNumber])
            : 0;

    private bool IsCurrentGpoIntent(ushort portNumber, long version) =>
        IsTrackedGpoPort(portNumber)
        && Volatile.Read(ref gpoIntentVersions[portNumber]) == version;

    private bool GetConfirmedGpoState(ushort portNumber) =>
        IsTrackedGpoPort(portNumber) && Volatile.Read(ref gpoConfirmedStates[portNumber]);

    private void SetConfirmedGpoState(ushort portNumber, bool value)
    {
        if (IsTrackedGpoPort(portNumber))
        {
            Volatile.Write(ref gpoConfirmedStates[portNumber], value);
        }
    }

    private void RevertGpoToConfirmed(ushort portNumber) =>
        RevertGpo(portNumber, GetConfirmedGpoState(portNumber));

    private void ResetGpoTracking()
    {
        for (ushort port = 1; port <= 4; port++)
        {
            Volatile.Write(ref gpoIntentVersions[port], 0);
            Volatile.Write(ref gpoConfirmedStates[port], false);
        }
    }

    private static bool IsTrackedGpoPort(ushort portNumber) => portNumber is >= 1 and <= 4;

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
        IsGpoAvailable
        && portNumber > 0
        && (gpoCount is null || portNumber <= gpoCount);

    private string GetGpoUnavailableMessage(ushort portNumber) =>
        portNumber == 0
            ? "GPO 端口必须从 1 开始。"
            : !capabilitySnapshotFresh
            ? "Reader 当前连接故障或能力已过期，请先重新连接。"
            : !IsGpoAvailable
            ? "当前 Reader 未声明标准 GPO 能力。"
            : $"当前 Reader 只有 {gpoCount} 个 GPO，端口 {portNumber} 不可用。";

    private bool IsCurrentReaderContext(Guid id, long version) =>
        !disposed
        && selectedReaderId == id
        && Volatile.Read(ref readerContextVersion) == version;

    private void OnGpiChanged(object? sender, GpiObservedEventArgs args)
    {
        if (disposed || selectedReaderId != args.ReaderId)
        {
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            TryPostToDispatcher(() => OnGpiChanged(sender, args));
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

        string state = args.Status.State ? "High" : "Low";
        string timestamp = args.Status.Timestamp is { } value
            ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff")
            : "时间未知";
        Status = $"GPI {args.Status.PortNumber} 已变为 {state}（Reader 时间：{timestamp}）。";
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
            // Shutdown can race the pre-check. A disposed diagnostics page has no
            // remaining UI state that needs to receive this event.
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
        lock (operationScopeSync)
        {
            operationScopeCts.Dispose();
        }
        lifetimeCts.Dispose();
        inventory.GpiChanged -= OnGpiChanged;
    }
}
