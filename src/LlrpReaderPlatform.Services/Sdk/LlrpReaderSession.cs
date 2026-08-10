using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Services.Extensions;
using Tagging = LlrpReaderPlatform.Contracts.Tagging;
using LlrpSdk;
using LlrpNet.Core.Protocol;
using Microsoft.Extensions.Logging;
using SdkLlrpProtocolVersion = LlrpNet.Core.Protocol.LlrpProtocolVersion;

namespace LlrpReaderPlatform.Services.Sdk;

/// <summary>
/// 标准的 LLRP Reader 会话（无厂商扩展）。负责连接、身份/能力读取与事件透传。
/// Impinj 等厂商能力不由本类承担，而由扩展模块在二次连接阶段叠加（F5）。
/// </summary>
internal sealed class LlrpReaderSession : IReaderSession
{
    private readonly LlrpReader reader;
    private InventorySession? inventorySession;

    public LlrpReaderSession(LlrpReader reader)
    {
        this.reader = reader;
        reader.TagsReported += OnTagsReported;
        reader.ReaderExceptionOccurred += OnReaderExceptionOccurred;
        reader.ConnectionChanged += OnConnectionChanged;
        reader.GpiChanged += OnGpiChanged;
    }

    public bool IsConnected => reader.IsConnected;
    public ReaderIdentity? Identity => reader.Identity;
    public ReaderCapabilities? Capabilities => reader.Capabilities;
    public SdkLlrpProtocolVersion? NegotiatedVersion => reader.NegotiatedVersion;

    public event EventHandler<SdkTagReportEventArgs>? TagReported;
    public event EventHandler<ReaderDeviceExceptionEventArgs>? ReaderExceptionOccurred;
    public event EventHandler<ReaderConnectionFaultedEventArgs>? ConnectionFaulted;
    public event EventHandler<EventArgs>? DeviceInitiatedClosed;
    public event EventHandler<SdkGpiChangedEventArgs>? GpiChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await reader.ConnectAsync(cancellationToken).ConfigureAwait(false);
        // 若因之前的原始协议访问导致 SDK 托管状态未知，先重新同步，避免后续托管调用抛
        // "SDK-managed reader state is unknown after raw protocol access"。
        if (!reader.IsManagedStateSynchronized)
        {
            await reader.SynchronizeStateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await reader.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            inventorySession = null;
        }
    }

    public async Task<ReaderSettingsSnapshot> QuerySettingsAsync(CancellationToken cancellationToken)
    {
        await SynchronizeIfNeededAsync(cancellationToken).ConfigureAwait(false);
        return await reader.QuerySettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ReaderSettingsDefaults> GetDefaultSettingsAsync(CancellationToken cancellationToken)
    {
        await SynchronizeIfNeededAsync(cancellationToken).ConfigureAwait(false);
        return await reader.GetDefaultSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplySettingsAsync(ReaderSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await SynchronizeIfNeededAsync(cancellationToken).ConfigureAwait(false);
        SettingsValidationResult validation = await reader
            .ValidateSettingsAsync(settings, cancellationToken)
            .ConfigureAwait(false);
        validation.ThrowIfInvalid();
        await reader.ApplySettingsAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartInventoryAsync(Tagging.InventorySpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (inventorySession is not null)
        {
            throw new InvalidOperationException("Inventory is already running for this reader.");
        }

        await SynchronizeIfNeededAsync(cancellationToken).ConfigureAwait(false);
        ReaderSettingsSnapshot current = await reader.QuerySettingsAsync(cancellationToken).ConfigureAwait(false);
        InventorySettings settings = current.ManagedRoSpec?.Inventory
            ?? current.Settings.Inventory
            ?? new InventorySettings();
        settings = ApplyInventorySpec(settings, spec);
        inventorySession = await reader.StartInventoryAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartInventoryAsync(InventorySettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (inventorySession is not null)
        {
            throw new InvalidOperationException("Inventory is already running for this reader.");
        }

        await SynchronizeIfNeededAsync(cancellationToken).ConfigureAwait(false);
        inventorySession = await reader.StartInventoryAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopInventoryAsync(CancellationToken cancellationToken)
    {
        InventorySession? session = inventorySession;
        inventorySession = null;
        if (session is null)
        {
            await SynchronizeIfNeededAsync(cancellationToken).ConfigureAwait(false);
            await reader.StopAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await session.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Tagging.TagAccessResult> ReadTagMemoryAsync(Tagging.TagReadRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await SynchronizeIfNeededAsync(cancellationToken).ConfigureAwait(false);
        await EnsureTagAccessConfigurationAsync(cancellationToken).ConfigureAwait(false);

        var sdkRequest = new LlrpSdk.ReadTagRequest
        {
            MemoryBank = (LlrpSdk.TagMemoryBank)request.MemoryBank,
            WordPointer = request.OffsetWords,
            WordCount = request.WordCount,
            Selection = SdkTagAccessMapper.BuildSelection(request.Epc, request.SelectionBank),
            AntennaId = request.AntennaId ?? 0,
            AccessPassword = SdkTagAccessMapper.ParseAccessPassword(request.AccessPasswordHex),
        };

        LlrpSdk.TagAccessResult result = await reader.ReadTagMemoryAsync(
            sdkRequest, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        return SdkTagAccessMapper.MapOperationResult(result.Operation);
    }

    public async Task<Tagging.TagAccessResult> WriteTagMemoryAsync(Tagging.TagWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await SynchronizeIfNeededAsync(cancellationToken).ConfigureAwait(false);
        await EnsureTagAccessConfigurationAsync(cancellationToken).ConfigureAwait(false);

        var sdkRequest = new LlrpSdk.WriteTagRequest
        {
            MemoryBank = (LlrpSdk.TagMemoryBank)request.MemoryBank,
            WordPointer = request.OffsetWords,
            WriteData = SdkTagAccessMapper.ParseWords(request.DataHex),
            Selection = SdkTagAccessMapper.BuildSelection(request.Epc, request.SelectionBank),
            AntennaId = request.AntennaId ?? 0,
            AccessPassword = SdkTagAccessMapper.ParseAccessPassword(request.AccessPasswordHex),
        };

        LlrpSdk.TagAccessResult result = await reader.WriteTagMemoryAsync(
            sdkRequest, TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        return SdkTagAccessMapper.MapOperationResult(result.Operation);
    }

    public async Task SetGpoAsync(ushort portNumber, bool state, CancellationToken cancellationToken)
    {
        await SynchronizeIfNeededAsync(cancellationToken).ConfigureAwait(false);
        await reader.SetGpoAsync(portNumber, state, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Tagging.GpiPortStatus>> GetGpiStatusAsync(CancellationToken cancellationToken)
    {
        return (await GetGpioStatusAsync(cancellationToken).ConfigureAwait(false)).Gpis;
    }

    public async Task<IReadOnlyList<Tagging.GpoPortStatus>> GetGpoStatusAsync(CancellationToken cancellationToken)
    {
        return (await GetGpioStatusAsync(cancellationToken).ConfigureAwait(false)).Gpos;
    }

    public async Task<Tagging.GpioStatusSnapshot> GetGpioStatusAsync(CancellationToken cancellationToken)
    {
        ReaderSettingsSnapshot snapshot = await QuerySettingsAsync(cancellationToken).ConfigureAwait(false);
        return new Tagging.GpioStatusSnapshot
        {
            Gpis = snapshot.Settings.Configuration.Gpis
                .Select(gpi => new Tagging.GpiPortStatus
                {
                    PortNumber = gpi.GpiPortNumber,
                    Configured = gpi.Configured,
                    State = gpi.State == GpiState.High,
                })
                .ToArray(),
            Gpos = snapshot.Settings.Configuration.Gpos
                .Select(gpo => new Tagging.GpoPortStatus
                {
                    PortNumber = gpo.GpoPortNumber,
                    State = gpo.GpoData,
                })
                .ToArray(),
        };
    }

    private async Task SynchronizeIfNeededAsync(CancellationToken cancellationToken)
    {
        if (!reader.IsManagedStateSynchronized)
        {
            await reader.SynchronizeStateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// TagAccess is implemented by the SDK through a temporary managed inventory lease. A
    /// short-lived connection may have no SDK-managed ROSpec after reconnect even though the
    /// Reader still reports its current settings, so materialize that current configuration on
    /// this same connection before executing the access operation.
    /// </summary>
    private async Task EnsureTagAccessConfigurationAsync(CancellationToken cancellationToken)
    {
        ReaderSettingsSnapshot snapshot = await reader.QuerySettingsAsync(cancellationToken)
            .ConfigureAwait(false);
        ReaderSettings? fallbackSettings = null;
        if (snapshot.Settings.Inventory is null && snapshot.ManagedRoSpec?.Inventory is null)
        {
            fallbackSettings = (await reader.GetDefaultSettingsAsync(cancellationToken)
                .ConfigureAwait(false)).Settings;
        }

        ReaderSettings settings = MaterializeTagAccessSettings(snapshot, fallbackSettings);

        await reader.ApplySettingsAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    internal static ReaderSettings MaterializeTagAccessSettings(
        ReaderSettingsSnapshot snapshot,
        ReaderSettings? fallbackSettings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Settings.Inventory is not null)
        {
            return snapshot.Settings;
        }

        if (snapshot.ManagedRoSpec?.Inventory is { } managedInventory)
        {
            // 部分 Reader 把当前托管 ROSpec 与 ReaderSettings 分开返回。
            // TagAccess 需要 materialize 一份完整设置，但不能因为 Inventory 为空
            // 就把设备当前配置替换成 SDK 默认值。
            return snapshot.Settings with { Inventory = managedInventory };
        }

        if (fallbackSettings?.Inventory is { } fallbackInventory)
        {
            // 没有 managed ROSpec 时只借用默认 Inventory；Query 得到的当前
            // ReaderConfiguration/Extensions 仍然是更准确的设备状态，不能整体回退。
            return snapshot.Settings with { Inventory = fallbackInventory };
        }

        return fallbackSettings
            ?? throw new InvalidOperationException("No current or fallback Reader Inventory settings are available.");
    }

    private static InventorySettings ApplyInventorySpec(InventorySettings settings, Tagging.InventorySpec spec)
    {
        if (spec.Antennas.Count > 0)
        {
            ushort[] selectedAntennas = spec.Antennas.ToArray();
            bool selectsAllAntennas = selectedAntennas.Contains((ushort)0);
            settings = settings with
            {
                AntennaIds = selectedAntennas,
                AntennaConfigurations = selectsAllAntennas
                    ? settings.AntennaConfigurations
                    : settings.AntennaConfigurations
                        .Where(configuration =>
                            configuration.AntennaId == 0
                            || selectedAntennas.Contains(configuration.AntennaId))
                        .ToArray(),
            };
        }

        if (spec.Report is { } report)
        {
            settings = settings with
            {
                Report = settings.Report with
                {
                    IncludeAntennaId = report.IncludeAntennaId ?? settings.Report.IncludeAntennaId,
                    IncludeChannelIndex = report.IncludeChannelIndex ?? settings.Report.IncludeChannelIndex,
                    IncludePeakRssi = report.IncludePeakRssi ?? settings.Report.IncludePeakRssi,
                    IncludeFirstSeenTimestamp = report.IncludeFirstSeenTimestamp ?? settings.Report.IncludeFirstSeenTimestamp,
                    IncludeLastSeenTimestamp = report.IncludeLastSeenTimestamp ?? settings.Report.IncludeLastSeenTimestamp,
                    IncludeTagSeenCount = report.IncludeTagSeenCount ?? settings.Report.IncludeTagSeenCount,
                    IncludePcBits = report.IncludePcBits ?? settings.Report.IncludePcBits,
                },
            };
        }

        return settings;
    }

    public async ValueTask DisposeAsync()
    {
        reader.TagsReported -= OnTagsReported;
        reader.ReaderExceptionOccurred -= OnReaderExceptionOccurred;
        reader.ConnectionChanged -= OnConnectionChanged;
        reader.GpiChanged -= OnGpiChanged;
        await reader.DisposeAsync().ConfigureAwait(false);
    }

    private void OnTagsReported(object? sender, TagReportEventArgs args) =>
        TagReported?.Invoke(this, new SdkTagReportEventArgs(args.Report));

    private void OnReaderExceptionOccurred(object? sender, ReaderExceptionEventArgs args) =>
        ReaderExceptionOccurred?.Invoke(this, new ReaderDeviceExceptionEventArgs(
            args.Message,
            args.ROSpecId,
            args.AntennaId,
            args.Timestamp));

    private void OnConnectionChanged(object? sender, ReaderConnectionChangedEventArgs args)
    {
        if (args.CurrentState != ReaderConnectionState.Faulted)
        {
            return;
        }

        if (args.DeviceInitiatedClose)
        {
            DeviceInitiatedClosed?.Invoke(this, EventArgs.Empty);
            return;
        }

        ConnectionFaulted?.Invoke(
            this,
            new ReaderConnectionFaultedEventArgs("Reader connection entered the faulted state."));
    }

    private void OnGpiChanged(object? sender, GpiChangedEventArgs args) =>
        GpiChanged?.Invoke(this, new SdkGpiChangedEventArgs(args.PortNumber, args.State, args.Timestamp));
}

/// <summary>创建标准 LLRP 会话的工厂实现（无 Impinj）。</summary>
public sealed class LlrpReaderSessionFactory : IReaderSessionFactory
{
    private readonly ILoggerFactory loggerFactory;

    public LlrpReaderSessionFactory(ILoggerFactory loggerFactory)
    {
        this.loggerFactory = loggerFactory;
    }

    public IReaderSession Create(ReaderProfile profile, IReadOnlyList<IReaderExtensionModule>? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();

        var builder = new LlrpReaderBuilder(profile.Host)
            .WithPort(profile.Port)
            .WithProtocolVersionPolicy(profile.LlrpVersion switch
            {
                LlrpProtocolVersionOption.Force101 => LlrpProtocolVersionPolicy.Force101,
                LlrpProtocolVersionOption.Force11 => LlrpProtocolVersionPolicy.Force11,
                _ => LlrpProtocolVersionPolicy.Auto,
            });
        builder.WithLoggerFactory(loggerFactory);

        if (extensions is not null)
        {
            foreach (IReaderExtensionModule module in extensions)
            {
                module.ConfigureBuilder(new ReaderBuilderContext(builder));
            }
        }

        return new LlrpReaderSession(builder.Build());
    }
}
