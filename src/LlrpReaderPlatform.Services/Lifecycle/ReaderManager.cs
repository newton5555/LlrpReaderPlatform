using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Errors;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Tagging;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Services.Capabilities;
using LlrpReaderPlatform.Services.Extensions;
using LlrpReaderPlatform.Services.Persistence;
using LlrpReaderPlatform.Services.Sdk;
using LlrpReaderPlatform.Services.Settings;
using LlrpSdk;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tagging = LlrpReaderPlatform.Contracts.Tagging;

namespace LlrpReaderPlatform.Services.Lifecycle;

/// <summary>
/// Reader 生命周期与盘存协调管理器（Singleton）：补偿式添加、短连接激活、Enable 分离、
/// 单 Session + 异步 Gate 串行化、Inventory 长租约 + ReaderBusy 冲突、TagReport 有界
/// Channel 聚合。状态在服务线程发布，UI 适配层负责切换线程。
/// </summary>
public sealed class ReaderManager : IReaderManager, IInventoryService, IReaderSettingsRuntime, IAsyncDisposable
{
    private const int TagChannelCapacity = 8_192;
    private const int TagPublishBatchSize = 512;
    private const int TagLogChannelCapacity = 25_000;

    private readonly IReaderSessionFactory sessionFactory;
    private readonly IReaderProfileStore profileStore;
    private readonly IInventoryRunStore? runStore;
    private readonly IInventoryTagLog tagLog;
    private readonly IInventorySnapshotStore snapshotStore;
    private readonly ILogger<ReaderManager> logger;
    private readonly IReadOnlyList<LlrpReaderPlatform.Services.Extensions.IReaderExtensionModule> extensionModules;
    private readonly ConcurrentDictionary<Guid, ReaderHandle> readers = new();
    private readonly SemaphoreSlim registryGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TagAggregateStore> aggregates = new();
    private readonly Channel<TagWorkItem> tagChannel;
    private readonly Task tagConsumer;
    private readonly Channel<TagLogWorkItem> tagLogChannel;
    private readonly Task tagLogConsumer;
    private readonly object disposeSync = new();
    private Task? disposeTask;
    private int disposeStarted;
    private long revisionCounter;
    private long tagsDropped;

    public ReaderManager(
        IReaderSessionFactory sessionFactory,
        IReaderProfileStore? profileStore = null,
        ILogger<ReaderManager>? logger = null,
        IEnumerable<LlrpReaderPlatform.Services.Extensions.IReaderExtensionModule>? extensions = null,
        IInventoryRunStore? runStore = null,
        IInventoryTagLog? tagLog = null,
        IInventorySnapshotStore? snapshotStore = null)
    {
        this.sessionFactory = sessionFactory;
        this.profileStore = profileStore ?? new InMemoryProfileStore();
        this.runStore = runStore;
        this.tagLog = tagLog ?? new NullInventoryTagLog();
        this.snapshotStore = snapshotStore ?? new NullInventorySnapshotStore();
        this.logger = logger ?? NullLogger<ReaderManager>.Instance;
        this.extensionModules = extensions?.ToArray() ?? [];

        tagChannel = Channel.CreateBounded<TagWorkItem>(new BoundedChannelOptions(TagChannelCapacity)
        {
            // TryWrite must return false when the queue is full so the producer can
            // decrement PendingTagReports and expose a deterministic drop counter.
            // BoundedChannelFullMode.DropWrite may report a successful TryWrite even
            // though the new item was silently discarded.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        tagLogChannel = Channel.CreateBounded<TagLogWorkItem>(new BoundedChannelOptions(TagLogChannelCapacity)
        {
            // Logging is downstream of tag aggregation. A bounded wait queue keeps
            // high-rate logging from creating one Task per report while allowing the
            // aggregator to apply backpressure before the main TagReport queue fills.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });
        tagConsumer = Task.Run(ConsumeTagReportsAsync);
        tagLogConsumer = Task.Run(ConsumeTagLogsAsync);
    }

    public event EventHandler<ReaderStateChangedEventArgs>? StateChanged;
    public event EventHandler<TagObservedEventArgs>? TagObserved;
    public event EventHandler<InventoryLifecycleChangedEventArgs>? LifecycleChanged;
    public event EventHandler<GpiObservedEventArgs>? GpiChanged;

    public long DroppedTagReportCount => Interlocked.Read(ref tagsDropped);

    public IReadOnlyList<ReaderRuntimeSnapshot> Readers =>
        readers.Values
            .Select(static h => h.Snapshot)
            .OrderBy(static s => s.Profile.Name)
            .ToArray();

    public ReaderRuntimeSnapshot GetSnapshot(Guid readerId) =>
        Get(readerId).Snapshot;

    public Task<ReaderProbeResult> ProbeAsync(ReaderProfile profile, CancellationToken ct = default)
    {
        ThrowIfDisposing();
        return ProbeCoreAsync(profile, ct);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        ThrowIfDisposing();
        IReadOnlyList<ReaderProfile> profiles = await profileStore.GetAllAsync(ct).ConfigureAwait(false);
        foreach (ReaderProfile profile in profiles)
        {
            ct.ThrowIfCancellationRequested();
            ReaderProfile normalizedProfile = profile with
            {
                Host = ReaderEndpoint.NormalizeHost(profile.Host),
            };
            if (readers.ContainsKey(normalizedProfile.Id))
            {
                continue;
            }

            IReaderSession? restoredSession = null;
            bool registered = false;
            try
            {
                // 启动恢复仍走标准 Probe → 扩展匹配两阶段流程；离线 Reader 也注册到
                // 列表，稍后由用户手动激活，不因启动时网络不可用而丢失配置。
                ReaderProbeResult probe = await ProbeCoreAsync(normalizedProfile, ct).ConfigureAwait(false);
                var probeInfo = new ReaderProbeInfo(
                    probe.ManufacturerId,
                    probe.ModelId,
                    probe.Firmware,
                    probe.Model,
                    ToSdkProtocolVersion(probe.NegotiatedProtocolVersion));
                IReadOnlyList<IReaderExtensionModule> applicable = GetApplicableExtensions(probeInfo);
                restoredSession = sessionFactory.Create(normalizedProfile, applicable);
                var handle = new ReaderHandle(
                    normalizedProfile,
                    restoredSession,
                    NextSnapshot(normalizedProfile, normalizedProfile.IsEnabled, applicable),
                    applicable,
                    needsExtensionResolution: !probe.Succeeded);
                AttachSessionEvents(handle);

                await registryGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (readers.ContainsKey(profile.Id))
                    {
                        await TryDisposeQuietlyAsync(restoredSession).ConfigureAwait(false);
                        restoredSession = null;
                        continue;
                    }

                    readers[profile.Id] = handle;
                    aggregates[profile.Id] = new TagAggregateStore();
                    registered = true;
                }
                finally
                {
                    registryGate.Release();
                }

                if (normalizedProfile.IsEnabled)
                {
                    ReaderActivationResult activation = await ActivateAsync(normalizedProfile.Id, ct).ConfigureAwait(false);
                    if (!activation.Succeeded)
                    {
                        logger.LogWarning("Reader {Id} restored but activation failed: {Error}", normalizedProfile.Id, activation.Error);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to restore Reader profile {Id}.", normalizedProfile.Id);
                if (!registered && restoredSession is not null)
                {
                    await TryDisposeQuietlyAsync(restoredSession).ConfigureAwait(false);
                }
            }
        }
    }

    // ---------- Lifecycle ----------

    public async Task<ReaderAddResult> AddAsync(
        ReaderProfile profile,
        bool enableAfterAdding,
        CancellationToken ct = default)
    {
        ThrowIfDisposing();
        ArgumentNullException.ThrowIfNull(profile);
        profile = profile with { Host = ReaderEndpoint.NormalizeHost(profile.Host) };
        profile.Validate();

        // 运行时先做一次无网络的快速判断，避免用户重复提交同一端点时再次连接设备。
        // 下面持有 registryGate 后还会读取持久化存储并复核一次，覆盖并发 AddAsync。
        ReaderProfile? registeredDuplicate = FindRegisteredEndpoint(profile);
        if (registeredDuplicate is not null)
        {
            return CreateDuplicateAddResult(profile);
        }

        ReaderProbeResult probe = await ProbeCoreAsync(profile, ct).ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            return new ReaderAddResult(ReaderAddStatus.ProbeFailed, probe.Error)
            {
                Model = probe.Model,
                Firmware = probe.Firmware,
                ManufacturerId = probe.ManufacturerId,
                ModelId = probe.ModelId,
                NegotiatedProtocolVersion = probe.NegotiatedProtocolVersion,
                ErrorCode = probe.ErrorCode,
            };
        }

        // 两阶段：标准 Probe 已获取厂商身份，据此解析匹配的扩展模块。
        var probeInfo = new ReaderProbeInfo(
            probe.ManufacturerId,
            probe.ModelId,
            probe.Firmware,
            probe.Model,
            ToSdkProtocolVersion(probe.NegotiatedProtocolVersion));
        IReadOnlyList<IReaderExtensionModule> applicable = GetApplicableExtensions(probeInfo);

        IReaderSession? session = null;
        bool persistedToStore = false;
        await registryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (readers.ContainsKey(profile.Id))
            {
                return CreateAddResult(
                    ReaderAddStatus.RegisterFailed,
                    "Reader is already registered.",
                    probe,
                    applicable,
                    errorCode: PlatformErrorCode.AlreadyExists);
            }

            IReadOnlyList<ReaderProfile> persistedProfiles;
            try
            {
                persistedProfiles = await profileStore.GetAllAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to inspect persisted profiles before registering {Id}.", profile.Id);
                return CreateAddResult(
                    ReaderAddStatus.PersistFailed,
                    ex.Message,
                    probe,
                    applicable,
                    errorCode: PlatformErrorCode.PersistenceFailed);
            }

            if (persistedProfiles.Any(existing => HasSameEndpoint(existing, profile)))
            {
                return CreateAddResult(
                    ReaderAddStatus.RegisterFailed,
                    $"Reader endpoint '{ReaderEndpoint.Format(profile.Host, profile.Port)}' is already registered.",
                    probe,
                    applicable,
                    errorCode: PlatformErrorCode.AlreadyExists);
            }

            IReadOnlyList<ReaderProfile> existingProfiles = persistedProfiles
                .Concat(readers.Values.Select(static handle => handle.Profile))
                .ToArray();
            profile = profile with
            {
                Name = CreateUniqueReaderName(profile.Name, existingProfiles),
            };
            ReaderProfile persisted = profile with { IsEnabled = enableAfterAdding };

            try
            {
                await profileStore.SaveAsync(persisted, ct).ConfigureAwait(false);
                persistedToStore = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Add 的取消必须保持取消语义，不能被包装成持久化失败，
                // 否则 WPF 关闭/切换页面时会误报一条设备错误。
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to persist profile {Id}.", profile.Id);
                return CreateAddResult(ReaderAddStatus.PersistFailed, ex.Message, probe, applicable);
            }

            try
            {
                session = sessionFactory.Create(profile, applicable);
                var handle = new ReaderHandle(
                    persisted,
                    session,
                    NextSnapshot(profile, enableAfterAdding, applicable),
                    applicable);
                AttachSessionEvents(handle);
                readers[profile.Id] = handle;
                // 新会话独立盘存周期：重置聚合库，避免旧报告混入。
                aggregates[profile.Id] = new TagAggregateStore();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to register reader {Id}; rolling back persisted profile.", profile.Id);
                if (persistedToStore)
                {
                    await TryDeleteProfileAsync(profile.Id);
                }

                if (session is not null)
                {
                    await TryDisposeQuietlyAsync(session);
                }

                return CreateAddResult(ReaderAddStatus.RegisterFailed, ex.Message, probe, applicable);
            }
        }
        finally
        {
            registryGate.Release();
        }

        if (enableAfterAdding)
        {
            ReaderActivationResult activation;
            try
            {
                activation = await ActivateAsync(profile.Id, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // AddAsync 已先把 IsEnabled 持久化为 true。若激活阶段被取消，
                // 必须补偿回 disabled，否则下次启动会把一个未完成激活的 Reader
                // 当成可用设备恢复。
                await RollbackEnabledAsync(profile.Id).ConfigureAwait(false);
                throw;
            }

            if (!activation.Succeeded)
            {
                await RollbackEnabledAsync(profile.Id);
                return CreateAddResult(
                    ReaderAddStatus.ActivationFailed,
                    activation.Error,
                    probe,
                    applicable,
                    profile.Id,
                    activation.ErrorCode);
            }
        }

        return CreateAddResult(ReaderAddStatus.Added, null, probe, applicable, profile.Id);
    }

    private ReaderProfile? FindRegisteredEndpoint(ReaderProfile profile) =>
        readers.Values
            .Select(static handle => handle.Profile)
            .FirstOrDefault(existing => HasSameEndpoint(existing, profile));

    private static bool HasSameEndpoint(ReaderProfile left, ReaderProfile right) =>
        left.Port == right.Port
        && string.Equals(
            ReaderEndpoint.NormalizeHost(left.Host),
            ReaderEndpoint.NormalizeHost(right.Host),
            StringComparison.OrdinalIgnoreCase);

    private static string CreateUniqueReaderName(
        string? requestedName,
        IEnumerable<ReaderProfile> existingProfiles)
    {
        string name = string.IsNullOrWhiteSpace(requestedName) ? "Reader" : requestedName.Trim();
        HashSet<string> usedNames = existingProfiles
            .Select(static profile => profile.Name.Trim())
            .Where(static profileName => profileName.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!usedNames.Contains(name))
        {
            return name;
        }

        int digitStart = name.Length;
        while (digitStart > 0 && char.IsAsciiDigit(name[digitStart - 1]))
        {
            digitStart--;
        }

        string stem = name[..digitStart].TrimEnd();
        string separator = digitStart == name.Length
            || digitStart > 0 && char.IsWhiteSpace(name[digitStart - 1])
            ? " "
            : string.Empty;
        int width = name.Length - digitStart;
        long nextNumber = 2;

        if (digitStart < name.Length
            && long.TryParse(name[digitStart..], NumberStyles.None, CultureInfo.InvariantCulture, out long suffix))
        {
            nextNumber = suffix == long.MaxValue ? 2 : Math.Max(1, suffix + 1);
        }

        if (stem.Length == 0)
        {
            stem = "Reader";
        }

        while (true)
        {
            string suffixText = width > 0
                ? nextNumber.ToString($"D{width}", CultureInfo.InvariantCulture)
                : nextNumber.ToString(CultureInfo.InvariantCulture);
            string candidate = $"{stem}{separator}{suffixText}";
            if (!usedNames.Contains(candidate))
            {
                return candidate;
            }

            nextNumber = nextNumber == long.MaxValue ? 2 : nextNumber + 1;
        }
    }

    private static ReaderAddResult CreateDuplicateAddResult(ReaderProfile profile) =>
        new(
            ReaderAddStatus.RegisterFailed,
            $"Reader endpoint '{ReaderEndpoint.Format(profile.Host, profile.Port)}' is already registered.",
            profile.Id)
        {
            ErrorCode = PlatformErrorCode.AlreadyExists,
        };

    private static ReaderAddResult CreateAddResult(
        ReaderAddStatus status,
        string? error,
        ReaderProbeResult probe,
        IReadOnlyList<IReaderExtensionModule> extensions,
        Guid? readerId = null,
        PlatformErrorCode? errorCode = null) => new(status, error, readerId)
        {
            ErrorCode = errorCode ?? status switch
            {
                ReaderAddStatus.Added => PlatformErrorCode.None,
                ReaderAddStatus.PersistFailed => PlatformErrorCode.PersistenceFailed,
                ReaderAddStatus.RegisterFailed => PlatformErrorCode.RegistrationFailed,
                _ => PlatformErrorCode.DeviceFailed,
            },
            Model = probe.Model,
            Firmware = probe.Firmware,
            ManufacturerId = probe.ManufacturerId,
            ModelId = probe.ModelId,
            NegotiatedProtocolVersion = probe.NegotiatedProtocolVersion,
            MatchedExtensionIds = extensions.Select(static extension => extension.Id).ToArray(),
        };

    public async Task RemoveAsync(Guid readerId, CancellationToken ct = default)
    {
        await registryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ReaderHandle handle = Get(readerId);
            await handle.Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                bool hadInventory = handle.InventoryRunning || handle.ActiveRun is not null;
                CancelInventoryDuration(handle);
                // Remove 是不可逆的清理操作；调用方取消只取消等待，不得让旧 Session
                // 因为取消令牌已触发而遗留在 Reader 上。
                await StopInventorySessionQuietlyAsync(handle, CancellationToken.None).ConfigureAwait(false);
                handle.AcceptTagReports = false;
                await DrainTagReportsAsync(handle).ConfigureAwait(false);
                await CompleteRunAsync(handle, "Removed").ConfigureAwait(false);
                handle.InventoryRunning = false;
                if (handle.Session.IsConnected)
                {
                    await TryDisconnectQuietlyAsync(handle, CancellationToken.None);
                }

                if (hadInventory)
                {
                    PublishInventoryStopped(handle, InventoryStopReason.Removed);
                }

                try
                {
                    await handle.Session.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to dispose session for {Id}.", readerId);
                }
            }
            finally
            {
                readers.TryRemove(new KeyValuePair<Guid, ReaderHandle>(readerId, handle));
                aggregates.TryRemove(readerId, out _);
                handle.Gate.Release();
                handle.Gate.Dispose();
            }

            // Keep registryGate held through the persistent delete. AddAsync cannot
            // register a replacement with the same Guid until the old profile is gone.
            await TryDeleteProfileAsync(readerId);
        }
        finally
        {
            registryGate.Release();
        }
    }

    public async Task SetEnabledAsync(Guid readerId, bool enabled, CancellationToken ct = default)
    {
        ReaderHandle handle = await AcquireHandleAsync(readerId, ct).ConfigureAwait(false);
        ReaderProfile updated = handle.Profile with { IsEnabled = enabled };
        try
        {
            await profileStore.SaveAsync(updated, ct).ConfigureAwait(false);
            handle.Profile = updated;
            handle.Snapshot = handle.Snapshot with { IsEnabled = enabled };
            Publish(handle);
            if (!enabled)
            {
                await DeactivateCoreAsync(handle).ConfigureAwait(false);
            }
        }
        finally
        {
            handle.Gate.Release();
        }
    }

    public async Task<ReaderActivationResult> ActivateAsync(Guid readerId, CancellationToken ct = default)
    {
        ReaderHandle handle = await AcquireHandleAsync(readerId, ct).ConfigureAwait(false);
        try
        {
            if (handle.InventoryRunning)
            {
                return new ReaderActivationResult(false, "Reader busy: inventory is running. Stop inventory first.")
                { ErrorCode = PlatformErrorCode.ReaderBusy };
            }

            // 启动恢复时 Reader 可能在 Probe 阶段离线。设备重新在线后，第一次激活必须
            // 再走一次标准 Probe -> 扩展匹配，否则已恢复的标准 Session 会永久跳过厂商扩展。
            try
            {
                await ResolveExtensionsFromProbeAsync(handle, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await ResetCancelledSessionAsync(handle).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                handle.Snapshot = handle.Snapshot with
                {
                    State = ReaderState.Faulted,
                    IsStale = true,
                    Error = ex.Message,
                };
                handle.NeedsExtensionResolution = true;
                handle.SessionNeedsRecreation = true;
                Publish(handle);
                return new ReaderActivationResult(false, ex.Message);
            }
            handle.Snapshot = handle.Snapshot with { State = ReaderState.Connecting, Error = null };
            Publish(handle);
            try
            {
                await handle.Session.ConnectAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await ResetCancelledSessionAsync(handle).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                // 某些传输层可能在握手失败后仍保留半开的 socket；激活失败也必须
                // 走统一清理，避免下一次 Activate/Inventory 复用脏连接。
                await TryDisconnectQuietlyAsync(handle, CancellationToken.None).ConfigureAwait(false);
                handle.Snapshot = handle.Snapshot with
                {
                    State = ReaderState.Faulted,
                    IsStale = true,
                    Error = ex.Message,
                };
                handle.NeedsExtensionResolution = true;
                handle.SessionNeedsRecreation = true;
                Publish(handle);
                return new ReaderActivationResult(false, ex.Message);
            }

            // Probe 仍可能因为瞬时网络错误失败，但随后注册的标准 Session 已经连上；
            // 此时可直接用同一次连接读到的身份完成扩展解析，再重建带扩展的 Session。
            try
            {
                await ResolveExtensionsFromConnectedIdentityAsync(handle, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await ResetCancelledSessionAsync(handle).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                await TryDisconnectQuietlyAsync(handle, CancellationToken.None).ConfigureAwait(false);
                handle.Snapshot = handle.Snapshot with
                {
                    State = ReaderState.Faulted,
                    IsStale = true,
                    Error = ex.Message,
                };
                handle.NeedsExtensionResolution = true;
                handle.SessionNeedsRecreation = true;
                Publish(handle);
                return new ReaderActivationResult(false, ex.Message);
            }

            CaptureCapabilities(handle, ReaderState.Connected);
            Publish(handle);

            try
            {
                TransitionState(handle, ReaderState.Disconnecting);
                await handle.Session.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                handle.NeedsExtensionResolution = true;
                handle.SessionNeedsRecreation = true;
                handle.Snapshot = handle.Snapshot with
                {
                    State = ReaderState.Faulted,
                    IsStale = true,
                    Error = ex.Message,
                };
                Publish(handle);
                return new ReaderActivationResult(false, ex.Message)
                { ErrorCode = PlatformErrorCode.DeviceFailed };
            }

            handle.Snapshot = handle.Snapshot with { State = ReaderState.Disconnected };
            Publish(handle);
            return new ReaderActivationResult(true);
        }
        finally
        {
            handle.Gate.Release();
        }
    }

    public async Task DeactivateAsync(Guid readerId, CancellationToken ct = default)
    {
        ReaderHandle handle = await AcquireHandleAsync(readerId, ct).ConfigureAwait(false);
        try
        {
            await DeactivateCoreAsync(handle).ConfigureAwait(false);
        }
        finally
        {
            handle.Gate.Release();
        }
    }

    private async Task DeactivateCoreAsync(ReaderHandle handle)
    {
        bool hadInventory = handle.InventoryRunning || handle.ActiveRun is not null;
        if (hadInventory)
        {
            TransitionState(handle, ReaderState.Stopping);
        }

        CancelInventoryDuration(handle);
        // Deactivate/Disable 的目标是释放连接租约。即使 UI 在等待期间取消，
        // 内部 Stop/Drain/Disconnect 仍必须继续完成，避免停用后留下半开连接。
        Exception? stopError = await StopInventorySessionQuietlyAsync(handle, CancellationToken.None)
            .ConfigureAwait(false);
        handle.AcceptTagReports = false;
        await DrainTagReportsAsync(handle).ConfigureAwait(false);
        handle.InventoryRunning = false;
        Exception? disconnectError = null;
        if (handle.Session.IsConnected)
        {
            TransitionState(handle, ReaderState.Disconnecting);
            try
            {
                await handle.Session.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                disconnectError = ex;
                logger.LogWarning(ex, "Failed to disconnect deactivated Reader {Id}.", handle.Profile.Id);
            }
        }

        Exception? lifecycleError = stopError ?? disconnectError;
        await CompleteRunAsync(
            handle,
            lifecycleError is null ? "Deactivated" : "StopFailed").ConfigureAwait(false);

        if (hadInventory)
        {
            PublishInventoryStopped(
                handle,
                lifecycleError is null ? InventoryStopReason.Deactivated : InventoryStopReason.StopFailed,
                lifecycleError?.Message);
        }

        handle.CapabilityCapture = null;
        handle.NeedsExtensionResolution = true;
        handle.SessionNeedsRecreation = lifecycleError is not null;
        handle.Snapshot = handle.Snapshot with
        {
            State = lifecycleError is null ? ReaderState.Disconnected : ReaderState.Faulted,
            Model = null,
            Firmware = null,
            CapturedAt = null,
            IsStale = true,
            Error = lifecycleError?.Message,
        };
        Publish(handle);

        if (lifecycleError is not null)
        {
            throw new InvalidOperationException(
                $"Failed to deactivate reader '{handle.Profile.Name}'.",
                lifecycleError);
        }
    }

    // ---------- Settings runtime (Services 内部 SDK 桥接) ----------

    public async Task<ReaderSettingsRuntimeSnapshot> QueryAsync(Guid readerId, CancellationToken ct = default)
    {
        ReaderHandle handle = await AcquireHandleAsync(readerId, ct).ConfigureAwait(false);
        try
        {
            ThrowIfInventoryRunning(handle);
            try
            {
                return await ExecuteShortSessionOperationAsync(
                    handle,
                    ct,
                    () => QuerySettingsCoreAsync(handle, ct)).ConfigureAwait(false);
            }
            finally
            {
                await DisconnectShortOperationAsync(handle).ConfigureAwait(false);
            }
        }
        finally
        {
            handle.Gate.Release();
        }
    }

    public async Task<ReaderSettingsRuntimeSnapshot> GetDefaultsAsync(Guid readerId, CancellationToken ct = default)
    {
        ReaderHandle handle = await AcquireHandleAsync(readerId, ct).ConfigureAwait(false);
        try
        {
            ThrowIfInventoryRunning(handle);
            try
            {
                return await ExecuteShortSessionOperationAsync(
                    handle,
                    ct,
                    async () =>
                    {
                        ReaderSettingsDefaults defaults = await handle.Session
                            .GetDefaultSettingsAsync(ct)
                            .ConfigureAwait(false);
                        return new ReaderSettingsRuntimeSnapshot(
                            new ReaderSettingsSnapshot(defaults.Settings, ManagedRoSpec: null),
                            handle.Session.Capabilities);
                    }).ConfigureAwait(false);
            }
            finally
            {
                await DisconnectShortOperationAsync(handle).ConfigureAwait(false);
            }
        }
        finally
        {
            handle.Gate.Release();
        }
    }

    public async Task ApplyAsync(
        Guid readerId,
        Func<ReaderSettingsRuntimeSnapshot, ReaderSettings> compile,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(compile);
        ReaderHandle handle = await AcquireHandleAsync(readerId, ct).ConfigureAwait(false);
        try
        {
            ThrowIfInventoryRunning(handle);
            try
            {
                ReaderSettingsRuntimeSnapshot current = await ExecuteShortSessionOperationAsync(
                    handle,
                    ct,
                    () => QuerySettingsCoreAsync(handle, ct)).ConfigureAwait(false);
                ReaderSettings settings = compile(current);
                await ExecuteShortSessionOperationAsync(
                    handle,
                    ct,
                    async () =>
                    {
                        await handle.Session.ApplySettingsAsync(settings, ct).ConfigureAwait(false);
                        // Apply 后在同一 Session 内重新 Query；设备可能会规范化 index、触发器或
                        // 扩展字段，只有重新读取成功才把本次操作视为完成。
                        _ = await QuerySettingsCoreAsync(handle, ct).ConfigureAwait(false);
                        return true;
                    }).ConfigureAwait(false);
            }
            finally
            {
                await DisconnectShortOperationAsync(handle).ConfigureAwait(false);
            }
        }
        finally
        {
            handle.Gate.Release();
        }
    }

    private static async Task<ReaderSettingsRuntimeSnapshot> QuerySettingsCoreAsync(
        ReaderHandle handle,
        CancellationToken ct)
    {
        ReaderSettingsSnapshot settings = await handle.Session.QuerySettingsAsync(ct).ConfigureAwait(false);
        if (settings.ManagedRoSpec?.Inventory is { } managedInventory
            && settings.Settings.Inventory is null)
        {
            // SDK 将设备上的 managed ROSpec 与 ReaderConfig 分开返回；编译 Apply 时必须
            // 沿用同一份有效 Inventory 基线，不能 Query 显示设备值、Apply 却回到空默认值。
            settings = settings with { Settings = settings.Settings with { Inventory = managedInventory } };
        }

        return new ReaderSettingsRuntimeSnapshot(settings, handle.Session.Capabilities);
    }

    private static void ThrowIfInventoryRunning(ReaderHandle handle)
    {
        if (handle.InventoryRunning)
        {
            throw new ReaderBusyException("Reader busy: inventory is running. Stop inventory first.");
        }
    }

    private static string? ValidateInventorySpec(InventorySpec? spec)
    {
        if (spec is null)
        {
            return "Inventory 参数不能为空。";
        }

        if (spec.DurationSeconds is < 0 or > 86_400)
        {
            return "寻卡时长必须是 0～86400 的整数秒；0 或空值表示持续运行。";
        }

        if (spec.Antennas is null)
        {
            return "寻卡天线集合不能为空。";
        }

        if (spec.Antennas.Count > 1
            && spec.Antennas.Contains((ushort)0))
        {
            return "寻卡天线不能同时包含全部天线(0)和指定天线。";
        }

        if (spec.Antennas.Count != spec.Antennas.Distinct().Count())
        {
            return "寻卡天线不能重复。";
        }

        return null;
    }

    private static string? ValidateInventoryAntennaOverride(ReaderHandle handle, InventorySpec spec)
    {
        if (spec.Antennas.Count == 0 || spec.Antennas.Contains((ushort)0))
        {
            return null;
        }

        ushort maxAntennas = handle.Session.Capabilities?.MaxNumberOfAntennas ?? 0;
        if (maxAntennas == 0)
        {
            return null;
        }

        ushort invalidAntenna = spec.Antennas.FirstOrDefault(antenna => antenna > maxAntennas);
        return invalidAntenna == 0
            ? null
            : $"Reader 只声明了 {maxAntennas} 个天线，天线 {invalidAntenna} 不存在。";
    }

    private static void ValidateGpoCommand(ReaderHandle handle, GpioCommand command)
    {
        if (command.PortNumber == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(command.PortNumber), "GPO 端口必须从 1 开始。");
        }

        ReaderGpioCapabilities? gpio = ReaderGpioCapabilities.From(handle.Session.Capabilities);
        if (gpio?.GpoCount is 0)
        {
            throw new PlatformOperationException(
                PlatformErrorCode.Unsupported,
                "Reader does not advertise standard GPO capability.");
        }

        if (gpio?.GpoCount is > 0 && command.PortNumber > gpio.GpoCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(command.PortNumber),
                $"Reader only exposes {gpio.GpoCount} GPO port(s).");
        }
    }

    private static void ValidateGpiStatusCapability(ReaderHandle handle)
    {
        ReaderGpioCapabilities? gpio = ReaderGpioCapabilities.From(handle.Session.Capabilities);
        if (gpio?.GpiCount is 0)
        {
            throw new PlatformOperationException(
                PlatformErrorCode.Unsupported,
                "Reader does not advertise standard GPI capability.");
        }
    }

    private static void ValidateGpoStatusCapability(ReaderHandle handle)
    {
        ReaderGpioCapabilities? gpio = ReaderGpioCapabilities.From(handle.Session.Capabilities);
        if (gpio?.GpoCount is 0)
        {
            throw new PlatformOperationException(
                PlatformErrorCode.Unsupported,
                "Reader does not advertise standard GPO capability.");
        }
    }

    private static void ValidateGpioStatusCapability(ReaderHandle handle)
    {
        ReaderGpioCapabilities? gpio = ReaderGpioCapabilities.From(handle.Session.Capabilities);
        if (gpio is { GpiCount: 0, GpoCount: 0 })
        {
            throw new PlatformOperationException(
                PlatformErrorCode.Unsupported,
                "Reader does not advertise standard GPIO capability.");
        }
    }

    private async Task StopInventoryAfterAsync(
        ReaderHandle handle,
        InventoryRunRecord expectedRun,
        IReaderSession expectedSession,
        int durationSeconds,
        CancellationTokenSource durationCts)
    {
        try
        {
            await Task.Delay(
                TimeSpan.FromSeconds(durationSeconds),
                durationCts.Token).ConfigureAwait(false);
            await StopInventoryCoreAsync(
                handle.Profile.Id,
                CancellationToken.None,
                "Duration",
                expectedSession,
                expectedRun).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Explicit Stop/Deactivate cancels the scheduled end.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Timed inventory stop failed for {Id}.", handle.Profile.Id);
        }
        finally
        {
            // The scheduled task owns the source lifetime. A concurrent manual Stop,
            // Deactivate, fault or application shutdown only cancels it; disposing here
            // avoids racing Task.Delay's cancellation registration and also handles the
            // natural-duration completion path.
            Interlocked.CompareExchange(
                ref handle.InventoryDurationCts,
                null,
                durationCts);
            durationCts.Dispose();
        }
    }

    private static void CancelInventoryDuration(ReaderHandle handle)
    {
        CancellationTokenSource? durationCts = Interlocked.Exchange(
            ref handle.InventoryDurationCts,
            null);
        try
        {
            durationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The scheduled task owns disposal. Stop/Deactivate may race with its
            // finally block; a source already disposed means the delay is already
            // completing or completed, so cancellation has no remaining work.
        }
    }

    private static void ClearActiveInventoryStopTrigger(ReaderHandle handle)
    {
        handle.ActiveInventoryStopTrigger = null;
        Interlocked.Exchange(ref handle.GpiStopQueued, 0);
    }

    private async Task CompleteRunAsync(ReaderHandle handle, string reason)
    {
        InventoryRunRecord? active = handle.ActiveRun;
        if (active is null)
        {
            return;
        }

        handle.ActiveRun = null;
        IReadOnlyList<TagObservation> tags = GetTags(handle.Profile.Id);
        await DrainTagLogsAsync(handle).ConfigureAwait(false);
        await CompleteTagLogQuietlyAsync(active).ConfigureAwait(false);
        InventoryRunRecord completedRun = active with
        {
            EndedAtUtc = DateTimeOffset.UtcNow,
            StopReason = reason,
            UniqueTagCount = tags.Count,
            TotalReadCount = tags.Sum(static x => x.ReadCount),
        };
        string? snapshotPath = await SaveSnapshotQuietlyAsync(
            new InventoryRunSnapshot
            {
                Run = completedRun,
                Tags = tags,
            }).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(snapshotPath))
        {
            completedRun = completedRun with { SnapshotFilePath = snapshotPath };
        }

        await SaveRunQuietlyAsync(completedRun).ConfigureAwait(false);
        logger.LogInformation(
            "Inventory run {RunId} completed for reader {ReaderId}: {StopReason}, {TotalReadCount} reads, {UniqueTagCount} unique tags, snapshot {SnapshotFilePath}.",
            completedRun.Id,
            completedRun.ReaderId,
            completedRun.StopReason,
            completedRun.TotalReadCount,
            completedRun.UniqueTagCount,
            completedRun.SnapshotFilePath ?? "none");
    }

    private async Task CompleteRunAfterDrainAsync(ReaderHandle handle, string reason)
    {
        handle.AcceptTagReports = false;
        await DrainTagReportsAsync(handle).ConfigureAwait(false);
        await CompleteRunAsync(handle, reason).ConfigureAwait(false);
    }

    private async Task SaveRunQuietlyAsync(InventoryRunRecord run)
    {
        if (runStore is null)
        {
            return;
        }

        try
        {
            await runStore.SaveAsync(run, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist inventory run {RunId}.", run.Id);
        }
    }

    private async Task AppendTagLogQuietlyAsync(InventoryRunRecord run, TagObservation tag)
    {
        try
        {
            await tagLog.AppendAsync(run, tag, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to append tag log for inventory run {RunId}.", run.Id);
        }
    }

    private async Task EnqueueTagLogAsync(ReaderHandle handle, InventoryRunRecord run, TagObservation tag)
    {
        Interlocked.Increment(ref handle.PendingTagLogs);
        try
        {
            await tagLogChannel.Writer.WriteAsync(
                new TagLogWorkItem(handle, run, tag),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to enqueue tag log for inventory run {RunId}.", run.Id);
            OnTagLogCompleted(handle);
        }
    }

    private async Task<string?> StartTagLogQuietlyAsync(InventoryRunRecord run)
    {
        try
        {
            return await tagLog.StartAsync(run, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to start tag log for inventory run {RunId}.", run.Id);
            return null;
        }
    }

    private async Task CompleteTagLogQuietlyAsync(InventoryRunRecord run)
    {
        try
        {
            await tagLog.CompleteAsync(run, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to complete tag log for inventory run {RunId}.", run.Id);
        }
    }

    private async Task ConsumeTagLogsAsync()
    {
        try
        {
            await foreach (TagLogWorkItem item in tagLogChannel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                try
                {
                    await AppendTagLogQuietlyAsync(item.Run, item.Tag).ConfigureAwait(false);
                }
                finally
                {
                    OnTagLogCompleted(item.Handle);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Consumer shut down with the manager.
        }
    }

    private async Task DrainTagLogsAsync(ReaderHandle handle)
    {
        Task waitTask;
        lock (handle.TagLogGate)
        {
            if (Volatile.Read(ref handle.PendingTagLogs) == 0)
            {
                return;
            }

            waitTask = (handle.TagLogWaiter ??= new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        await waitTask.ConfigureAwait(false);
    }

    private static void OnTagLogCompleted(ReaderHandle handle)
    {
        if (Interlocked.Decrement(ref handle.PendingTagLogs) != 0)
        {
            return;
        }

        lock (handle.TagLogGate)
        {
            if (Volatile.Read(ref handle.PendingTagLogs) == 0)
            {
                handle.TagLogWaiter?.TrySetResult(true);
                handle.TagLogWaiter = null;
            }
        }
    }

    // ---------- IInventoryService ----------

    public async Task<StartInventoryResult> StartInventoryAsync(
        Guid readerId,
        InventorySpec spec,
        CancellationToken ct = default)
    {
        ThrowIfDisposing();
        string? validationError = ValidateInventorySpec(spec);
        if (validationError is not null)
        {
            return new StartInventoryResult(false, InventoryError.InvalidSettings, validationError)
            { ErrorCode = PlatformErrorCode.InvalidSettings };
        }

        ReaderHandle handle = await AcquireHandleAsync(readerId, ct).ConfigureAwait(false);
        try
        {
            if (handle.InventoryRunning)
            {
                return new StartInventoryResult(false, InventoryError.ReaderBusy, "Inventory is already running.")
                { ErrorCode = PlatformErrorCode.ReaderBusy };
            }

            // StartInventory is a complete connect -> inventory lease operation. Publish
            // the per-Reader transition before probe/connect so consumers can indicate
            // progress for this Reader without blocking unrelated Readers or the page.
            TransitionState(handle, ReaderState.Connecting);

            // 启动恢复时可能只注册了无厂商扩展的标准 Session。若用户直接从寻卡页
            // 开始盘存，也必须先完成标准 Probe -> 扩展匹配 -> 会话替换，不能依赖用户
            // 先打开设备设置页触发 ActivateAsync。
            try
            {
                await ResolveExtensionsFromProbeAsync(handle, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Extension reprobe is the first step of a direct Inventory start
                // after a fault or restored offline session. It can be cancelled
                // before the normal Connect/Query cleanup block is entered, so
                // converge the registered Session here as well instead of leaving
                // the handle in Faulted/Connecting with a half-open transport.
                await CleanupFailedInventoryStartAsync(
                    handle,
                    error: null,
                    recreateSession: true).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                handle.Snapshot = handle.Snapshot with
                {
                    State = ReaderState.Faulted,
                    IsStale = true,
                    Error = ex.Message,
                };
                handle.NeedsExtensionResolution = true;
                handle.SessionNeedsRecreation = true;
                Publish(handle);
                return new StartInventoryResult(false, InventoryError.DeviceFailed, ex.Message)
                { ErrorCode = PlatformErrorCode.DeviceFailed };
            }

            if (!handle.Session.IsConnected)
            {
                try
                {
                    await handle.Session.ConnectAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    await ResetCancelledSessionAsync(handle).ConfigureAwait(false);
                    throw;
                }
                catch (Exception ex)
                {
                    await TryDisconnectQuietlyAsync(handle, CancellationToken.None).ConfigureAwait(false);
                    handle.Snapshot = handle.Snapshot with
                    {
                        State = ReaderState.Faulted,
                        IsStale = true,
                        Error = ex.Message,
                    };
                    handle.NeedsExtensionResolution = true;
                    handle.SessionNeedsRecreation = true;
                    Publish(handle);
                    return new StartInventoryResult(false, InventoryError.DeviceFailed, ex.Message)
                    { ErrorCode = PlatformErrorCode.DeviceFailed };
                }
            }

            try
            {
                // If the temporary reprobe failed but this session connected successfully,
                // resolve the extension from the identity carried by the connected session.
                // This mirrors ActivateAsync and prevents a transient Probe failure from
                // silently dropping vendor TagReport/settings behavior on direct Start.
                await ResolveExtensionsFromConnectedIdentityAsync(handle, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await CleanupFailedInventoryStartAsync(
                    handle,
                    error: null,
                    recreateSession: true).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                await TryDisconnectQuietlyAsync(handle, CancellationToken.None).ConfigureAwait(false);
                handle.Snapshot = handle.Snapshot with
                {
                    State = ReaderState.Faulted,
                    IsStale = true,
                    Error = ex.Message,
                };
                handle.NeedsExtensionResolution = true;
                handle.SessionNeedsRecreation = true;
                Publish(handle);
                return new StartInventoryResult(false, InventoryError.DeviceFailed, ex.Message)
                { ErrorCode = PlatformErrorCode.DeviceFailed };
            }

            // Inventory can be the first operation after an offline startup restore. Capture
            // identity/capabilities here as well as in ActivateAsync, otherwise a successful
            // direct Start would leave the runtime snapshot stale and the next Settings page
            // would incorrectly ask the user to reconnect.
            CaptureCapabilities(handle, ReaderState.Connected);
            Publish(handle);

            string? antennaValidationError = ValidateInventoryAntennaOverride(handle, spec);
            if (antennaValidationError is not null)
            {
                await CleanupFailedInventoryStartAsync(handle, error: null).ConfigureAwait(false);
                return new StartInventoryResult(false, InventoryError.InvalidSettings, antennaValidationError)
                { ErrorCode = PlatformErrorCode.InvalidSettings };
            }

            try
            {
                // 盘存启动沿用设备当前配置；只把平台层的可选天线限制覆盖到一份
                // SDK InventorySettings 副本，绝不通过第二个 Session 重连或重建租约。
                ReaderSettingsSnapshot current = await handle.Session.QuerySettingsAsync(ct).ConfigureAwait(false);
                InventorySettings inventory = InventorySettingsResolver.Resolve(current);
                if (spec.Antennas.Count > 0)
                {
                    ushort[] selectedAntennas = spec.Antennas.ToArray();
                    bool selectsAllAntennas = selectedAntennas.Contains((ushort)0);
                    inventory = inventory with
                    {
                        AntennaIds = selectedAntennas,
                        // A per-antenna RF configuration for an antenna outside the override
                        // is invalid for several Readers (R420 rejects it before ROSpec start).
                        // Keep the SDK's global antenna 0 configuration because it applies to
                        // every selected antenna; retain concrete configurations only for the
                        // requested set. This keeps InventorySpec.Antennas a real override,
                        // without changing the saved Reader settings.
                        AntennaConfigurations = selectsAllAntennas
                            ? inventory.AntennaConfigurations
                            : inventory.AntennaConfigurations
                                .Where(configuration =>
                                    configuration.AntennaId == 0
                                    || selectedAntennas.Contains(configuration.AntennaId))
                                .ToArray(),
                    };
                }

                if (spec.Report is { } report)
                {
                    inventory = inventory with
                    {
                        Report = inventory.Report with
                        {
                            IncludeAntennaId = report.IncludeAntennaId ?? inventory.Report.IncludeAntennaId,
                            IncludeChannelIndex = report.IncludeChannelIndex ?? inventory.Report.IncludeChannelIndex,
                            IncludePeakRssi = report.IncludePeakRssi ?? inventory.Report.IncludePeakRssi,
                            IncludeFirstSeenTimestamp = report.IncludeFirstSeenTimestamp ?? inventory.Report.IncludeFirstSeenTimestamp,
                            IncludeLastSeenTimestamp = report.IncludeLastSeenTimestamp ?? inventory.Report.IncludeLastSeenTimestamp,
                            IncludeTagSeenCount = report.IncludeTagSeenCount ?? inventory.Report.IncludeTagSeenCount,
                            IncludePcBits = report.IncludePcBits ?? inventory.Report.IncludePcBits,
                        },
                    };
                }

                // 先建立运行上下文，再启动 ROSpec。部分 Reader 会在 Start 返回前立即
                // 发出 TagReport；这样首批报告也能归属于本次 InventoryRun 和日志文件。
                if (aggregates.TryGetValue(readerId, out TagAggregateStore? aggregate))
                {
                    aggregate.Clear();
                }

                handle.ActiveRun = new InventoryRunRecord
                {
                    Id = Guid.NewGuid(),
                    ReaderId = readerId,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                };
                string? logFilePath = await StartTagLogQuietlyAsync(handle.ActiveRun).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(logFilePath))
                {
                    handle.ActiveRun = handle.ActiveRun with { LogFilePath = logFilePath };
                }
                handle.AcceptTagReports = true;
                // GPI Stop 是 Reader 配置的一部分。先登记触发器，再调用 SDK，
                // 这样极快的设备事件也会在当前 Start 释放 Gate 后收敛为一次正常 Stop。
                handle.ActiveInventoryStopTrigger = inventory.StopTrigger;
                await handle.Session.StartInventoryAsync(inventory, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await CleanupFailedInventoryStartAsync(
                    handle,
                    error: null,
                    recreateSession: true).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                await CleanupFailedInventoryStartAsync(handle, ex.Message).ConfigureAwait(false);
                return new StartInventoryResult(false, InventoryError.DeviceFailed, ex.Message)
                { ErrorCode = PlatformErrorCode.DeviceFailed };
            }

            handle.InventoryRunning = true;
            if (spec.DurationSeconds is > 0)
            {
                CancelInventoryDuration(handle);
                CancellationTokenSource durationCts = new();
                handle.InventoryDurationCts = durationCts;
                _ = StopInventoryAfterAsync(
                    handle,
                    handle.ActiveRun!,
                    handle.Session,
                    spec.DurationSeconds.Value,
                    durationCts);
            }
            await SaveRunQuietlyAsync(handle.ActiveRun).ConfigureAwait(false);
            handle.Snapshot = handle.Snapshot with { State = ReaderState.Inventorying, Error = null };
            Publish(handle);
            PublishInventoryStarted(handle);
            return new StartInventoryResult(true);
        }
        finally
        {
            handle.Gate.Release();
        }
    }

    public Task StopInventoryAsync(Guid readerId, CancellationToken ct = default) =>
        StopInventoryCoreAsync(readerId, ct, "Manual");

    private async Task StopInventoryCoreAsync(
        Guid readerId,
        CancellationToken ct,
        string stopReason,
        IReaderSession? expectedSession = null,
        InventoryRunRecord? expectedRun = null)
    {
        ReaderHandle handle = await AcquireHandleAsync(readerId, ct).ConfigureAwait(false);
        try
        {
            if ((expectedSession is not null && !ReferenceEquals(handle.Session, expectedSession))
                || (expectedRun is not null && !ReferenceEquals(handle.ActiveRun, expectedRun)))
            {
                // A GPI event is queued off the SDK callback thread. If the originating
                // Session was replaced before the stop task acquired the Gate, the event
                // belongs to the old lifecycle and must not stop the new Inventory lease.
                if (expectedRun is null || ReferenceEquals(handle.ActiveRun, expectedRun))
                {
                    Interlocked.Exchange(ref handle.GpiStopQueued, 0);
                }

                return;
            }

            bool hadInventory = handle.InventoryRunning || handle.ActiveRun is not null;
            if (hadInventory)
            {
                TransitionState(handle, ReaderState.Stopping);
            }

            CancelInventoryDuration(handle);
            ClearActiveInventoryStopTrigger(handle);
            Exception? stopError = null;
            OperationCanceledException? cancellationError = null;
            Exception? disconnectError = null;
            if (handle.InventoryRunning)
            {
                try
                {
                    await handle.Session.StopInventoryAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
                {
                    cancellationError = ex;
                    logger.LogWarning(ex, "Stopping inventory was cancelled for {Id}.", handle.Profile.Id);
                }
                catch (Exception ex)
                {
                    // Stop 失败不能伪装成正常断开：调用方需要知道设备没有确认
                    // ROSpec 已停止。后续仍执行断连和队列排空，防止长连接泄漏。
                    stopError = ex;
                    logger.LogWarning(ex, "Failed to stop inventory for {Id}.", handle.Profile.Id);
                }
            }

            handle.AcceptTagReports = false;
            await DrainTagReportsAsync(handle).ConfigureAwait(false);

            handle.InventoryRunning = false;
            if (handle.Session.IsConnected)
            {
                TransitionState(handle, ReaderState.Disconnecting);
                try
                {
                    await handle.Session.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Stop 的成功语义包含释放 Inventory 长租约。断开失败时不能把
                    // 仍可能持有 TCP 的 Reader 伪装成正常 Disconnected。
                    disconnectError = ex;
                    logger.LogWarning(ex, "Failed to disconnect inventory session for {Id}.", handle.Profile.Id);
                }
            }

            await CompleteRunAsync(
                handle,
                stopError is null && disconnectError is null ? stopReason : "StopFailed").ConfigureAwait(false);

            string? lifecycleError = cancellationError?.Message
                ?? stopError?.Message
                ?? disconnectError?.Message;
            handle.Snapshot = handle.Snapshot with
            {
                State = stopError is null && cancellationError is null && disconnectError is null
                    ? ReaderState.Disconnected
                    : ReaderState.Faulted,
                IsStale = stopError is not null
                    || cancellationError is not null
                    || disconnectError is not null
                    || handle.Snapshot.IsStale,
                Error = lifecycleError,
            };
            if (stopError is not null || cancellationError is not null || disconnectError is not null)
            {
                handle.NeedsExtensionResolution = true;
                handle.SessionNeedsRecreation = true;
            }
            Publish(handle);
            if (hadInventory)
            {
                PublishInventoryStopped(
                    handle,
                    stopError is null && cancellationError is null && disconnectError is null
                        ? MapStopReason(stopReason)
                        : InventoryStopReason.StopFailed,
                    lifecycleError);
            }

            if (cancellationError is not null)
            {
                throw cancellationError;
            }

            if (stopError is not null)
            {
                throw new InvalidOperationException(
                    $"Failed to stop inventory for reader '{handle.Profile.Name}'.",
                    stopError);
            }

            if (disconnectError is not null)
            {
                throw new InvalidOperationException(
                    $"Failed to disconnect inventory for reader '{handle.Profile.Name}'.",
                    disconnectError);
            }
        }
        finally
        {
            handle.Gate.Release();
        }
    }

    public IReadOnlyList<TagObservation> GetTags(Guid readerId) =>
        aggregates.TryGetValue(readerId, out TagAggregateStore? store) ? store.Snapshot() : [];

    public async Task<IReadOnlyList<GpiPortStatus>> GetGpiStatusAsync(
        Guid readerId,
        CancellationToken ct = default)
    {
        ReaderHandle handle = await AcquireHandleAsync(readerId, ct).ConfigureAwait(false);
        try
        {
            if (handle.InventoryRunning)
            {
                throw new ReaderBusyException("Reader busy: inventory is running. Stop inventory first.");
            }

            IReadOnlyList<GpiPortStatus> statuses = await ExecuteShortSessionOperationAsync(
                handle,
                ct,
                () =>
                {
                    ValidateGpiStatusCapability(handle);
                    return handle.Session.GetGpiStatusAsync(ct);
                }).ConfigureAwait(false);
            MergeObservedGpioCounts(handle, gpiStatuses: statuses);
            return statuses;
        }
        finally
        {
            try
            {
                await DisconnectShortOperationAsync(handle).ConfigureAwait(false);
            }
            finally
            {
                handle.Gate.Release();
            }
        }
    }

    public async Task<IReadOnlyList<GpoPortStatus>> GetGpoStatusAsync(
        Guid readerId,
        CancellationToken ct = default)
    {
        ReaderHandle handle = await AcquireHandleAsync(readerId, ct).ConfigureAwait(false);
        try
        {
            if (handle.InventoryRunning)
            {
                throw new ReaderBusyException("Reader busy: inventory is running. Stop inventory first.");
            }

            IReadOnlyList<GpoPortStatus> statuses = await ExecuteShortSessionOperationAsync(
                handle,
                ct,
                () =>
                {
                    ValidateGpoStatusCapability(handle);
                    return handle.Session.GetGpoStatusAsync(ct);
                }).ConfigureAwait(false);
            MergeObservedGpioCounts(handle, gpoStatuses: statuses);
            return statuses;
        }
        finally
        {
            try
            {
                await DisconnectShortOperationAsync(handle).ConfigureAwait(false);
            }
            finally
            {
                handle.Gate.Release();
            }
        }
    }

    public async Task<GpioStatusSnapshot> GetGpioStatusAsync(
        Guid readerId,
        CancellationToken ct = default)
    {
        ReaderHandle handle = await AcquireHandleAsync(readerId, ct).ConfigureAwait(false);
        try
        {
            if (handle.InventoryRunning)
            {
                throw new ReaderBusyException("Reader busy: inventory is running. Stop inventory first.");
            }

            GpioStatusSnapshot statuses = await ExecuteShortSessionOperationAsync(
                handle,
                ct,
                () =>
                {
                    ValidateGpioStatusCapability(handle);
                    return handle.Session.GetGpioStatusAsync(ct);
                }).ConfigureAwait(false);
            MergeObservedGpioCounts(handle, statuses.Gpis, statuses.Gpos);
            return statuses;
        }
        finally
        {
            try
            {
                await DisconnectShortOperationAsync(handle).ConfigureAwait(false);
            }
            finally
            {
                handle.Gate.Release();
            }
        }
    }

    public void ClearTags(Guid readerId)
    {
        if (aggregates.TryGetValue(readerId, out TagAggregateStore? store))
        {
            store.Clear();
        }
    }

    public async Task<Tagging.TagAccessResult> ReadTagMemoryAsync(
        Guid readerId,
        TagReadRequest request,
        CancellationToken ct = default)
    {
        ReaderHandle handle = await AcquireHandleAsync(readerId, ct).ConfigureAwait(false);
        try
        {
            if (handle.InventoryRunning)
            {
                return new Tagging.TagAccessResult(false, "Reader busy: inventory is running. Stop inventory first.")
                { ErrorCode = PlatformErrorCode.ReaderBusy };
            }

            try
            {
                SdkTagAccessMapper.ValidateReadRequest(request);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                return new Tagging.TagAccessResult(false, ex.Message)
                { ErrorCode = PlatformErrorCode.InvalidSettings };
            }

            try
            {
                return await ExecuteShortSessionOperationAsync(
                    handle,
                    ct,
                    async () =>
                    {
                        if (handle.Session.Capabilities?.IsTagAccessAvailable == false)
                        {
                            return new Tagging.TagAccessResult(
                                false,
                                "Reader does not advertise standard Tag Access capability.")
                            { ErrorCode = PlatformErrorCode.Unsupported };
                        }

                        return await handle.Session.ReadTagMemoryAsync(request, ct).ConfigureAwait(false);
                    },
                    static exception => exception is TimeoutException).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException)
            {
                return new Tagging.TagAccessResult(false, "未找到匹配标签，操作超时。")
                { ErrorCode = PlatformErrorCode.NotFound };
            }
            catch (ReaderBusyException ex)
            {
                return new Tagging.TagAccessResult(false, ex.Message)
                { ErrorCode = PlatformErrorCode.ReaderBusy };
            }
            catch (Exception ex)
            {
                return new Tagging.TagAccessResult(false, ex.Message)
                { ErrorCode = PlatformErrorCode.DeviceFailed };
            }
        }
        finally
        {
            try
            {
                await DisconnectShortOperationAsync(handle).ConfigureAwait(false);
            }
            finally
            {
                handle.Gate.Release();
            }
        }
    }

    public async Task<Tagging.TagAccessResult> WriteTagMemoryAsync(
        Guid readerId,
        TagWriteRequest request,
        CancellationToken ct = default)
    {
        ReaderHandle handle = await AcquireHandleAsync(readerId, ct).ConfigureAwait(false);
        try
        {
            if (handle.InventoryRunning)
            {
                return new Tagging.TagAccessResult(false, "Reader busy: inventory is running. Stop inventory first.")
                { ErrorCode = PlatformErrorCode.ReaderBusy };
            }

            try
            {
                SdkTagAccessMapper.ValidateWriteRequest(request);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                return new Tagging.TagAccessResult(false, ex.Message)
                { ErrorCode = PlatformErrorCode.InvalidSettings };
            }

            try
            {
                return await ExecuteShortSessionOperationAsync(
                    handle,
                    ct,
                    async () =>
                    {
                        if (handle.Session.Capabilities?.IsTagAccessAvailable == false)
                        {
                            return new Tagging.TagAccessResult(
                                false,
                                "Reader does not advertise standard Tag Access capability.")
                            { ErrorCode = PlatformErrorCode.Unsupported };
                        }

                        return await handle.Session.WriteTagMemoryAsync(request, ct).ConfigureAwait(false);
                    },
                    static exception => exception is TimeoutException).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException)
            {
                return new Tagging.TagAccessResult(false, "未找到匹配标签，操作超时。")
                { ErrorCode = PlatformErrorCode.NotFound };
            }
            catch (ReaderBusyException ex)
            {
                return new Tagging.TagAccessResult(false, ex.Message)
                { ErrorCode = PlatformErrorCode.ReaderBusy };
            }
            catch (Exception ex)
            {
                return new Tagging.TagAccessResult(false, ex.Message)
                { ErrorCode = PlatformErrorCode.DeviceFailed };
            }
        }
        finally
        {
            try
            {
                await DisconnectShortOperationAsync(handle).ConfigureAwait(false);
            }
            finally
            {
                handle.Gate.Release();
            }
        }
    }

    public async Task SetGpoAsync(Guid readerId, GpioCommand command, CancellationToken ct = default)
    {
        ReaderHandle handle = await AcquireHandleAsync(readerId, ct).ConfigureAwait(false);
        try
        {
            if (handle.InventoryRunning)
            {
                throw new ReaderBusyException("Reader busy: inventory is running. Stop inventory first.");
            }

            ArgumentNullException.ThrowIfNull(command);
            try
            {
                await EnsureShortOperationSessionAsync(handle, ct).ConfigureAwait(false);
                ValidateGpoCommand(handle, command);
                await handle.Session.SetGpoAsync(command.PortNumber, command.State, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (ReaderBusyException)
            {
                throw;
            }
            catch (PlatformOperationException)
            {
                // 明确的能力/平台边界错误不代表 TCP Session 已损坏，保留稳定错误码并
                // 让 finally 的短租约断开逻辑正常执行。
                throw;
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // 本地能力边界/输入校验失败不代表 Reader 连接已经损坏。
                throw;
            }
            catch (Exception ex)
            {
                await MarkShortOperationFaultAsync(handle, ex).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            try
            {
                await DisconnectShortOperationAsync(handle).ConfigureAwait(false);
            }
            finally
            {
                handle.Gate.Release();
            }
        }
    }

    // ---------- TagReport 聚合（有界 Channel，防卡死） ----------

    private void OnSessionTagReported(ReaderHandle handle, IReaderSession source, SdkTagReportEventArgs args)
    {
        InventoryRunRecord? activeRun = handle.ActiveRun;
        if (!ReferenceEquals(handle.Session, source) || !handle.AcceptTagReports || activeRun is null)
        {
            return;
        }

        Interlocked.Increment(ref handle.PendingTagReports);
        if (!tagChannel.Writer.TryWrite(new TagWorkItem(handle, source, activeRun, args.Report)))
        {
            OnTagReportConsumed(handle);
            long dropped = Interlocked.Increment(ref tagsDropped);
            logger.LogWarning("Tag report dropped (consumer saturated); total dropped: {Dropped}", dropped);
        }
    }

    private void OnSessionGpiChanged(ReaderHandle handle, IReaderSession source, SdkGpiChangedEventArgs args)
    {
        if (!ReferenceEquals(handle.Session, source))
        {
            return;
        }

        // 先发布输入状态，保证 UI 看到 GPI 物理变化后再收到由它触发的
        // Inventory Stopped 生命周期事实。
        var status = new GpiPortStatus
        {
            PortNumber = args.PortNumber,
            Configured = true,
            State = args.State,
            Timestamp = args.Timestamp,
        };
        logger.LogInformation(
            "GPI state changed for reader {ReaderId}: port {PortNumber}, state {State}, timestamp {Timestamp}.",
            handle.Profile.Id,
            status.PortNumber,
            status.State,
            status.Timestamp);
        PublishGpiChanged(handle, status);

        InventoryStopTrigger? stopTrigger = handle.ActiveInventoryStopTrigger;
        InventoryRunRecord? activeRun = handle.ActiveRun;
        if (stopTrigger is { Type: InventoryStopTriggerType.GpiWithTimeout }
            && stopTrigger.GpiPortNumber == args.PortNumber
            && stopTrigger.GpiState == args.State
            && (handle.InventoryRunning || activeRun is not null)
            && Interlocked.Exchange(ref handle.GpiStopQueued, 1) == 0)
        {
            logger.LogInformation(
                "GPI stop trigger matched for reader {ReaderId}: port {PortNumber}, state {State}.",
                handle.Profile.Id,
                args.PortNumber,
                args.State);
            QueueGpiTriggeredStop(handle, source, activeRun);
        }
    }

    private void QueueGpiTriggeredStop(
        ReaderHandle handle,
        IReaderSession source,
        InventoryRunRecord? expectedRun) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await StopInventoryCoreAsync(
                    handle.Profile.Id,
                    CancellationToken.None,
                    "Gpi",
                    source,
                    expectedRun)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "GPI-triggered inventory stop failed for {Id}.", handle.Profile.Id);
            }
        });

    private async Task ConsumeTagReportsAsync()
    {
        try
        {
            await foreach (TagWorkItem first in tagChannel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                var batch = new List<TagWorkItem>(TagPublishBatchSize) { first };
                while (batch.Count < TagPublishBatchSize && tagChannel.Reader.TryRead(out TagWorkItem next))
                {
                    batch.Add(next);
                }

                try
                {
                    await ProcessTagBatchAsync(batch).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to process an inventory report batch.");
                }
                finally
                {
                    foreach (TagWorkItem item in batch)
                    {
                        OnTagReportConsumed(item.Handle);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task<string?> SaveSnapshotQuietlyAsync(InventoryRunSnapshot snapshot)
    {
        try
        {
            return await snapshotStore.SaveAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist final inventory snapshot for run {RunId}.", snapshot.Run.Id);
            return null;
        }
    }

    private async Task ProcessTagBatchAsync(IReadOnlyList<TagWorkItem> batch)
    {
        var latestByReaderAndEpc = new Dictionary<
            (Guid ReaderId, string Epc),
            (ReaderHandle Handle, TagAggregateStore Store, string Epc)>();
        foreach (TagWorkItem item in batch)
        {
            // REMOVE 已移除的 reader，Session/InventoryRun 已切换：丢弃已入队
            // 的旧报告，避免重新 Add 或下一次寻卡时误并入新的生命周期。
            if (!readers.ContainsKey(item.Handle.Profile.Id)
                || !ReferenceEquals(item.Handle.Session, item.Source)
                || !ReferenceEquals(item.Handle.ActiveRun, item.Run))
            {
                continue;
            }

            try
            {
                TagAggregateStore store = aggregates.GetOrAdd(item.Handle.Profile.Id, static _ => new TagAggregateStore());
                ReaderTagReportProjection projection = ProjectTagReport(item.Handle, item.Report);
                // Apply the raw report without allocating a TagObservation snapshot. The UI only
                // needs the latest cumulative state for each Reader/EPC at the end of this batch;
                // snapshotting every report creates avoidable allocations and GC pressure when a
                // Reader is configured for ReportEveryNTags=1.
                string epc = store.Apply(item.Report, projection.TidHex, projection.Fields);
                if (item.Handle.ActiveRun is InventoryRunRecord run
                    && run.LogFilePath is not null)
                {
                    // TagLog intentionally preserves one cumulative observation per raw report.
                    // This is the explicit full-fidelity logging path; the normal UI path below
                    // remains batch-coalesced.
                    if (store.TrySnapshot(epc, out TagObservation? logTag)
                        && logTag is not null)
                    {
                        await EnqueueTagLogAsync(item.Handle, run, logTag).ConfigureAwait(false);
                    }
                }

                // The UI needs the newest cumulative observation for each Reader/EPC,
                // not one notification for every raw air-protocol read in this batch.
                latestByReaderAndEpc[(item.Handle.Profile.Id, epc)] = (item.Handle, store, epc);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to aggregate a tag report for reader {Name}.",
                    item.Handle.Profile.Name);
            }
        }

        foreach ((ReaderHandle handle, TagAggregateStore store, string epc) in latestByReaderAndEpc.Values)
        {
            if (handle.AcceptTagReports
                && handle.ActiveRun is not null
                && store.TrySnapshot(epc, out TagObservation? tag)
                && tag is not null)
            {
                PublishTagObserved(handle, tag);
            }
        }
    }

    private ReaderTagReportProjection ProjectTagReport(ReaderHandle handle, TagReport report)
    {
        string? tid = null;
        Dictionary<string, string>? fields = null;
        foreach (IReaderExtensionModule extension in handle.Extensions)
        {
            try
            {
                ReaderTagReportProjection projection = extension.ProjectTagReport(report);
                if (string.IsNullOrWhiteSpace(tid) && !string.IsNullOrWhiteSpace(projection.TidHex))
                {
                    tid = projection.TidHex;
                }

                foreach ((string key, string value) in projection.Fields)
                {
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        continue;
                    }

                    fields ??= new Dictionary<string, string>(StringComparer.Ordinal);
                    fields[key] = value;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to project TagReport extension {ExtensionId} for reader {Name}.",
                    extension.Id, handle.Profile.Name);
            }
        }

        return new ReaderTagReportProjection
        {
            TidHex = tid,
            Fields = fields ?? ReaderTagReportProjection.EmptyFields,
        };
    }

    // ---------- 内部辅助 ----------

    /// <summary>
    /// 在获取 Reader Gate 前短暂持有注册表 Gate，避免 Remove 已经取得注册表锁后，
    /// 旧调用才开始等待并最终拿到已释放的 Session。
    /// </summary>
    private async Task<ReaderHandle> AcquireHandleAsync(Guid readerId, CancellationToken ct)
    {
        ThrowIfDisposing();
        await registryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposing();
            ReaderHandle handle = Get(readerId);
            await handle.Gate.WaitAsync(ct).ConfigureAwait(false);
            return handle;
        }
        finally
        {
            registryGate.Release();
        }
    }

    private async Task EnsureConnectedAsync(ReaderHandle handle, CancellationToken ct)
    {
        if (!handle.Session.IsConnected)
        {
            await handle.Session.ConnectAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Prepare a short operation for a restored or faulted Reader. Short operations
    /// must be able to recover the standard/extension session themselves; opening
    /// the Settings page is not a prerequisite for Tag Access or GPIO.
    /// </summary>
    private async Task EnsureShortOperationSessionAsync(ReaderHandle handle, CancellationToken ct)
    {
        try
        {
            await ResolveExtensionsFromProbeAsync(handle, ct).ConfigureAwait(false);
            await EnsureConnectedAsync(handle, ct).ConfigureAwait(false);
            await ResolveExtensionsFromConnectedIdentityAsync(handle, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await ResetCancelledSessionAsync(handle).ConfigureAwait(false);
            throw;
        }

        if (handle.Snapshot.IsStale || handle.Snapshot.CapabilityRevision == 0)
        {
            CaptureCapabilities(handle, ReaderState.Connected);
            Publish(handle);
        }
    }

    /// <summary>
    /// Execute one operation on the short Session lease. SDK implementations do not all
    /// raise ConnectionFaulted before propagating a transport exception, so a failed
    /// operation must converge the runtime snapshot itself rather than leaving a stale
    /// Connected/capability state for the next consumer call.
    /// </summary>
    private async Task<T> ExecuteShortSessionOperationAsync<T>(
        ReaderHandle handle,
        CancellationToken ct,
        Func<Task<T>> operation,
        Func<Exception, bool>? isExpectedFailure = null)
    {
        try
        {
            await EnsureShortOperationSessionAsync(handle, ct).ConfigureAwait(false);
            return await operation().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ReaderBusyException)
        {
            throw;
        }
        catch (PlatformOperationException)
        {
            // 明确的能力/平台边界错误不代表 TCP Session 已损坏；保留稳定错误码，
            // 让调用方看到 Unsupported/InvalidSettings，而不是错误地进入 Faulted。
            throw;
        }
        catch (Exception ex) when (isExpectedFailure?.Invoke(ex) == true)
        {
            // Tag Access 未找到目标时由 SDK 以 TimeoutException 结束。这是一次正常的
            // 业务失败，不代表 TCP Session 或 Reader 能力已经损坏。
            throw;
        }
        catch (Exception ex)
        {
            await MarkShortOperationFaultAsync(handle, ex).ConfigureAwait(false);
            throw;
        }
    }

    private async Task MarkShortOperationFaultAsync(ReaderHandle handle, Exception error)
    {
        await TryDisconnectQuietlyAsync(handle, CancellationToken.None).ConfigureAwait(false);
        handle.NeedsExtensionResolution = true;
        handle.SessionNeedsRecreation = true;
        await RecreateSessionAfterFaultAsync(handle).ConfigureAwait(false);
        handle.Snapshot = handle.Snapshot with
        {
            State = ReaderState.Faulted,
            IsStale = true,
            Error = error.Message,
        };
        Publish(handle);
    }

    /// <summary>
    /// 连接建立、标准 Probe 或扩展 Session 替换阶段被取消时，调用方虽然会收到
    /// OperationCanceledException，但当前 Session 可能已经处于半开或未知状态。
    /// 取消路径必须像故障路径一样回收并重建一个干净的标准 Session；下一次操作
    /// 再根据新的 Probe 结果匹配扩展，不能复用这次被取消的传输对象。
    /// </summary>
    private async Task ResetCancelledSessionAsync(ReaderHandle handle)
    {
        await TryDisconnectQuietlyAsync(handle, CancellationToken.None).ConfigureAwait(false);
        handle.NeedsExtensionResolution = true;
        handle.SessionNeedsRecreation = true;
        await RecreateSessionAfterFaultAsync(handle).ConfigureAwait(false);
        handle.Snapshot = handle.Snapshot with
        {
            State = ReaderState.Disconnected,
            IsStale = true,
            Error = null,
        };
        Publish(handle);
    }

    private async Task DisconnectShortOperationAsync(ReaderHandle handle)
    {
        if (!handle.InventoryRunning && handle.Session.IsConnected)
        {
            TransitionState(handle, ReaderState.Disconnecting);
            try
            {
                await handle.Session.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 短操作已经可能在设备侧完成；但 TCP 租约没有可靠释放，不能继续
                // 对外宣称 Connected/能力新鲜，否则下一次操作会复用不确定的会话。
                handle.NeedsExtensionResolution = true;
                handle.SessionNeedsRecreation = true;
                await RecreateSessionAfterFaultAsync(handle).ConfigureAwait(false);
                handle.Snapshot = handle.Snapshot with
                {
                    State = ReaderState.Faulted,
                    IsStale = true,
                    Error = ex.Message,
                };
                Publish(handle);
            }
        }

        if (!handle.InventoryRunning
            && !handle.Session.IsConnected
            && handle.Snapshot.State is ReaderState.Connected or ReaderState.Disconnecting)
        {
            handle.Snapshot = handle.Snapshot with { State = ReaderState.Disconnected };
            Publish(handle);
        }
    }

    private async Task<ReaderProbeResult> ProbeCoreAsync(ReaderProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();

        IReaderSession? session = null;
        try
        {
            // Builder/SDK 会话构造本身也可能因为地址、协议策略或扩展配置失败。
            // Probe 的公开契约必须把这类失败投影为统一的 ProbeResult，不能让添加页
            // 收到一个绕过 ReaderAddResult 的未观察异常。
            session = sessionFactory.Create(profile);
            await session.ConnectAsync(ct).ConfigureAwait(false);
            ReaderIdentity? identity = session.Identity;
            var result = new ReaderProbeResult(
                identity is null ? null : $"{identity.ManufacturerId}:{identity.ModelId}",
                identity?.FirmwareVersion,
                null,
                identity?.ManufacturerId,
                identity?.ModelId,
                ToContractProtocolVersion(session.NegotiatedVersion));
            if (identity is null)
            {
                return result;
            }

            IReadOnlyList<string> matchedExtensions = GetApplicableExtensions(
                    ReaderProbeInfo.FromIdentity(identity, session.NegotiatedVersion))
                .Select(static extension => extension.Id)
                .ToArray();
            return result with { MatchedExtensionIds = matchedExtensions };
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                throw;
            }

            logger.LogDebug(ex, "Probe failed for {Host}:{Port}.", profile.Host, profile.Port);
            return new ReaderProbeResult(null, null, ex.Message);
        }
        finally
        {
            if (session is not null)
            {
                await TryDisposeQuietlyAsync(session);
            }
        }
    }

    private static LlrpNet.Core.Protocol.LlrpProtocolVersion? ToSdkProtocolVersion(
        LlrpProtocolVersion? version) => version switch
        {
            LlrpProtocolVersion.Version101 => LlrpNet.Core.Protocol.LlrpProtocolVersion.Version101,
            LlrpProtocolVersion.Version11 => LlrpNet.Core.Protocol.LlrpProtocolVersion.Version11,
            _ => null,
        };

    private static LlrpProtocolVersion? ToContractProtocolVersion(
        LlrpNet.Core.Protocol.LlrpProtocolVersion? version) => version switch
        {
            LlrpNet.Core.Protocol.LlrpProtocolVersion.Version101 => LlrpProtocolVersion.Version101,
            LlrpNet.Core.Protocol.LlrpProtocolVersion.Version11 => LlrpProtocolVersion.Version11,
            _ => null,
        };

    private async Task RollbackEnabledAsync(Guid readerId)
    {
        ReaderHandle handle = Get(readerId);
        ReaderProfile updated = handle.Profile with { IsEnabled = false };
        try
        {
            await profileStore.SaveAsync(updated, CancellationToken.None).ConfigureAwait(false);
            handle.Profile = updated;
            handle.Snapshot = handle.Snapshot with { IsEnabled = false };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to roll back IsEnabled for {Id}.", readerId);
            handle.Snapshot = handle.Snapshot with { IsEnabled = false, Error = ex.Message };
        }

        Publish(handle);
    }

    private async Task TryDeleteProfileAsync(Guid readerId)
    {
        try
        {
            await profileStore.DeleteAsync(readerId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete persisted profile {Id}.", readerId);
        }
    }

    private static async Task TryDisconnectQuietlyAsync(ReaderHandle handle, CancellationToken ct)
        => await TryDisconnectQuietlyAsync(handle.Session, ct).ConfigureAwait(false);

    private static async Task TryDisconnectQuietlyAsync(IReaderSession session, CancellationToken ct)
    {
        try
        {
            await session.DisconnectAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // 断开失败不阻塞；dispose 仍会释放传输。
        }
    }

    private static async Task TryDisposeQuietlyAsync(IReaderSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // 释放失败可忽略。
        }
    }

    private static IReadOnlyList<string> GetExtensionIds(
        IReadOnlyList<IReaderExtensionModule>? extensions) =>
        extensions is null
            ? []
            : extensions.Select(static extension => extension.Id).ToArray();

    private ReaderRuntimeSnapshot NextSnapshot(
        ReaderProfile profile,
        bool enabled,
        IReadOnlyList<IReaderExtensionModule>? extensions = null) => new()
        {
            ReaderId = profile.Id,
            Profile = profile,
            State = ReaderState.Disconnected,
            IsEnabled = enabled,
            IsStale = true,
            ActiveExtensionIds = GetExtensionIds(extensions),
        };

    private ReaderCapabilityCapture CaptureCapabilities(ReaderHandle handle, ReaderState state)
    {
        long revision = Interlocked.Increment(ref revisionCounter);
        IReadOnlyList<ReaderAntennaInfo> antennas = ReaderAntennaFactory.FromMaxAntennas(
            handle.Session.Capabilities?.MaxNumberOfAntennas ?? 0);
        ReaderGpioCapabilities? gpio = ReaderGpioCapabilities.From(handle.Session.Capabilities);
        ReaderCapabilityCapture capture = new()
        {
            ReaderId = handle.Profile.Id,
            CapturedAt = DateTimeOffset.UtcNow,
            Model = handle.Session.Identity is null
                ? null
                : $"{handle.Session.Identity.ManufacturerId}:{handle.Session.Identity.ModelId}",
            ManufacturerId = handle.Session.Identity?.ManufacturerId,
            ModelId = handle.Session.Identity?.ModelId,
            Firmware = handle.Session.Identity?.FirmwareVersion,
            NegotiatedProtocolVersion = ToContractProtocolVersion(handle.Session.NegotiatedVersion),
            Revision = revision,
            Antennas = antennas,
            GpiCount = gpio?.GpiCount,
            GpoCount = gpio?.GpoCount,
            ActiveExtensionIds = GetExtensionIds(handle.Extensions),
            FeatureCatalog = BuildFeatureCatalog(handle),
        };
        handle.CapabilityCapture = capture;
        handle.Snapshot = handle.Snapshot with
        {
            State = state,
            Model = capture.Model,
            ManufacturerId = capture.ManufacturerId,
            ModelId = capture.ModelId,
            Firmware = capture.Firmware,
            NegotiatedProtocolVersion = capture.NegotiatedProtocolVersion,
            CapturedAt = capture.CapturedAt,
            CapabilityRevision = revision,
            IsStale = false,
            Antennas = antennas,
            GpiCount = capture.GpiCount,
            GpoCount = capture.GpoCount,
            ActiveExtensionIds = capture.ActiveExtensionIds,
            FeatureCatalog = capture.FeatureCatalog,
            Error = null,
        };
        return capture;
    }

    /// <summary>
    /// 某些标准 Reader 的 General Device Capabilities 不包含 GPIO 数量，
    /// 但 GET/查询状态会返回带端口号的实际配置。未知能力只在成功收到状态后
    /// 补充运行时快照；明确声明为 0 的能力不被查询结果覆盖。
    /// </summary>
    private void MergeObservedGpioCounts(
        ReaderHandle handle,
        IReadOnlyList<GpiPortStatus>? gpiStatuses = null,
        IReadOnlyList<GpoPortStatus>? gpoStatuses = null)
    {
        ushort? gpiCount = MergeObservedPortCount(handle.Snapshot.GpiCount, gpiStatuses);
        ushort? gpoCount = MergeObservedPortCount(handle.Snapshot.GpoCount, gpoStatuses);
        if (gpiCount == handle.Snapshot.GpiCount && gpoCount == handle.Snapshot.GpoCount)
        {
            return;
        }

        if (handle.CapabilityCapture is { } capture)
        {
            handle.CapabilityCapture = capture with
            {
                GpiCount = gpiCount,
                GpoCount = gpoCount,
            };
        }

        handle.Snapshot = handle.Snapshot with
        {
            GpiCount = gpiCount,
            GpoCount = gpoCount,
        };
        Publish(handle);
    }

    private static ushort? MergeObservedPortCount(
        ushort? currentCount,
        IReadOnlyList<GpiPortStatus>? statuses)
    {
        if (currentCount is not null || statuses is null || statuses.Count == 0)
        {
            return currentCount;
        }

        ushort maxPort = statuses.Max(static status => status.PortNumber);
        return maxPort == 0 ? currentCount : maxPort;
    }

    private static ushort? MergeObservedPortCount(
        ushort? currentCount,
        IReadOnlyList<GpoPortStatus>? statuses)
    {
        if (currentCount is not null || statuses is null || statuses.Count == 0)
        {
            return currentCount;
        }

        ushort maxPort = statuses.Max(static status => status.PortNumber);
        return maxPort == 0 ? currentCount : maxPort;
    }

    private ReaderFeatureCatalog BuildFeatureCatalog(ReaderHandle handle)
    {
        var features = new List<Feature>
        {
            ReaderFeatures.StandardSettings,
            ReaderFeatures.StandardInventory,
        };

        ReaderGpioCapabilities? gpio = ReaderGpioCapabilities.From(handle.Session.Capabilities);
        if (gpio is null || gpio.GpiCount > 0)
        {
            features.Add(ReaderFeatures.StandardGpi);
        }

        if (gpio is null || gpio.GpoCount > 0)
        {
            features.Add(ReaderFeatures.StandardGpo);
        }

        // LLRP capabilities explicitly report whether the Reader can execute
        // C1G2 access operations. Keep the feature visible while capability
        // discovery is unknown (for example, a test double or a legacy SDK
        // session), but do not advertise it when the device reports false.
        if (handle.Session.Capabilities?.IsTagAccessAvailable != false)
        {
            features.Add(ReaderFeatures.StandardTagAccess);
        }

        if (handle.Session.Capabilities?.RfModes is { Count: > 0 })
        {
            features.Add(ReaderFeatures.StandardRf);
        }

        if (handle.Session.Capabilities?.HopTables.Any(table => table.Frequencies.Count > 0) == true
            || handle.Session.Capabilities?.TxFrequencies is { Count: > 0 })
        {
            features.Add(ReaderFeatures.StandardFrequency);
        }

        if (handle.Session.Capabilities?.CanDoTagInventoryStateAwareSingulation == true)
        {
            features.Add(ReaderFeatures.StandardStateAwareSingulation);
        }

        ReaderProbeInfo info = ReaderProbeInfo.FromIdentity(
            handle.Session.Identity,
            handle.Session.NegotiatedVersion);
        foreach (IReaderExtensionModule module in handle.Extensions)
        {
            try
            {
                features.AddRange(module.GetFeatures(info));
            }
            catch (Exception ex)
            {
                // Capability contribution is optional. A broken vendor capability
                // reader must not remove the standard settings/inventory baseline.
                logger.LogWarning(
                    ex,
                    "Extension module {ModuleId} failed while building capabilities for Reader {Model}; skipping vendor features.",
                    module.Id,
                    info.Model ?? "unknown");
            }
        }

        return new ReaderFeatureCatalog
        {
            SupportedFeatures = features.Distinct().ToArray(),
        };
    }

    private ReaderHandle Get(Guid readerId) =>
        readers.TryGetValue(readerId, out ReaderHandle? handle)
            ? handle
            : throw new KeyNotFoundException($"Reader '{readerId}' is not registered.");

    private void OnReaderException(
        ReaderHandle handle,
        IReaderSession source,
        ReaderDeviceExceptionEventArgs args)
    {
        if (!ReferenceEquals(handle.Session, source))
        {
            return;
        }

        QueueConnectionFault(handle, source, "ReaderException", args.Message);
    }

    private IReadOnlyList<IReaderExtensionModule> GetApplicableExtensions(ReaderProbeInfo probeInfo)
    {
        var applicable = new List<IReaderExtensionModule>(extensionModules.Count);
        foreach (IReaderExtensionModule module in extensionModules)
        {
            try
            {
                if (module.IsApplicable(probeInfo))
                {
                    applicable.Add(module);
                }
            }
            catch (Exception ex)
            {
                // A faulty optional matcher must not turn a standard Probe into a
                // device failure. The module is skipped for this identity and the
                // standard L1/L2 path remains available.
                logger.LogWarning(
                    ex,
                    "Extension module {ModuleId} failed while matching Reader {Model}; skipping the module.",
                    module.Id,
                    probeInfo.Model ?? "unknown");
            }
        }

        return applicable;
    }

    private void AttachSessionEvents(ReaderHandle handle)
    {
        IReaderSession source = handle.Session;
        source.ReaderExceptionOccurred += (_, args) => OnReaderException(handle, source, args);
        source.ConnectionFaulted += (_, args) => OnConnectionFaulted(handle, source, args);
        source.DeviceInitiatedClosed += (_, _) => OnDeviceInitiatedClosed(handle, source);
        source.TagReported += (_, args) => OnSessionTagReported(handle, source, args);
        source.GpiChanged += (_, args) => OnSessionGpiChanged(handle, source, args);
    }

    private async Task ResolveExtensionsFromProbeAsync(ReaderHandle handle, CancellationToken ct)
    {
        if (!handle.NeedsExtensionResolution)
        {
            return;
        }

        ReaderProbeResult probe = await ProbeCoreAsync(handle.Profile, ct).ConfigureAwait(false);
        if (!probe.Succeeded)
        {
            if (handle.SessionNeedsRecreation)
            {
                await RecreateSessionAfterFaultAsync(handle).ConfigureAwait(false);
            }

            return;
        }

        IReadOnlyList<IReaderExtensionModule> applicable = GetApplicableExtensions(
            new ReaderProbeInfo(
                probe.ManufacturerId,
                probe.ModelId,
                probe.Firmware,
                probe.Model,
                ToSdkProtocolVersion(probe.NegotiatedProtocolVersion)));
        bool wasConnected = handle.Session.IsConnected;
        await ReplaceSessionAsync(
            handle,
            applicable,
            wasConnected,
            ct,
            force: handle.SessionNeedsRecreation).ConfigureAwait(false);
        handle.NeedsExtensionResolution = false;
        handle.SessionNeedsRecreation = false;
    }

    private async Task ResolveExtensionsFromConnectedIdentityAsync(ReaderHandle handle, CancellationToken ct)
    {
        if (!handle.NeedsExtensionResolution || handle.Session.Identity is null)
        {
            return;
        }

        IReadOnlyList<IReaderExtensionModule> applicable = GetApplicableExtensions(
            ReaderProbeInfo.FromIdentity(handle.Session.Identity, handle.Session.NegotiatedVersion));
        await ReplaceSessionAsync(
            handle,
            applicable,
            wasConnected: true,
            ct,
            force: handle.SessionNeedsRecreation).ConfigureAwait(false);
        handle.NeedsExtensionResolution = false;
        handle.SessionNeedsRecreation = false;
    }

    private async Task ReplaceSessionAsync(
        ReaderHandle handle,
        IReadOnlyList<IReaderExtensionModule> extensions,
        bool wasConnected,
        CancellationToken ct,
        bool force = false)
    {
        if (!force && SameExtensions(handle.Extensions, extensions))
        {
            return;
        }

        IReaderSession replacement = sessionFactory.Create(handle.Profile, extensions);
        IReaderSession previous = handle.Session;

        // 先切换当前引用，再关闭旧连接。某些传输实现会在本地断开时异步抛出
        // DeviceInitiatedClosed；此时旧事件必须立即被 source 守卫识别为过期事件。
        handle.Session = replacement;
        handle.Extensions = extensions;
        handle.Snapshot = handle.Snapshot with
        {
            ActiveExtensionIds = GetExtensionIds(extensions),
        };
        AttachSessionEvents(handle);

        if (wasConnected)
        {
            await TryDisconnectQuietlyAsync(previous, ct).ConfigureAwait(false);
        }

        await TryDisposeQuietlyAsync(previous).ConfigureAwait(false);

        if (wasConnected)
        {
            await replacement.ConnectAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task RecreateSessionAfterFaultAsync(ReaderHandle handle)
    {
        try
        {
            await ReplaceSessionAsync(
                handle,
                // 故障后必须先回到标准会话。当前扩展可能属于已经被替换的
                // Reader；只有新 Probe/Connected Identity 成功后，才能重新匹配模块。
                [],
                wasConnected: false,
                CancellationToken.None,
                force: true).ConfigureAwait(false);
            handle.SessionNeedsRecreation = false;
        }
        catch (Exception ex)
        {
            // Session factory failure is retained in the Faulted snapshot by the caller;
            // the next explicit Activate/Start can retry creating a clean session.
            logger.LogWarning(ex, "Failed to recreate faulted Reader session for {Id}.", handle.Profile.Id);
        }
    }

    private static bool SameExtensions(
        IReadOnlyList<IReaderExtensionModule> left,
        IReadOnlyList<IReaderExtensionModule> right) =>
        left.Select(module => module.Id).SequenceEqual(right.Select(module => module.Id), StringComparer.Ordinal);

    private void OnDeviceInitiatedClosed(ReaderHandle handle, IReaderSession source)
    {
        if (!ReferenceEquals(handle.Session, source))
        {
            return;
        }

        QueueConnectionFault(
            handle,
            source,
            "DeviceClosed",
            "Reader closed the connection (device-initiated).");
    }

    private void OnConnectionFaulted(
        ReaderHandle handle,
        IReaderSession source,
        ReaderConnectionFaultedEventArgs args)
    {
        if (!ReferenceEquals(handle.Session, source))
        {
            return;
        }

        QueueConnectionFault(handle, source, "ConnectionFaulted", args.Message);
    }

    /// <summary>
    /// SDK 事件可能运行在协议消息泵线程。故障收敛包含 Stop/Drain/Disconnect，
    /// 不能在事件回调线程同步开始这些控制操作，否则设备异常时可能阻塞 KEEPALIVE
    /// 和后续协议消息处理。
    /// </summary>
    private void QueueConnectionFault(
        ReaderHandle handle,
        IReaderSession source,
        string reason,
        string error) =>
        _ = Task.Run(() => HandleConnectionFaultAsync(handle, source, reason, error));

    private async Task HandleConnectionFaultAsync(
        ReaderHandle handle,
        IReaderSession source,
        string reason,
        string error)
    {
        try
        {
            await handle.Gate.WaitAsync(CancellationToken.None);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            // Fault handling is queued off the SDK callback thread. The Reader may have
            // replaced the faulted Session before this task gets the Gate; an old event
            // must never disconnect or fault the replacement Session.
            if (!ReferenceEquals(handle.Session, source))
            {
                return;
            }

            // 设备断连：盘存租约随之结束，允许后续重新连接/盘存。
            bool hadInventory = handle.InventoryRunning || handle.ActiveRun is not null;
            bool needsFaultTransition = handle.Snapshot.State is not ReaderState.Faulted || hadInventory;
            if (!needsFaultTransition)
            {
                return;
            }

            CancelInventoryDuration(handle);
            await StopInventorySessionQuietlyAsync(handle, CancellationToken.None).ConfigureAwait(false);
            handle.AcceptTagReports = false;
            await DrainTagReportsAsync(handle).ConfigureAwait(false);
            await CompleteRunAsync(handle, reason).ConfigureAwait(false);
            handle.InventoryRunning = false;

            // ReaderException 可能发生在没有主动断开 TCP 的协议错误路径；无论故障
            // 来源如何，故障收敛完成后都不得把旧 Session 留在 Connected 状态。
            await TryDisconnectQuietlyAsync(handle, CancellationToken.None).ConfigureAwait(false);

            handle.Snapshot = handle.Snapshot with
            {
                State = ReaderState.Faulted,
                IsStale = true,
                Error = error,
            };
            handle.NeedsExtensionResolution = true;
            handle.SessionNeedsRecreation = true;
            Publish(handle);
            if (hadInventory)
            {
                PublishInventoryStopped(handle, MapStopReason(reason), error);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to complete reader fault lifecycle for {Id}.", handle.Profile.Id);
        }
        finally
        {
            try
            {
                handle.Gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Gate 已被 Dispose：忽略。
            }
        }
    }

    private async Task DrainTagReportsAsync(ReaderHandle handle)
    {
        Task waitTask;
        lock (handle.TagDrainGate)
        {
            if (Volatile.Read(ref handle.PendingTagReports) == 0)
            {
                return;
            }

            waitTask = (handle.TagDrainWaiter ??= new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }

        await waitTask.ConfigureAwait(false);
    }

    private async Task<Exception?> StopInventorySessionQuietlyAsync(ReaderHandle handle, CancellationToken ct)
    {
        ClearActiveInventoryStopTrigger(handle);
        if (!handle.InventoryRunning)
        {
            return null;
        }

        try
        {
            await handle.Session.StopInventoryAsync(ct).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to stop inventory for {Id}.", handle.Profile.Id);
            return ex;
        }
    }

    private async Task CleanupFailedInventoryStartAsync(
        ReaderHandle handle,
        string? error,
        bool recreateSession = false)
    {
        ClearActiveInventoryStopTrigger(handle);
        handle.AcceptTagReports = false;
        await DrainTagReportsAsync(handle).ConfigureAwait(false);
        InventoryRunRecord? failedRun = handle.ActiveRun;
        handle.ActiveRun = null;
        if (failedRun is not null)
        {
            await DrainTagLogsAsync(handle).ConfigureAwait(false);
            await CompleteTagLogQuietlyAsync(failedRun).ConfigureAwait(false);
        }

        await TryDisconnectQuietlyAsync(handle, CancellationToken.None).ConfigureAwait(false);
        if (error is not null || recreateSession)
        {
            handle.NeedsExtensionResolution = true;
            handle.SessionNeedsRecreation = true;
            await RecreateSessionAfterFaultAsync(handle).ConfigureAwait(false);
        }
        handle.Snapshot = handle.Snapshot with
        {
            State = error is null ? ReaderState.Disconnected : ReaderState.Faulted,
            IsStale = error is not null || recreateSession || handle.Snapshot.IsStale,
            Error = error,
        };
        Publish(handle);
    }

    private static void OnTagReportConsumed(ReaderHandle handle)
    {
        if (Interlocked.Decrement(ref handle.PendingTagReports) != 0)
        {
            return;
        }

        lock (handle.TagDrainGate)
        {
            if (Volatile.Read(ref handle.PendingTagReports) == 0)
            {
                handle.TagDrainWaiter?.TrySetResult(true);
                handle.TagDrainWaiter = null;
            }
        }
    }

    private void Publish(ReaderHandle handle)
    {
        var args = new ReaderStateChangedEventArgs(handle.Snapshot);
        foreach (Delegate subscriber in StateChanged?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<ReaderStateChangedEventArgs>)subscriber)(this, args);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "A reader state subscriber failed for {Id}.", handle.Profile.Id);
            }
        }
    }

    private void PublishGpiChanged(ReaderHandle handle, GpiPortStatus status)
    {
        var args = new GpiObservedEventArgs(handle.Profile.Id, status);
        foreach (Delegate subscriber in GpiChanged?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<GpiObservedEventArgs>)subscriber)(this, args);
            }
            catch (Exception ex)
            {
                // GPI 状态来自 SDK 回调线程；订阅者异常不能阻断后续的 GPI Stop
                // 匹配和 Reader 生命周期收敛。
                logger.LogWarning(ex, "A GPI state subscriber failed for {Id}.", handle.Profile.Id);
            }
        }
    }

    private void PublishTagObserved(ReaderHandle handle, TagObservation tag)
    {
        var args = new TagObservedEventArgs(handle.Profile.Id, tag);
        foreach (Delegate subscriber in TagObserved?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<TagObservedEventArgs>)subscriber)(this, args);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "A tag observer failed for {Id}.", handle.Profile.Id);
            }
        }
    }

    private void TransitionState(ReaderHandle handle, ReaderState state)
    {
        if (handle.Snapshot.State == state)
        {
            return;
        }

        handle.Snapshot = handle.Snapshot with
        {
            State = state,
            Error = (state is ReaderState.Stopping or ReaderState.Disconnecting)
                && handle.Snapshot.State is not ReaderState.Faulted
                ? null
                : handle.Snapshot.Error,
        };
        Publish(handle);
    }

    private void PublishInventoryStarted(ReaderHandle handle)
    {
        PublishLifecycleChanged(new InventoryLifecycleChangedEventArgs(
            handle.Profile.Id,
            InventoryLifecycleState.Started));
    }

    private void PublishInventoryStopped(
        ReaderHandle handle,
        InventoryStopReason reason,
        string? error = null)
    {
        PublishLifecycleChanged(new InventoryLifecycleChangedEventArgs(
            handle.Profile.Id,
            InventoryLifecycleState.Stopped,
            reason,
            error));
    }

    private void PublishLifecycleChanged(InventoryLifecycleChangedEventArgs args)
    {
        foreach (Delegate subscriber in LifecycleChanged?.GetInvocationList() ?? [])
        {
            try
            {
                ((EventHandler<InventoryLifecycleChangedEventArgs>)subscriber)(this, args);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "An inventory lifecycle subscriber failed for {Id}.", args.ReaderId);
            }
        }
    }

    private static InventoryStopReason MapStopReason(string reason) => reason switch
    {
        "Gpi" => InventoryStopReason.Gpi,
        "Duration" => InventoryStopReason.Duration,
        "DeviceClosed" => InventoryStopReason.DeviceDisconnected,
        "ConnectionFaulted" => InventoryStopReason.ConnectionFaulted,
        "ReaderException" => InventoryStopReason.ReaderException,
        "Removed" => InventoryStopReason.Removed,
        "Deactivated" => InventoryStopReason.Deactivated,
        "ApplicationExit" => InventoryStopReason.ApplicationExit,
        _ => InventoryStopReason.Manual,
    };

    public ValueTask DisposeAsync()
    {
        lock (disposeSync)
        {
            disposeTask ??= DisposeCoreAsync();
            return new ValueTask(disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref disposeStarted, 1);
        await registryGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // 注册表 Gate 必须覆盖整个 Reader 清理窗口：已经进入的操作可以先释放
            // 自己的 Reader Gate，尚未进入的 Add/Probe/短操作不能在清理期间创建或
            // 获取新 Session。否则 readers.Clear() 后仍可能注册一个没人负责释放的会话。
            foreach (ReaderHandle handle in readers.Values.ToArray())
            {
                try
                {
                    await handle.Gate.WaitAsync(CancellationToken.None);
                }
                catch (ObjectDisposedException)
                {
                    continue;
                }

                try
                {
                    bool hadInventory = handle.InventoryRunning || handle.ActiveRun is not null;
                    CancelInventoryDuration(handle);
                    await StopInventorySessionQuietlyAsync(handle, CancellationToken.None).ConfigureAwait(false);
                    handle.AcceptTagReports = false;
                    await DrainTagReportsAsync(handle).ConfigureAwait(false);
                    await CompleteRunAsync(handle, "ApplicationExit").ConfigureAwait(false);
                    handle.InventoryRunning = false;
                    if (hadInventory)
                    {
                        PublishInventoryStopped(handle, InventoryStopReason.ApplicationExit);
                    }
                    if (handle.Session.IsConnected)
                    {
                        await TryDisconnectQuietlyAsync(handle, CancellationToken.None);
                    }

                    try
                    {
                        await handle.Session.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        // 应用退出必须继续清理其他 Reader；单个 SDK Session 的 Dispose
                        // 异常不能让剩余 Reader、Channel 和 DI 容器释放被截断。
                        logger.LogWarning(ex, "Failed to dispose Reader session for {Id} during application exit.", handle.Profile.Id);
                    }
                }
                catch (Exception ex)
                {
                    // 退出阶段不因某个 Reader 的 SDK/日志清理异常而跳过其它 Reader。
                    logger.LogWarning(ex, "Failed to complete Reader cleanup for {Id} during application exit.", handle.Profile.Id);
                }
                finally
                {
                    try
                    {
                        handle.Gate.Release();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Gate 已由并发 Remove 清理。
                    }
                    handle.Gate.Dispose();
                }
            }

            readers.Clear();
            aggregates.Clear();
        }
        finally
        {
            registryGate.Release();
            registryGate.Dispose();
        }

        tagChannel.Writer.TryComplete();
        try
        {
            await tagConsumer.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Consumer shut down with the manager.
        }

        tagLogChannel.Writer.TryComplete();
        try
        {
            await tagLogConsumer.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Log consumer shut down with the manager.
        }
    }

    private void ThrowIfDisposing()
    {
        if (Volatile.Read(ref disposeStarted) != 0)
        {
            throw new ObjectDisposedException(nameof(ReaderManager));
        }
    }

    private sealed class ReaderHandle
    {
        public ReaderHandle(
            ReaderProfile profile,
            IReaderSession session,
            ReaderRuntimeSnapshot snapshot,
            IReadOnlyList<IReaderExtensionModule>? extensions = null,
            bool needsExtensionResolution = false)
        {
            Profile = profile;
            Session = session;
            Snapshot = snapshot;
            Extensions = extensions ?? [];
            NeedsExtensionResolution = needsExtensionResolution;
        }

        public ReaderProfile Profile { get; set; }
        public IReaderSession Session { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ReaderRuntimeSnapshot Snapshot { get; set; }
        public IReadOnlyList<IReaderExtensionModule> Extensions { get; set; }
        public bool NeedsExtensionResolution { get; set; }
        public bool SessionNeedsRecreation { get; set; }
        public ReaderCapabilityCapture? CapabilityCapture { get; set; }
        public bool InventoryRunning { get; set; }
        public bool AcceptTagReports { get; set; }
        public int PendingTagReports;
        public object TagDrainGate { get; } = new();
        public TaskCompletionSource<bool>? TagDrainWaiter { get; set; }
        public int PendingTagLogs;
        public object TagLogGate { get; } = new();
        public TaskCompletionSource<bool>? TagLogWaiter { get; set; }
        public CancellationTokenSource? InventoryDurationCts;
        public InventoryStopTrigger? ActiveInventoryStopTrigger { get; set; }
        public int GpiStopQueued;
        public InventoryRunRecord? ActiveRun { get; set; }
    }

    private readonly record struct TagWorkItem(
        ReaderHandle Handle,
        IReaderSession Source,
        InventoryRunRecord Run,
        TagReport Report);

    private readonly record struct TagLogWorkItem(
        ReaderHandle Handle,
        InventoryRunRecord Run,
        TagObservation Tag);

    /// <summary>平台标签聚合：按 EPC 去重合并（循环后 close on line）。</summary>
    private sealed class TagAggregateStore
    {
        private readonly object gate = new();
        private readonly Dictionary<string, MutableTag> tags = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Applies one raw report in place and returns its EPC. No public snapshot is allocated;
        /// callers should snapshot only the EPCs that need to cross a consumer boundary.
        /// </summary>
        public string Apply(
            LlrpSdk.TagReport report,
            string? tid = null,
            IReadOnlyDictionary<string, string>? extensionFields = null)
        {
            string epc = report.ElectronicProductCode is { Length: > 0 }
                ? Convert.ToHexString(report.ElectronicProductCode.Span)
                : string.Empty;
            DateTimeOffset now = ToUtcTimestamp(report.LastSeen) ?? DateTimeOffset.UtcNow;
            DateTimeOffset firstSeen = ToUtcTimestamp(report.FirstSeen) ?? now;

            lock (gate)
            {
                if (!tags.TryGetValue(epc, out MutableTag? tag))
                {
                    tag = new MutableTag(epc, firstSeen);
                    tags.Add(epc, tag);
                }

                tag.ReadCount += Math.Max(1, (int)(report.SeenCount ?? 1));
                if (report.PcBits is ushort pc)
                {
                    tag.PcBits = pc;
                }

                if (!string.IsNullOrWhiteSpace(tid))
                {
                    tag.Tid = tid;
                }

                if (extensionFields is not null)
                {
                    foreach ((string key, string value) in extensionFields)
                    {
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            tag.ExtensionFields[key] = value;
                        }
                    }
                }

                if (firstSeen < tag.FirstSeen)
                {
                    tag.FirstSeen = firstSeen;
                }

                tag.LastSeen = now > tag.LastSeen ? now : tag.LastSeen;
                tag.LastRssi = report.PeakRssi;
                tag.LastChannelIndex = report.ChannelIndex;
                if (report.AntennaId is ushort ant)
                {
                    tag.LastAntenna = ant;
                }

                return epc;
            }
        }

        public bool TrySnapshot(string epc, out TagObservation? observation)
        {
            lock (gate)
            {
                if (tags.TryGetValue(epc, out MutableTag? tag))
                {
                    observation = tag.Snapshot();
                    return true;
                }
            }

            observation = null;
            return false;
        }

        private static DateTimeOffset? ToUtcTimestamp(TagTimestamp? timestamp)
        {
            if (timestamp?.UtcMicroseconds is not ulong micros)
            {
                return null;
            }

            try
            {
                return DateTimeOffset.UnixEpoch.AddTicks(checked((long)micros * 10));
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        public IReadOnlyList<TagObservation> Snapshot()
        {
            lock (gate)
            {
                return tags.Values
                    .Select(static t => t.Snapshot())
                    .OrderByDescending(static t => t.LastSeen)
                    .ToArray();
            }
        }

        public void Clear()
        {
            lock (gate)
            {
                tags.Clear();
            }
        }

        private sealed class MutableTag(string epc, DateTimeOffset firstSeen)
        {
            public string Epc { get; } = epc;
            public long ReadCount { get; set; }
            public string Tid { get; set; } = string.Empty;
            public ushort? PcBits { get; set; }
            public DateTimeOffset FirstSeen { get; set; } = firstSeen;
            public DateTimeOffset LastSeen { get; set; } = firstSeen;
            public sbyte? LastRssi { get; set; }
            public ushort? LastChannelIndex { get; set; }
            public ushort? LastAntenna { get; set; }
            public Dictionary<string, string> ExtensionFields { get; } = new(StringComparer.Ordinal);

            public TagObservation Snapshot() => new()
            {
                Epc = Epc,
                Tid = Tid,
                PcBits = PcBits,
                PcBitsHex = PcBits?.ToString("X4"),
                ReadCount = ReadCount,
                FirstSeen = FirstSeen,
                LastSeen = LastSeen,
                LastRssi = LastRssi,
                LastChannelIndex = LastChannelIndex,
                LastAntenna = LastAntenna,
                ExtensionFields = new Dictionary<string, string>(ExtensionFields, StringComparer.Ordinal),
            };
        }
    }
}
