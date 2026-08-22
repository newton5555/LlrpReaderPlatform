using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using LlrpNet.Core.Protocol;
using LlrpNet.Protocol.Parameters;
using LlrpNet.Protocol.Parameters.V1_0_1;
using LlrpReaderPlatform.Contracts.Errors;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Tagging;
using LlrpReaderPlatform.Services.Sdk;
using LlrpSdk;
using Tagging = LlrpReaderPlatform.Contracts.Tagging;
using ContractLlrpProtocolVersion = LlrpReaderPlatform.Contracts.Readers.LlrpProtocolVersion;
using ContractTagAccessResult = LlrpReaderPlatform.Contracts.Tagging.TagAccessResult;
using ContractTagMemoryBank = LlrpReaderPlatform.Contracts.Tagging.TagMemoryBank;
using V101Messages = LlrpNet.Protocol.Messages.V1_0_1;

namespace LlrpReaderPlatform.VirtualReader;

public enum VirtualReaderState
{
    PowerOff,
    Disconnected,
    Connecting,
    Connected,
    Ready,
    InventoryRunning,
    Stopping,
    Faulted,
}

/// <summary>
/// 进程内的完整虚拟 Reader。它实现 Services 使用的同一 Session 边界，
/// 不直接给 WPF 推送标签，也不绕过 ReaderManager 的生命周期编排。
/// </summary>
public sealed class VirtualReaderSession : IReaderSession
{
    private readonly VirtualInventoryDataset dataset;
    private readonly VirtualReaderScenario scenario;
    private readonly VirtualReaderDeviceState deviceState;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly object stateGate = new();
    private readonly ReaderSettingsDefaults defaults;
    private CancellationTokenSource? inventoryCancellation;
    private Task? inventoryTask;
    private SemaphoreSlim stepSignal = new(0);
    private bool disposed;
    private bool isConnected;
    private VirtualReaderState state = VirtualReaderState.Disconnected;

    public VirtualReaderSession(VirtualInventoryDataset dataset)
        : this(dataset, new VirtualReaderDeviceState())
    {
    }

    internal VirtualReaderSession(VirtualInventoryDataset dataset, VirtualReaderDeviceState deviceState)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(deviceState);
        this.dataset = dataset;
        scenario = dataset.Scenario;
        this.deviceState = deviceState;
        Identity = CreateIdentity(scenario.Identity);
        Capabilities = CreateCapabilities(scenario.Capabilities);
        lock (deviceState.SyncRoot)
        {
            if (!deviceState.Initialized)
            {
                ReaderSettings initialSettings = CreateInitialSettings(scenario.Capabilities);
                deviceState.SettingsSnapshot = new ReaderSettingsSnapshot(
                    initialSettings,
                    new ManagedRoSpecSnapshot(
                        initialSettings.Inventory ?? new InventorySettings(),
                        InventoryRuntimeState.Disabled));

                foreach (ushort port in Enumerable.Range(1, scenario.Capabilities.GpiCount))
                {
                    deviceState.GpiStates[port] = false;
                }

                foreach (ushort port in Enumerable.Range(1, scenario.Capabilities.GpoCount))
                {
                    deviceState.GpoStates[port] = false;
                }

                InitializeMemories();
                deviceState.Initialized = true;
            }
        }

        defaults = new ReaderSettingsDefaults
        {
            Settings = settingsSnapshot.Settings,
            ProfileId = $"virtual.{scenario.Identity.ManufacturerId:X8}.{scenario.Identity.ModelId:X8}",
            Source = ReaderSettingsDefaultSource.ReaderProfile,
            Notes = ["Settings are provided by the Virtual Reader scenario."],
        };
    }

    private ConcurrentDictionary<string, VirtualTagMemoryState> memories => deviceState.TagMemories;
    private Dictionary<ushort, bool> gpiStates => deviceState.GpiStates;
    private Dictionary<ushort, bool> gpoStates => deviceState.GpoStates;
    private ReaderSettingsSnapshot settingsSnapshot
    {
        get => deviceState.SettingsSnapshot
            ?? throw new InvalidOperationException("Virtual Reader device state is not initialized.");
        set => deviceState.SettingsSnapshot = value;
    }

    public bool IsConnected
    {
        get
        {
            lock (stateGate)
            {
                return isConnected;
            }
        }
    }

    public bool InventoryRunning
    {
        get
        {
            lock (stateGate)
            {
                return state == VirtualReaderState.InventoryRunning;
            }
        }
    }

    public VirtualReaderState State
    {
        get
        {
            lock (stateGate)
            {
                return state;
            }
        }
    }

    public ReaderIdentity? Identity { get; }
    public ReaderCapabilities? Capabilities { get; }
    public ContractLlrpProtocolVersion? NegotiatedVersion => MapProtocolVersion(scenario.ProtocolVersion);
    public ReaderSettingsSnapshot SettingsSnapshot => settingsSnapshot;
    public ReaderSettingsDefaults SettingsDefaults => defaults;
    public InventorySettings? LastStartedInventorySettings { get; private set; }
    public int ReportsEmitted { get; private set; }
    public int ConnectCount { get; private set; }
    public int DisconnectCount { get; private set; }
    public int SettingsQueryCount { get; private set; }
    public int SettingsApplyCount { get; private set; }
    public int StopInventoryCount { get; private set; }

    public event EventHandler<SdkTagReportEventArgs>? TagReported;
    public event EventHandler<ReaderDeviceExceptionEventArgs>? ReaderExceptionOccurred;
    public event EventHandler<ReaderConnectionFaultedEventArgs>? ConnectionFaulted;
    public event EventHandler<EventArgs>? DeviceInitiatedClosed;
    public event EventHandler<SdkGpiChangedEventArgs>? GpiChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (IsConnected)
            {
                return;
            }

            SetState(VirtualReaderState.Connecting);
            await DelayAsync(cancellationToken).ConfigureAwait(false);
            if (scenario.Faults.FailConnect)
            {
                SetState(VirtualReaderState.Faulted);
                throw new InvalidOperationException("Virtual Reader connection was configured to fail.");
            }

            lock (stateGate)
            {
                isConnected = true;
                state = VirtualReaderState.Ready;
            }

            ConnectCount++;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopInventoryCoreAsync(cancellationToken).ConfigureAwait(false);
            lock (stateGate)
            {
                isConnected = false;
                state = VirtualReaderState.Disconnected;
            }

            DisconnectCount++;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<ReaderSettingsSnapshot> QuerySettingsAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            await DelayAsync(cancellationToken).ConfigureAwait(false);
            if (scenario.Faults.FailSettingsQuery)
            {
                throw new InvalidOperationException("Virtual Reader settings query was configured to fail.");
            }

            SettingsQueryCount++;
            return settingsSnapshot;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public Task<ReaderSettingsDefaults> GetDefaultSettingsAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        EnsureConnected();
        return Task.FromResult(defaults);
    }

    public async Task ApplySettingsAsync(ReaderSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            if (InventoryRunning)
            {
                throw new InvalidOperationException("Virtual Reader is busy with inventory.");
            }

            await DelayAsync(cancellationToken).ConfigureAwait(false);
            if (scenario.Faults.FailSettingsApply)
            {
                throw new InvalidOperationException("Virtual Reader settings apply was configured to fail.");
            }

            ValidateSettings(settings);
            SettingsApplyCount++;
            settingsSnapshot = new ReaderSettingsSnapshot(
                settings,
                new ManagedRoSpecSnapshot(
                    settings.Inventory ?? new InventorySettings(),
                    InventoryRuntimeState.Disabled));
        }
        finally
        {
            operationGate.Release();
        }
    }

    public Task StartInventoryAsync(InventorySpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        InventorySettings settings = settingsSnapshot.Settings.Inventory ?? new InventorySettings();
        if (spec.Antennas.Count > 0)
        {
            settings = settings with { AntennaIds = spec.Antennas.ToArray() };
        }

        if (spec.Report?.ReportEveryNTags is ushort reportEvery)
        {
            settings = settings with { ReportEveryNTags = reportEvery };
        }

        return StartInventoryAsync(settings, cancellationToken);
    }

    public async Task StartInventoryAsync(InventorySettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureConnected();
            if (InventoryRunning)
            {
                throw new InvalidOperationException("Virtual Reader inventory is already running.");
            }

            await DelayAsync(cancellationToken).ConfigureAwait(false);
            if (scenario.Faults.FailInventoryStart)
            {
                throw new InvalidOperationException("Virtual Reader inventory start was configured to fail.");
            }

            ValidateInventory(settings);
            LastStartedInventorySettings = settings;
            settingsSnapshot = settingsSnapshot with
            {
                Settings = settingsSnapshot.Settings with { Inventory = settings },
                ManagedRoSpec = new ManagedRoSpecSnapshot(settings, InventoryRuntimeState.Running),
            };
            lock (stateGate)
            {
                state = VirtualReaderState.InventoryRunning;
            }

            inventoryCancellation?.Dispose();
            inventoryCancellation = new CancellationTokenSource();
            stepSignal.Dispose();
            stepSignal = new SemaphoreSlim(0);
            inventoryTask = Task.Run(
                () => ReplayAsync(settings, inventoryCancellation.Token),
                CancellationToken.None);

            if (scenario.Faults.CloseConnectionOnInventoryStart)
            {
                OnDeviceInitiatedClosed();
            }
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task StopInventoryAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopInventoryCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    /// <summary>Step 模式下手动释放一条回放事件。</summary>
    public void AdvanceOneReplayEvent()
    {
        ThrowIfDisposed();
        if (scenario.Replay.Mode != VirtualReplayMode.Step)
        {
            throw new InvalidOperationException("AdvanceOneReplayEvent is only available in Step mode.");
        }

        stepSignal.Release();
    }

    public async Task<ContractTagAccessResult> ReadTagMemoryAsync(TagReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureShortOperationReady();
            if (Capabilities?.IsTagAccessAvailable != true)
            {
                return Failure("Virtual Reader does not support Tag Access.");
            }

            VirtualTagMemoryState? tag = FindTag(request.Epc, request.SelectionBank);
            if (tag is null)
            {
                return Failure("No matching virtual tag was found.");
            }

            if (!tag.AcceptsPassword(request.AccessPasswordHex))
            {
                return Failure("Virtual tag access password was rejected.");
            }

            IReadOnlyList<ushort> words = tag.Read(request.MemoryBank, request.OffsetWords, request.WordCount);
            return new ContractTagAccessResult(true, DataHex: ToHex(words));
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return Failure(exception.Message);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<ContractTagAccessResult> WriteTagMemoryAsync(TagWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureShortOperationReady();
            VirtualTagMemoryState? tag = FindTag(request.Epc, request.SelectionBank);
            if (tag is null)
            {
                return Failure("No matching virtual tag was found.");
            }

            if (!tag.AcceptsPassword(request.AccessPasswordHex))
            {
                return Failure("Virtual tag access password was rejected.");
            }

            IReadOnlyList<ushort> words = SdkTagAccessMapper.ParseWords(request.DataHex);
            string previousEpc = tag.EpcHex;
            tag.Write(request.MemoryBank, request.OffsetWords, words);
            if (request.MemoryBank == ContractTagMemoryBank.Epc
                && !string.Equals(previousEpc, tag.EpcHex, StringComparison.OrdinalIgnoreCase))
            {
                memories.TryRemove(previousEpc, out _);
                memories[tag.EpcHex] = tag;
            }
            return new ContractTagAccessResult(true);
        }
        catch (FormatException exception)
        {
            return Failure(exception.Message);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return Failure(exception.Message);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<ContractTagAccessResult> BlockEraseTagMemoryAsync(TagBlockEraseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureShortOperationReady();
            if (Capabilities?.IsMultiwordBlockEraseAvailable != true)
            {
                return Failure("Virtual Reader does not support block erase.");
            }

            VirtualTagMemoryState? tag = FindTag(request.Epc, request.SelectionBank);
            if (tag is null)
            {
                return Failure("No matching virtual tag was found.");
            }

            if (!tag.AcceptsPassword(request.AccessPasswordHex))
            {
                return Failure("Virtual tag access password was rejected.");
            }

            tag.Erase(request.MemoryBank, request.OffsetWords, request.WordCount);
            return new ContractTagAccessResult(true);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return Failure(exception.Message);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task SetGpoAsync(ushort portNumber, bool state, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureShortOperationReady();
            if (!gpoStates.ContainsKey(portNumber))
            {
                throw new ArgumentOutOfRangeException(nameof(portNumber), "Virtual GPO port does not exist.");
            }

            gpoStates[portNumber] = state;
            ReaderConfiguration configuration = settingsSnapshot.Settings.Configuration with
            {
                Gpos = gpoStates
                    .OrderBy(static item => item.Key)
                    .Select(static item => new GpoConfiguration
                    {
                        GpoPortNumber = item.Key,
                        GpoData = item.Value,
                    })
                    .ToArray(),
            };
            settingsSnapshot = settingsSnapshot with
            {
                Settings = settingsSnapshot.Settings with { Configuration = configuration },
            };
        }
        finally
        {
            operationGate.Release();
        }
    }

    public Task<IReadOnlyList<GpiPortStatus>> GetGpiStatusAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        EnsureConnected();
        return Task.FromResult<IReadOnlyList<GpiPortStatus>>(GetGpiStatus());
    }

    public Task<IReadOnlyList<GpoPortStatus>> GetGpoStatusAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        EnsureConnected();
        return Task.FromResult<IReadOnlyList<GpoPortStatus>>(GetGpoStatus());
    }

    public Task<GpioStatusSnapshot> GetGpioStatusAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        EnsureConnected();
        return Task.FromResult(new GpioStatusSnapshot
        {
            Gpis = GetGpiStatus(),
            Gpos = GetGpoStatus(),
        });
    }

    /// <summary>向虚拟 Reader 注入一个 GPI 变化，供 GPI Stop 和 UI 状态测试使用。</summary>
    public void RaiseGpiChanged(ushort portNumber, bool stateValue, DateTimeOffset? timestamp = null)
    {
        ThrowIfDisposed();
        if (!gpiStates.ContainsKey(portNumber))
        {
            throw new ArgumentOutOfRangeException(nameof(portNumber), "Virtual GPI port does not exist.");
        }

        gpiStates[portNumber] = stateValue;
        GpiChanged?.Invoke(this, new SdkGpiChangedEventArgs(
            portNumber,
            stateValue,
            timestamp ?? DateTimeOffset.UtcNow));
    }

    /// <summary>向虚拟 Reader 注入设备异常。</summary>
    public void RaiseReaderException(string message)
    {
        ThrowIfDisposed();
        ReaderExceptionOccurred?.Invoke(this, new ReaderDeviceExceptionEventArgs(
            message,
            null,
            null,
            DateTimeOffset.UtcNow));
    }

    /// <summary>向虚拟 Reader 注入连接故障并结束当前 Inventory。</summary>
    public void RaiseConnectionFaulted(string message = "Virtual Reader connection faulted.")
    {
        ThrowIfDisposed();
        OnConnectionFaulted(message);
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        inventoryCancellation?.Cancel();
        if (inventoryTask is not null)
        {
            try
            {
                await inventoryTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        inventoryCancellation?.Dispose();
        stepSignal.Dispose();
        operationGate.Dispose();
        lock (stateGate)
        {
            isConnected = false;
            state = VirtualReaderState.Disconnected;
        }
    }

    private async Task ReplayAsync(InventorySettings settings, CancellationToken cancellationToken)
    {
        try
        {
            do
            {
                TimeSpan previousOffset = TimeSpan.Zero;
                foreach (VirtualReplayEvent replayEvent in dataset.Events)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await WaitForReplayEventAsync(replayEvent.Offset - previousOffset, cancellationToken)
                        .ConfigureAwait(false);
                    previousOffset = replayEvent.Offset;

                    if (!MatchesAntenna(settings, replayEvent.Tag.LastAntenna))
                    {
                        continue;
                    }

                    EmitTag(replayEvent.Tag);
                }
            }
            while (scenario.Replay.Loop || scenario.Replay.Mode == VirtualReplayMode.Loop);

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            OnConnectionFaulted($"Virtual inventory replay failed: {exception.Message}");
        }
    }

    private async Task WaitForReplayEventAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (scenario.Replay.Mode == VirtualReplayMode.Step)
        {
            await stepSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (delay <= TimeSpan.Zero)
        {
            await Task.Yield();
            return;
        }

        double speed = scenario.Replay.Mode == VirtualReplayMode.Accelerated
            ? Math.Max(0.001, scenario.Replay.Speed)
            : 1.0;
        TimeSpan scaled = TimeSpan.FromTicks((long)(delay.Ticks / speed));
        await Task.Delay(scaled, cancellationToken).ConfigureAwait(false);
    }

    private void EmitTag(TagObservation tag)
    {
        byte[] epc = Convert.FromHexString(NormalizeHex(tag.Epc));
        ulong firstSeen = ToSdkTimestamp(tag.FirstSeen);
        ulong lastSeen = ToSdkTimestamp(tag.LastSeen);
        ulong timestamp = Math.Max(firstSeen, lastSeen);
        var sdkTimestamp = new TagTimestamp(timestamp, timestamp);
        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [VirtualReaderExtensionModule.VirtualReaderIdField] = scenario.ReaderId.ToString("D"),
        };
        string? tid = tag.Tid;
        VirtualTagMemorySeed? memory = null;
        if (dataset.MemoryByEpc.TryGetValue(tag.Epc, out VirtualTagMemorySeed? configuredMemory))
        {
            memory = configuredMemory;
        }
        else
        {
            string normalizedEpc = NormalizeHex(tag.Epc);
            memory = scenario.TagMemory.FirstOrDefault(seed =>
                string.Equals(NormalizeHex(seed.Epc), normalizedEpc, StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrWhiteSpace(tid) && memory is not null)
        {
            tid = memory.TidHex;
        }

        if (!string.IsNullOrWhiteSpace(tid))
        {
            extensions[VirtualReaderExtensionModule.VirtualTidField] = tid;
        }

        foreach ((string key, string value) in tag.ExtensionFields)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                extensions[key] = value;
            }
        }

        var report = new TagReport(
            ElectronicProductCode: new ReadOnlyMemory<byte>(epc),
            RoSpecId: 1,
            SpecIndex: 0,
            InventoryParameterSpecId: 1,
            AntennaId: tag.LastAntenna,
            PeakRssi: tag.LastRssi,
            ChannelIndex: tag.LastChannelIndex,
            FirstSeen: sdkTimestamp,
            LastSeen: sdkTimestamp,
            SeenCount: (ushort)Math.Clamp(tag.ReadCount, 0, ushort.MaxValue),
            AccessSpecId: 0,
            AccessOperationResults: null,
            Extensions: extensions,
            EpcBitLength: checked((ushort)(epc.Length * 8)),
            PcBits: tag.PcBits);

        ReportsEmitted++;
        foreach (Delegate subscriber in TagReported?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<SdkTagReportEventArgs>)subscriber)(this, new SdkTagReportEventArgs(report));
            }
            catch
            {
                // A slow/failing platform subscriber must not stop the virtual device replay.
            }
        }
    }

    private async Task StopInventoryCoreAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? cancellation = inventoryCancellation;
        Task? task = inventoryTask;
        if (cancellation is null && task is null)
        {
            return;
        }

        SetState(VirtualReaderState.Stopping);
        cancellation?.Cancel();
        ReleaseReplayStep();
        if (task is not null)
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellation?.Dispose();
        inventoryCancellation = null;
        inventoryTask = null;
        StopInventoryCount++;
        settingsSnapshot = settingsSnapshot with
        {
            ManagedRoSpec = new ManagedRoSpecSnapshot(
                settingsSnapshot.Settings.Inventory ?? new InventorySettings(),
                InventoryRuntimeState.Disabled),
        };
        if (IsConnected)
        {
            SetState(VirtualReaderState.Ready);
        }
    }

    private void ValidateSettings(ReaderSettings settings)
    {
        foreach (AntennaConfigurationSettings antenna in settings.Configuration.Antennas)
        {
            ValidateAntenna(antenna.AntennaId);
            ValidateIndex(antenna.TransmitPowerIndex, scenario.Capabilities.TxPowerIndices, "Tx Power");
            ValidateIndex(antenna.ReceiverSensitivityIndex, scenario.Capabilities.RxSensitivityIndices, "Rx Sensitivity");
        }

        if (settings.Inventory is not null)
        {
            ValidateInventory(settings.Inventory);
        }
    }

    private void ValidateInventory(InventorySettings settings)
    {
        if (settings.AntennaIds.Count == 0)
        {
            throw new ArgumentException("Virtual inventory requires at least one antenna.", nameof(settings));
        }

        foreach (ushort antenna in settings.AntennaIds)
        {
            if (antenna == 0 && scenario.Capabilities.RequireExplicitAntennaIds)
            {
                throw new ArgumentException("Virtual Reader requires explicit non-zero antenna IDs.", nameof(settings));
            }

            if (antenna != 0)
            {
                ValidateAntenna(antenna);
            }
        }

        if (settings.ModeIndex != 0 && !scenario.Capabilities.RfModeIndices.Contains(settings.ModeIndex))
        {
            throw new ArgumentException($"RF Mode index {settings.ModeIndex} is not advertised by the Virtual Reader.", nameof(settings));
        }
    }

    private void ValidateAntenna(ushort antennaId)
    {
        if (antennaId == 0 || antennaId > scenario.Capabilities.MaxAntennas)
        {
            throw new ArgumentOutOfRangeException(nameof(antennaId), $"Virtual antenna {antennaId} does not exist.");
        }
    }

    private static void ValidateIndex(ushort? value, IReadOnlyList<ushort> supported, string name)
    {
        if (value is ushort index && !supported.Contains(index))
        {
            throw new ArgumentException($"{name} index {index} is not advertised by the Virtual Reader.", name);
        }
    }

    private IReadOnlyList<GpiPortStatus> GetGpiStatus() =>
        gpiStates
            .OrderBy(static item => item.Key)
            .Select(item => new GpiPortStatus
            {
                PortNumber = item.Key,
                Configured = true,
                State = item.Value,
            })
            .ToArray();

    private IReadOnlyList<GpoPortStatus> GetGpoStatus() =>
        gpoStates
            .OrderBy(static item => item.Key)
            .Select(item => new GpoPortStatus
            {
                PortNumber = item.Key,
                State = item.Value,
            })
            .ToArray();

    private VirtualTagMemoryState? FindTag(string target, ContractTagMemoryBank selectionBank)
    {
        string normalized = NormalizeHex(target);
        if (selectionBank == ContractTagMemoryBank.Epc)
        {
            return memories.TryGetValue(normalized, out VirtualTagMemoryState? tag) ? tag : null;
        }

        return memories.Values.FirstOrDefault(tag =>
            string.Equals(tag.TidHex, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private void InitializeMemories()
    {
        IEnumerable<TagObservation> observations = dataset.SnapshotTags
            .Concat(dataset.Events.Select(static item => item.Tag));
        foreach (TagObservation observation in observations)
        {
            if (string.IsNullOrWhiteSpace(observation.Epc))
            {
                continue;
            }

            VirtualTagMemorySeed seed = dataset.MemoryByEpc.TryGetValue(observation.Epc, out VirtualTagMemorySeed? configured)
                ? configured
                : new VirtualTagMemorySeed
                {
                    Epc = observation.Epc,
                    TidHex = observation.Tid,
                };
            memories.TryAdd(NormalizeHex(seed.Epc), new VirtualTagMemoryState(seed));
        }

        foreach (VirtualTagMemorySeed seed in scenario.TagMemory.Concat(dataset.MemoryByEpc.Values))
        {
            memories[NormalizeHex(seed.Epc)] = new VirtualTagMemoryState(seed);
        }
    }

    private static ReaderSettings CreateInitialSettings(VirtualReaderCapabilities capabilities)
    {
        ushort[] antennas = Enumerable.Range(1, capabilities.MaxAntennas)
            .Select(static value => checked((ushort)value))
            .ToArray();
        return new ReaderSettings
        {
            Configuration = new ReaderConfiguration
            {
                Antennas = antennas.Select(antenna => new AntennaConfigurationSettings
                {
                    AntennaId = antenna,
                    IsConnected = true,
                    TransmitPowerIndex = capabilities.TxPowerIndices.FirstOrDefault(),
                    ReceiverSensitivityIndex = capabilities.RxSensitivityIndices.FirstOrDefault(),
                    HopTableId = 1,
                    ChannelIndex = 1,
                }).ToArray(),
                Gpos = Enumerable.Range(1, capabilities.GpoCount)
                    .Select(value => new GpoConfiguration
                    {
                        GpoPortNumber = checked((ushort)value),
                        GpoData = false,
                    })
                    .ToArray(),
                Gpis = Enumerable.Range(1, capabilities.GpiCount)
                    .Select(value => new GpiStatus
                    {
                        GpiPortNumber = checked((ushort)value),
                        Configured = true,
                        State = GpiState.Low,
                    })
                    .ToArray(),
            },
            Inventory = new InventorySettings { AntennaIds = antennas },
        };
    }

    private static ReaderIdentity CreateIdentity(VirtualReaderIdentity identity) =>
        (ReaderIdentity)Activator.CreateInstance(
            typeof(ReaderIdentity),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [identity.ManufacturerId, identity.ModelId, identity.Firmware],
            culture: null)!;

    private static ReaderCapabilities CreateCapabilities(VirtualReaderCapabilities configuration)
    {
        IEnumerable<ILlrpParameter> generalParameters =
        [
            new GeneralDeviceCapabilities(
                configuration.MaxAntennas,
                false,
                false,
                0,
                0,
                "virtual-reader",
                [],
                [],
                new GPIOCapabilities(configuration.GpiCount, configuration.GpoCount),
                []),
        ];
        var txPowers = configuration.TxPowerIndices
            .Select(index => new TxPowerEntry(index, checked((short)(index * 100))))
            .ToArray();
        var rxSensitivities = configuration.RxSensitivityIndices
            .Select(index => new RxSensitivityEntry(index, checked((short)Math.Max(0, index - 1))))
            .ToArray();
        var rfModes = configuration.RfModeIndices
            .Select(index => new C1G2RfModeEntry(
                index,
                "DRV_64_3",
                true,
                1,
                "PR_ASK",
                "DI",
                64_000,
                2_000,
                0,
                0,
                0))
            .ToArray();

        return (ReaderCapabilities)Activator.CreateInstance(
            typeof(ReaderCapabilities),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                configuration.MaxAntennas,
                false,
                false,
                generalParameters,
                (LlrpNet.Protocol.Messages.ILlrpMessage)RuntimeHelpers.GetUninitializedObject(
                    typeof(V101Messages.GET_READER_CAPABILITIES_RESPONSE)),
                Array.Empty<ILlrpParameter>(),
                txPowers,
                rxSensitivities,
                Array.Empty<uint>(),
                new[] { new FrequencyHopTableEntry(1, [902_750]) },
                rfModes,
                configuration.TagAccessAvailable,
                false,
                configuration.BlockEraseAvailable,
                false,
                false,
                false,
                null,
                null,
            ],
            culture: null)!;
    }

    private static ContractLlrpProtocolVersion MapProtocolVersion(LlrpProtocolVersionOption option) => option switch
    {
        LlrpProtocolVersionOption.Force11 => ContractLlrpProtocolVersion.Version11,
        LlrpProtocolVersionOption.Force20 => ContractLlrpProtocolVersion.Version20,
        _ => ContractLlrpProtocolVersion.Version101,
    };

    private static bool MatchesAntenna(InventorySettings settings, ushort? antenna)
    {
        if (settings.AntennaIds.Contains((ushort)0) || settings.AntennaIds.Count == 0)
        {
            return true;
        }

        return antenna is ushort value && settings.AntennaIds.Contains(value);
    }

    private async Task DelayAsync(CancellationToken cancellationToken)
    {
        if (scenario.Faults.ResponseDelayMilliseconds > 0)
        {
            await Task.Delay(scenario.Faults.ResponseDelayMilliseconds, cancellationToken).ConfigureAwait(false);
        }
    }

    private void EnsureConnected()
    {
        ThrowIfDisposed();
        if (!IsConnected)
        {
            throw new InvalidOperationException("Virtual Reader is not connected.");
        }
    }

    private void EnsureShortOperationReady()
    {
        EnsureConnected();
        if (InventoryRunning)
        {
            throw new InvalidOperationException("Virtual Reader is busy with inventory.");
        }
    }

    private void SetState(VirtualReaderState next)
    {
        lock (stateGate)
        {
            state = next;
        }
    }

    private void ReleaseReplayStep()
    {
        if (scenario.Replay.Mode != VirtualReplayMode.Step)
        {
            return;
        }

        try
        {
            stepSignal.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposal is racing with a stop; the replay task is being cancelled as well.
        }
        catch (SemaphoreFullException)
        {
            // One cancellation signal is enough to wake the step replay.
        }
    }

    private void OnDeviceInitiatedClosed()
    {
        lock (stateGate)
        {
            isConnected = false;
            state = VirtualReaderState.Disconnected;
        }
        inventoryCancellation?.Cancel();
        DeviceInitiatedClosed?.Invoke(this, EventArgs.Empty);
    }

    private void OnConnectionFaulted(string message)
    {
        lock (stateGate)
        {
            isConnected = false;
            state = VirtualReaderState.Faulted;
        }
        inventoryCancellation?.Cancel();
        ConnectionFaulted?.Invoke(this, new ReaderConnectionFaultedEventArgs(message));
    }

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(VirtualReaderSession));
        }
    }

    private static ContractTagAccessResult Failure(string message) => new(false, message)
    {
        ErrorCode = PlatformErrorCode.DeviceFailed,
    };

    private static string NormalizeHex(string value)
    {
        string normalized = new string(value
            .Where(static character => !char.IsWhiteSpace(character) && character is not '-' and not ':')
            .ToArray());
        return normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? normalized[2..]
            : normalized;
    }

    private static ulong ToSdkTimestamp(DateTimeOffset timestamp)
    {
        if (timestamp <= DateTimeOffset.UnixEpoch)
        {
            return 0;
        }

        long microseconds = (timestamp - DateTimeOffset.UnixEpoch).Ticks / 10;
        return microseconds > 0 ? checked((ulong)microseconds) : 0;
    }

    private static string ToHex(IReadOnlyList<ushort> words)
    {
        var bytes = new byte[words.Count * 2];
        for (int index = 0; index < words.Count; index++)
        {
            bytes[index * 2] = (byte)(words[index] >> 8);
            bytes[index * 2 + 1] = (byte)words[index];
        }

        return Convert.ToHexString(bytes);
    }

    internal sealed class VirtualTagMemoryState
    {
        private readonly string accessPassword;
        private byte[] epc;
        private readonly byte[] tid;
        private readonly byte[] reserved;
        private byte[] user;

        public VirtualTagMemoryState(VirtualTagMemorySeed seed)
        {
            EpcHex = Normalize(seed.Epc);
            epc = Convert.FromHexString(EpcHex);
            TidHex = NormalizeOptional(seed.TidHex);
            tid = ParseOptional(seed.TidHex);
            reserved = ParseOptional(seed.ReservedHex, 8);
            user = ParseOptional(seed.UserHex, 8);
            accessPassword = NormalizeOptional(seed.AccessPasswordHex) ?? "00000000";
            UserWritable = seed.UserWritable;
        }

        public string EpcHex { get; private set; }
        public string? TidHex { get; }
        public bool UserWritable { get; }

        public bool AcceptsPassword(string? candidate) =>
            string.Equals(
                NormalizeOptional(candidate) ?? "00000000",
                accessPassword,
                StringComparison.OrdinalIgnoreCase);

        public IReadOnlyList<ushort> Read(ContractTagMemoryBank bank, ushort offset, ushort count)
        {
            byte[] bytes = GetBank(bank);
            int start = checked(offset * 2);
            int length = checked(count * 2);
            if (start < 0 || start + length > bytes.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "Virtual tag memory read exceeds the bank.");
            }

            return BytesToWords(bytes.AsSpan(start, length));
        }

        public void Write(ContractTagMemoryBank bank, ushort offset, IReadOnlyList<ushort> words)
        {
            if (bank is ContractTagMemoryBank.Tid or ContractTagMemoryBank.Reserved)
            {
                throw new ArgumentOutOfRangeException(nameof(bank), "Virtual TID and Reserved banks are read-only.");
            }

            if (bank == ContractTagMemoryBank.User && !UserWritable)
            {
                throw new ArgumentOutOfRangeException(nameof(bank), "Virtual User bank is read-only.");
            }

            byte[] bytes = GetBank(bank);
            int start = checked(offset * 2);
            int length = checked(words.Count * 2);
            if (start < 0 || start + length > bytes.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "Virtual tag memory write exceeds the bank.");
            }

            for (int index = 0; index < words.Count; index++)
            {
                bytes[start + index * 2] = (byte)(words[index] >> 8);
                bytes[start + index * 2 + 1] = (byte)words[index];
            }

            if (bank == ContractTagMemoryBank.Epc)
            {
                epc = bytes[4..];
                EpcHex = Convert.ToHexString(epc);
            }
        }

        public void Erase(ContractTagMemoryBank bank, ushort offset, ushort count)
        {
            if (bank != ContractTagMemoryBank.User)
            {
                throw new ArgumentOutOfRangeException(nameof(bank), "Virtual block erase is available only for User memory.");
            }

            if (!UserWritable)
            {
                throw new ArgumentOutOfRangeException(nameof(bank), "Virtual User bank is read-only.");
            }

            int start = checked(offset * 2);
            int length = checked(count * 2);
            if (start < 0 || start + length > user.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "Virtual tag memory erase exceeds the bank.");
            }

            Array.Clear(user, start, length);
        }

        private byte[] GetBank(ContractTagMemoryBank bank) => bank switch
        {
            ContractTagMemoryBank.Reserved => reserved,
            ContractTagMemoryBank.Epc => [0, 0, 0, 0, .. epc],
            ContractTagMemoryBank.Tid => tid,
            ContractTagMemoryBank.User => user,
            _ => throw new ArgumentOutOfRangeException(nameof(bank)),
        };

        private static IReadOnlyList<ushort> BytesToWords(ReadOnlySpan<byte> bytes)
        {
            var words = new ushort[bytes.Length / 2];
            for (int index = 0; index < words.Length; index++)
            {
                words[index] = (ushort)((bytes[index * 2] << 8) | bytes[index * 2 + 1]);
            }

            return words;
        }

        private static byte[] ParseOptional(string? value, int defaultBytes = 0)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return new byte[defaultBytes];
            }

            string normalized = Normalize(value);
            return Convert.FromHexString(normalized);
        }

        private static string Normalize(string value)
        {
            string normalized = new(value
                .Where(static character => !char.IsWhiteSpace(character) && character is not '-' and not ':')
                .ToArray());
            return normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? normalized[2..]
                : normalized;
        }

        private static string? NormalizeOptional(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : Normalize(value);
    }
}
