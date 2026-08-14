using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Services.Extensions;
using LlrpReaderPlatform.Services.Sdk;
using LlrpSdk;
using System.Runtime.CompilerServices;
using System.Reflection;
using Tagging = LlrpReaderPlatform.Contracts.Tagging;

namespace LlrpReaderPlatform.TestKit;

/// <summary>
/// 可控的标准 LLRP 会话替身：不连接真实网络。测试可配置连接/盘存/标签访问行为，
/// 并可手动触发 TagReported 等事件。
/// </summary>
public sealed class FakeSession : IReaderSession
{
    private bool disposed;

    public bool IsConnected { get; private set; }
    public bool InventoryRunning { get; private set; }
    public ReaderIdentity? Identity { get; set; }
    public ReaderCapabilities? Capabilities { get; set; }
    public LlrpNet.Core.Protocol.LlrpProtocolVersion? NegotiatedVersion { get; set; }

    /// <summary>测试 SDK 的内部构造函数，便于模拟标准 Probe 返回的设备身份。</summary>
    public void SetIdentity(uint manufacturerId, uint modelId, string firmwareVersion) =>
        Identity = (ReaderIdentity)Activator.CreateInstance(
            typeof(ReaderIdentity),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [manufacturerId, modelId, firmwareVersion],
            culture: null)!;

    /// <summary>
    /// 使用 SDK 的内部构造函数设置可控能力，主要用于验证不支持能力时的平台降级行为。
    /// </summary>
    public void SetCapabilities(
        bool isTagAccessAvailable = true,
        ushort maxNumberOfAntennas = 0,
        bool canDoRfSurvey = false,
        ushort? gpiCount = null,
        ushort? gpoCount = null,
        bool useProtocol11 = false,
        bool isMultiwordBlockEraseAvailable = false)
    {
        IEnumerable<LlrpNet.Protocol.Parameters.ILlrpParameter> generalDeviceParameters = [];
        if (gpiCount is not null || gpoCount is not null)
        {
            generalDeviceParameters = useProtocol11
                ?
                [
                    new LlrpNet.Protocol.Parameters.V1_1.GeneralDeviceCapabilities(
                        maxNumberOfAntennas,
                        false,
                        false,
                        0,
                        0,
                        string.Empty,
                        [],
                        [],
                        new LlrpNet.Protocol.Parameters.V1_1.GPIOCapabilities(
                            gpiCount ?? 0,
                            gpoCount ?? 0),
                        [],
                        new LlrpNet.Protocol.Parameters.V1_1.MaximumReceiveSensitivity(0)),
                ]
                :
                [
                    new LlrpNet.Protocol.Parameters.V1_0_1.GeneralDeviceCapabilities(
                        maxNumberOfAntennas,
                        false,
                        false,
                        0,
                        0,
                        string.Empty,
                        [],
                        [],
                        new LlrpNet.Protocol.Parameters.V1_0_1.GPIOCapabilities(
                            gpiCount ?? 0,
                            gpoCount ?? 0),
                        []),
                ];
        }

        Capabilities = (ReaderCapabilities)Activator.CreateInstance(
            typeof(ReaderCapabilities),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                maxNumberOfAntennas,
                false,
                false,
                generalDeviceParameters,
                (LlrpNet.Protocol.Messages.ILlrpMessage)RuntimeHelpers.GetUninitializedObject(
                    typeof(LlrpNet.Protocol.Messages.V1_0_1.GET_READER_CAPABILITIES_RESPONSE)),
                Array.Empty<LlrpNet.Protocol.Parameters.ILlrpParameter>(),
                Array.Empty<TxPowerEntry>(),
                Array.Empty<RxSensitivityEntry>(),
                Array.Empty<uint>(),
                Array.Empty<FrequencyHopTableEntry>(),
                Array.Empty<C1G2RfModeEntry>(),
                isTagAccessAvailable,
                false,
                isMultiwordBlockEraseAvailable,
                false,
                false,
                canDoRfSurvey,
                null,
            ],
            culture: null)!;
    }

    /// <summary>QuerySettingsAsync 返回的可控设置快照。</summary>
    public ReaderSettingsSnapshot SettingsSnapshot { get; set; } =
        new(new ReaderSettings(), new ManagedRoSpecSnapshot(new InventorySettings(), InventoryRuntimeState.Disabled));

    public ReaderSettingsDefaults SettingsDefaults { get; set; } = ReaderSettingsDefaults.CreateGeneric();

    public ReaderSettings? LastAppliedSettings { get; private set; }

    public InventorySettings? LastStartedInventorySettings { get; private set; }

    public Exception? SettingsQueryThrows { get; set; }
    public Func<int, Exception?>? SettingsQueryExceptionFactory { get; set; }
    public Exception? SettingsApplyThrows { get; set; }
    public Action? BeforeQuerySettings { get; set; }
    public Action? BeforeApplySettings { get; set; }
    public int SettingsQueryCount { get; private set; }
    public int SettingsApplyCount { get; private set; }
    public int ConnectCount { get; private set; }
    public int DisconnectCount { get; private set; }
    public int StopInventoryCount { get; private set; }
    public int GpoSetCount { get; private set; }
    public int ReadTagMemoryCount { get; private set; }
    public int WriteTagMemoryCount { get; private set; }
    public int DisposeCount { get; private set; }

    /// <summary>连接时抛出的异常；null 表示连接成功。</summary>
    public Exception? ConnectThrows { get; set; }

    /// <summary>连接前执行的测试回调，可用于在异步操作中触发取消。</summary>
    public Action? BeforeConnect { get; set; }

    /// <summary>StartInventoryAsync 抛出的异常；null 表示成功。</summary>
    public Exception? StartInventoryThrows { get; set; }

    /// <summary>启动盘存前执行的测试回调，可用于在异步操作中触发取消。</summary>
    public Action? BeforeStartInventory { get; set; }

    /// <summary>停止盘存前执行的测试回调，可用于在异步操作中触发取消。</summary>
    public Action? BeforeStopInventory { get; set; }

    /// <summary>测试需要时让连接操作显式尊重传入的取消令牌。</summary>
    public bool HonorCancellation { get; set; }

    /// <summary>设置 GPO 前执行的异步测试回调，可用于覆盖 UI 忙碌和防重入行为。</summary>
    public Func<Task>? BeforeSetGpoAsync { get; set; }

    /// <summary>设置 GPO 时抛出的异常；null 表示成功。</summary>
    public Exception? SetGpoThrows { get; set; }

    /// <summary>StopInventoryAsync 抛出的异常；null 表示成功。</summary>
    public Exception? StopInventoryThrows { get; set; }

    /// <summary>DisconnectAsync 抛出的异常；null 表示成功。</summary>
    public Exception? DisconnectThrows { get; set; }

    /// <summary>DisposeAsync 抛出的异常；用于验证应用退出时仍会清理其他 Reader。</summary>
    public Exception? DisposeThrows { get; set; }

    /// <summary>StartInventoryAsync 成功时立即发出的 EPC，用于覆盖设备早期 TagReport。</summary>
    public byte[]? TagToEmitOnStart { get; set; }

    /// <summary>设为 true 时，ConnectAsync 后模拟设备立即主动断连。</summary>
    public bool DeviceInitiatedCloseOnConnect { get; set; }

    /// <summary>设为 true 时，DisconnectAsync 后模拟传输层发出迟到的设备断开事件。</summary>
    public bool DeviceInitiatedCloseOnDisconnect { get; set; }

    /// <summary>TagAccess 读/写默认返回的结果；null 表示成功。</summary>
    public Tagging.TagAccessResult? TagAccessResult { get; set; }

    public Func<Task>? BeforeReadTagMemoryAsync { get; set; }

    public Func<Task>? BeforeWriteTagMemoryAsync { get; set; }
    public Tagging.TagReadRequest? LastTagReadRequest { get; private set; }
    public Tagging.TagWriteRequest? LastTagWriteRequest { get; private set; }

    public event EventHandler<SdkTagReportEventArgs>? TagReported;
    public event EventHandler<ReaderDeviceExceptionEventArgs>? ReaderExceptionOccurred;
    public event EventHandler<ReaderConnectionFaultedEventArgs>? ConnectionFaulted;
    public event EventHandler<EventArgs>? DeviceInitiatedClosed;
    public event EventHandler<SdkGpiChangedEventArgs>? GpiChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        IfDisposed();
        BeforeConnect?.Invoke();
        if (ConnectThrows is not null)
        {
            throw ConnectThrows;
        }

        IsConnected = true;
        ConnectCount++;
        if (DeviceInitiatedCloseOnConnect)
        {
            await Task.Yield();
            OnDeviceInitiatedClosed();
        }
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        IfDisposed();
        if (HonorCancellation)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (DisconnectThrows is not null)
        {
            throw DisconnectThrows;
        }

        IsConnected = false;
        InventoryRunning = false;
        DisconnectCount++;
        if (DeviceInitiatedCloseOnDisconnect)
        {
            OnDeviceInitiatedClosed();
        }

        return Task.CompletedTask;
    }

    public Task<ReaderSettingsSnapshot> QuerySettingsAsync(CancellationToken cancellationToken)
    {
        IfDisposed();
        BeforeQuerySettings?.Invoke();
        SettingsQueryCount++;
        Exception? queryException = SettingsQueryExceptionFactory?.Invoke(SettingsQueryCount)
            ?? SettingsQueryThrows;
        if (queryException is not null)
        {
            throw queryException;
        }

        return Task.FromResult(SettingsSnapshot);
    }

    public Task<ReaderSettingsDefaults> GetDefaultSettingsAsync(CancellationToken cancellationToken)
    {
        IfDisposed();
        return Task.FromResult(SettingsDefaults);
    }

    public Task ApplySettingsAsync(ReaderSettings settings, CancellationToken cancellationToken)
    {
        IfDisposed();
        BeforeApplySettings?.Invoke();
        SettingsApplyCount++;
        if (SettingsApplyThrows is not null)
        {
            throw SettingsApplyThrows;
        }

        LastAppliedSettings = settings;
        SettingsSnapshot = new ReaderSettingsSnapshot(settings, new ManagedRoSpecSnapshot(
            settings.Inventory ?? new InventorySettings(), InventoryRuntimeState.Disabled));
        return Task.CompletedTask;
    }

    public Task StartInventoryAsync(Tagging.InventorySpec spec, CancellationToken cancellationToken)
    {
        IfDisposed();
        BeforeStartInventory?.Invoke();
        if (StartInventoryThrows is not null)
        {
            throw StartInventoryThrows;
        }

        InventoryRunning = true;
        return Task.CompletedTask;
    }

    public Task StartInventoryAsync(InventorySettings settings, CancellationToken cancellationToken)
    {
        IfDisposed();
        BeforeStartInventory?.Invoke();
        if (StartInventoryThrows is not null)
        {
            throw StartInventoryThrows;
        }

        LastStartedInventorySettings = settings;
        InventoryRunning = true;
        if (TagToEmitOnStart is { Length: > 0 } epc)
        {
            EmitTag(epc);
        }
        return Task.CompletedTask;
    }

    public Task StopInventoryAsync(CancellationToken cancellationToken)
    {
        IfDisposed();
        BeforeStopInventory?.Invoke();
        if (HonorCancellation)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        StopInventoryCount++;
        if (StopInventoryThrows is not null)
        {
            throw StopInventoryThrows;
        }

        InventoryRunning = false;
        return Task.CompletedTask;
    }

    public async Task<Tagging.TagAccessResult> ReadTagMemoryAsync(Tagging.TagReadRequest request, CancellationToken cancellationToken)
    {
        IfDisposed();
        if (BeforeReadTagMemoryAsync is not null)
        {
            await BeforeReadTagMemoryAsync().ConfigureAwait(false);
        }

        ReadTagMemoryCount++;
        LastTagReadRequest = request;
        return TagAccessResult ?? new Tagging.TagAccessResult(true);
    }

    public async Task<Tagging.TagAccessResult> WriteTagMemoryAsync(Tagging.TagWriteRequest request, CancellationToken cancellationToken)
    {
        IfDisposed();
        if (BeforeWriteTagMemoryAsync is not null)
        {
            await BeforeWriteTagMemoryAsync().ConfigureAwait(false);
        }

        WriteTagMemoryCount++;
        LastTagWriteRequest = request;
        return TagAccessResult ?? new Tagging.TagAccessResult(true);
    }

    public Tagging.TagBlockEraseRequest? LastBlockEraseRequest { get; private set; }
    public int BlockEraseTagMemoryCount { get; private set; }

    public async Task<Tagging.TagAccessResult> BlockEraseTagMemoryAsync(Tagging.TagBlockEraseRequest request, CancellationToken cancellationToken)
    {
        IfDisposed();
        BlockEraseTagMemoryCount++;
        LastBlockEraseRequest = request;
        return TagAccessResult ?? new Tagging.TagAccessResult(true);
    }

    public async Task SetGpoAsync(ushort portNumber, bool state, CancellationToken cancellationToken)
    {
        IfDisposed();
        if (BeforeSetGpoAsync is not null)
        {
            await BeforeSetGpoAsync().ConfigureAwait(false);
        }

        if (SetGpoThrows is not null)
        {
            throw SetGpoThrows;
        }

        LastGpoState = (portNumber, state);
        GpoSetCount++;
    }

    public Task<IReadOnlyList<Tagging.GpiPortStatus>> GetGpiStatusAsync(CancellationToken cancellationToken)
    {
        IfDisposed();
        return Task.FromResult<IReadOnlyList<Tagging.GpiPortStatus>>(GetGpioStatus().Gpis);
    }

    public Task<IReadOnlyList<Tagging.GpoPortStatus>> GetGpoStatusAsync(CancellationToken cancellationToken)
    {
        IfDisposed();
        return Task.FromResult<IReadOnlyList<Tagging.GpoPortStatus>>(GetGpioStatus().Gpos);
    }

    public Task<Tagging.GpioStatusSnapshot> GetGpioStatusAsync(CancellationToken cancellationToken)
    {
        IfDisposed();
        return Task.FromResult(GetGpioStatus());
    }

    private Tagging.GpioStatusSnapshot GetGpioStatus()
    {
        SettingsQueryCount++;
        return new Tagging.GpioStatusSnapshot
        {
            Gpis = SettingsSnapshot.Settings.Configuration.Gpis.Select(gpi => new Tagging.GpiPortStatus
            {
                PortNumber = gpi.GpiPortNumber,
                Configured = gpi.Configured,
                State = gpi.State == GpiState.High,
            }).ToArray(),
            Gpos = SettingsSnapshot.Settings.Configuration.Gpos.Select(gpo => new Tagging.GpoPortStatus
            {
                PortNumber = gpo.GpoPortNumber,
                State = gpo.GpoData,
            }).ToArray(),
        };
    }

    /// <summary>最近一次 GPO 设置。</summary>
    public (ushort Port, bool State)? LastGpoState { get; private set; }

    public void RaiseTagReported(TagReport report) =>
        TagReported?.Invoke(this, new SdkTagReportEventArgs(report));

    /// <summary>便携：从标量构造一个 TagReport 并触发 TagReported（封装 SDK 构造细节）。</summary>
    public void EmitTag(
        byte[] epc,
        long seenCount = 1,
        sbyte? rssi = null,
        ushort? antenna = null,
        ushort? pcBits = null,
        ulong? timestampMicros = null)
    {
        long micros = (DateTimeOffset.UtcNow - DateTimeOffset.UnixEpoch).Ticks / 10;
        ulong timestamp = timestampMicros ?? (ulong)micros;
        var ts = new TagTimestamp(UtcMicroseconds: timestamp, UptimeMicroseconds: timestamp);
        var report = new TagReport(
            ElectronicProductCode: new ReadOnlyMemory<byte>(epc),
            RoSpecId: 1,
            SpecIndex: 0,
            InventoryParameterSpecId: 0,
            AntennaId: antenna,
            PeakRssi: rssi,
            ChannelIndex: 0,
            FirstSeen: ts,
            LastSeen: ts,
            SeenCount: (ushort?)(ushort)seenCount,
            AccessSpecId: 0,
            AccessOperationResults: null,
            Extensions: null,
            EpcBitLength: null,
            PcBits: pcBits);
        RaiseTagReported(report);
    }

    public void RaiseReaderException(string message) =>
        ReaderExceptionOccurred?.Invoke(this, new ReaderDeviceExceptionEventArgs(
            message, roSpecId: null, antennaId: null, DateTimeOffset.UtcNow));

    public void RaiseDeviceInitiatedClosed() => OnDeviceInitiatedClosed();

    public void RaiseConnectionFaulted(string message = "Simulated connection fault.") =>
        OnConnectionFaulted(message);

    public void RaiseGpiChanged(ushort portNumber, bool state, DateTimeOffset? timestamp = null) =>
        GpiChanged?.Invoke(this, new SdkGpiChangedEventArgs(portNumber, state, timestamp ?? DateTimeOffset.UtcNow));

    public async ValueTask DisposeAsync()
    {
        DisposeCount++;
        disposed = true;
        IsConnected = false;
        InventoryRunning = false;
        if (DisposeThrows is not null)
        {
            throw DisposeThrows;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void OnDeviceInitiatedClosed()
    {
        IsConnected = false;
        InventoryRunning = false;
        DeviceInitiatedClosed?.Invoke(this, EventArgs.Empty);
    }

    private void OnConnectionFaulted(string message)
    {
        IsConnected = false;
        InventoryRunning = false;
        ConnectionFaulted?.Invoke(this, new ReaderConnectionFaultedEventArgs(message));
    }

    private void IfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(FakeSession));
        }
    }
}

/// <summary>
/// 为 profile 返回 FakeSession 的工厂替身。可通过 <see cref="Factory"/> 自定义，
/// 或向 <see cref="Queue"/> 预置按调用顺序出队的会话序列。
/// </summary>
public sealed class FakeSessionFactory : IReaderSessionFactory
{
    public Func<ReaderProfile, FakeSession> Factory { get; set; } = static _ => new FakeSession();

    /// <summary>每次 Create 记录收到的 profile 与扩展模块（用于两阶段匹配断言）。</summary>
    public List<(ReaderProfile Profile, IReadOnlyList<IReaderExtensionModule> Extensions)> Created { get; } = [];

    /// <summary>按调用顺序出队；非空时优先于 <see cref="Factory"/>。</summary>
    public Queue<FakeSession> Queue { get; } = new();

    public IReaderSession Create(ReaderProfile profile, IReadOnlyList<IReaderExtensionModule>? extensions = null)
    {
        extensions ??= [];
        Created.Add((profile, extensions));
        if (Queue.Count > 0)
        {
            return Queue.Dequeue();
        }

        return Factory(profile);
    }
}
