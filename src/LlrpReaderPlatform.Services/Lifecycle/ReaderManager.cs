using System.Collections.Concurrent;
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
    private const int TagChannelCapacity = 100_000;
    private const int TagLogChannelCapacity = 25_000;

    private readonly IReaderSessionFactory sessionFactory;
    private readonly IReaderProfileStore profileStore;
    private readonly IInventoryRunStore? runStore;
    private readonly IInventoryTagLog tagLog;
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
    private long revisionCounter;
    private long tagsDropped;

    public ReaderManager(
        IReaderSessionFactory sessionFactory,
        IReaderProfileStore? profileStore = null,
        ILogger<ReaderManager>? logger = null,
        IEnumerable<LlrpReaderPlatform.Services.Extensions.IReaderExtensionModule>? extensions = null,
        IInventoryRunStore? runStore = null,
        IInventoryTagLog? tagLog = null)
    {
        this.sessionFactory = sessionFactory;
        this.profileStore = profileStore ?? new InMemoryProfileStore();
        this.runStore = runStore;
        this.tagLog = tagLog ?? new NullInventoryTagLog();
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

    public Task<ReaderProbeResult> ProbeAsync(ReaderProfile profile, CancellationToken ct = default) =>
        ProbeCoreAsync(profile, ct);

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ReaderProfile> profiles = await profileStore.GetAllAsync(ct).ConfigureAwait(false);
        foreach (ReaderProfile profile in profiles)
        {
            ct.ThrowIfCancellationRequested();
            if (readers.ContainsKey(profile.Id))
            {
                continue;
            }

            IReaderSession? restoredSession = null;
            bool registered = false;
            try
            {
                // 启动恢复仍走标准 Probe → 扩展匹配两阶段流程；离线 Reader 也注册到
                // 列表，稍后由用户手动激活，不因启动时网络不可用而丢失配置。
                ReaderProbeResult probe = await ProbeCoreAsync(profile, ct).ConfigureAwait(false);
                var probeInfo = new ReaderProbeInfo(
                    probe.ManufacturerId,
                    probe.ModelId,
                    probe.Firmware,
                    probe.Model,
                    ToSdkProtocolVersion(probe.NegotiatedProtocolVersion));
                IReadOnlyList<IReaderExtensionModule> applicable = GetApplicableExtensions(probeInfo);
                restoredSession = sessionFactory.Create(profile, applicable);
                var handle = new ReaderHandle(
                    profile,
                    restoredSession,
                    NextSnapshot(profile, profile.IsEnabled),
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

                if (profile.IsEnabled)
                {
                    ReaderActivationResult activation = await ActivateAsync(profile.Id, ct).ConfigureAwait(false);
                    if (!activation.Succeeded)
                    {
                        logger.LogWarning("Reader {Id} restored but activation failed: {Error}", profile.Id, activation.Error);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to restore Reader profile {Id}.", profile.Id);
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
        ArgumentNullException.ThrowIfNull(profile);

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

        var persisted = profile with { IsEnabled = enableAfterAdding };
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
                    applicable);
            }

            try
            {
                await profileStore.SaveAsync(persisted, ct).ConfigureAwait(false);
                persistedToStore = true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to persist profile {Id}.", profile.Id);
                return CreateAddResult(ReaderAddStatus.PersistFailed, ex.Message, probe, applicable);
            }

            try
            {
                session = sessionFactory.Create(profile, applicable);
                var handle = new ReaderHandle(persisted, session, NextSnapshot(profile, enabled: enableAfterAdding), applicable);
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
            ReaderActivationResult activation = await ActivateAsync(profile.Id, ct).ConfigureAwait(false);
            if (!activation.Succeeded)
            {
                await RollbackEnabledAsync(profile.Id);
                return CreateAddResult(
                    ReaderAddStatus.ActivationFailed,
                    activation.Error,
                    probe,
                    applicable,
                    profile.Id);
            }
        }

        return CreateAddResult(ReaderAddStatus.Added, null, probe, applicable, profile.Id);
    }

    private static ReaderAddResult CreateAddResult(
        ReaderAddStatus status,
        string? error,
        ReaderProbeResult probe,
        IReadOnlyList<IReaderExtensionModule> extensions,
        Guid? readerId = null) => new(status, error, readerId)
        {
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
                await StopInventorySessionQuietlyAsync(handle, ct).ConfigureAwait(false);
                handle.AcceptTagReports = false;
                await DrainTagReportsAsync(handle).ConfigureAwait(false);
                await CompleteRunAsync(handle, "Removed").ConfigureAwait(false);
                handle.InventoryRunning = false;
                if (handle.Session.IsConnected)
                {
                    await TryDisconnectQuietlyAsync(handle, ct);
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
                return new ReaderActivationResult(false, "Reader busy: inventory is running. Stop inventory first.");
            }

            // 启动恢复时 Reader 可能在 Probe 阶段离线。设备重新在线后，第一次激活必须
            // 再走一次标准 Probe -> 扩展匹配，否则已恢复的标准 Session 会永久跳过厂商扩展。
            try
            {
                await ResolveExtensionsFromProbeAsync(handle, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                handle.Snapshot = handle.Snapshot with { State = ReaderState.Faulted, Error = ex.Message };
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
                await TryDisconnectQuietlyAsync(handle, CancellationToken.None).ConfigureAwait(false);
                handle.Snapshot = handle.Snapshot with { State = ReaderState.Disconnected, Error = null };
                Publish(handle);
                throw;
            }
            catch (Exception ex)
            {
                // 某些传输层可能在握手失败后仍保留半开的 socket；激活失败也必须
                // 走统一清理，避免下一次 Activate/Inventory 复用脏连接。
                await TryDisconnectQuietlyAsync(handle, CancellationToken.None).ConfigureAwait(false);
                handle.Snapshot = handle.Snapshot with { State = ReaderState.Faulted, Error = ex.Message };
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
                await TryDisconnectQuietlyAsync(handle, CancellationToken.None).ConfigureAwait(false);
                handle.Snapshot = handle.Snapshot with { State = ReaderState.Disconnected, Error = null };
                Publish(handle);
                throw;
            }
            catch (Exception ex)
            {
                await TryDisconnectQuietlyAsync(handle, CancellationToken.None).ConfigureAwait(false);
                handle.Snapshot = handle.Snapshot with { State = ReaderState.Faulted, Error = ex.Message };
                Publish(handle);
                return new ReaderActivationResult(false, ex.Message);
            }

            CaptureCapabilities(handle, ReaderState.Connected);
            Publish(handle);

            await TryDisconnectQuietlyAsync(handle, CancellationToken.None);
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
            bool hadInventory = handle.InventoryRunning || handle.ActiveRun is not null;
            CancelInventoryDuration(handle);
            await StopInventorySessionQuietlyAsync(handle, ct).ConfigureAwait(false);
            handle.AcceptTagReports = false;
            await DrainTagReportsAsync(handle).ConfigureAwait(false);
            await CompleteRunAsync(handle, "Deactivated").ConfigureAwait(false);
            handle.InventoryRunning = false;
            if (handle.Session.IsConnected)
            {
                await TryDisconnectQuietlyAsync(handle, ct);
            }

            if (hadInventory)
            {
                PublishInventoryStopped(handle, InventoryStopReason.Deactivated);
            }

            handle.CapabilityCapture = null;
            handle.Snapshot = handle.Snapshot with
            {
                State = ReaderState.Disconnected,
                Model = null,
                Firmware = null,
                CapturedAt = null,
                IsStale = true,
                Error = null,
            };
            Publish(handle);
        }
        finally
        {
            handle.Gate.Release();
        }
    }

    // ---------- Settings runtime (Services 内部 SDK 桥接) ----------

    public async Task<ReaderSettingsRuntimeSnapshot> QueryAsync(Guid readerId, CancellationToken ct = default)
    {
        ReaderHandle handle = await AcquireHandleAsync(readerId, ct).ConfigureAwait(false);
        try
        {
            ThrowIfInventoryRunning(handle);
            await EnsureConnectedAsync(handle, ct).ConfigureAwait(false);
            try
            {
                return await QuerySettingsCoreAsync(handle, ct).ConfigureAwait(false);
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
            await EnsureConnectedAsync(handle, ct).ConfigureAwait(false);
            try
            {
                ReaderSettingsDefaults defaults = await handle.Session.GetDefaultSettingsAsync(ct).ConfigureAwait(false);
                return new ReaderSettingsRuntimeSnapshot(
                    new ReaderSettingsSnapshot(defaults.Settings, ManagedRoSpec: null),
                    handle.Session.Capabilities);
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
            await EnsureConnectedAsync(handle, ct).ConfigureAwait(false);
            try
            {
                ReaderSettingsRuntimeSnapshot current = await QuerySettingsCoreAsync(handle, ct).ConfigureAwait(false);
                ReaderSettings settings = compile(current);
                await handle.Session.ApplySettingsAsync(settings, ct).ConfigureAwait(false);
                // Apply 后在同一 Session 内重新 Query；设备可能会规范化 index、触发器或
                // 扩展字段，只有重新读取成功才把本次操作视为完成。
                _ = await QuerySettingsCoreAsync(handle, ct).ConfigureAwait(false);
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

    private async Task StopInventoryAfterAsync(Guid readerId, int durationSeconds, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(durationSeconds), ct).ConfigureAwait(false);
            await StopInventoryCoreAsync(readerId, CancellationToken.None, "Duration").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Explicit Stop/Deactivate cancels the scheduled end.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Timed inventory stop failed for {Id}.", readerId);
        }
    }

    private static void CancelInventoryDuration(ReaderHandle handle)
    {
        handle.InventoryDurationCts?.Cancel();
        handle.InventoryDurationCts?.Dispose();
        handle.InventoryDurationCts = null;
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
        await SaveRunQuietlyAsync(active with
        {
            EndedAtUtc = DateTimeOffset.UtcNow,
            StopReason = reason,
            UniqueTagCount = tags.Count,
            TotalReadCount = tags.Sum(static x => x.ReadCount),
        }).ConfigureAwait(false);
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
        ReaderHandle handle = await AcquireHandleAsync(readerId, ct).ConfigureAwait(false);
        try
        {
            if (handle.InventoryRunning)
            {
                return new StartInventoryResult(false, InventoryError.ReaderBusy, "Inventory is already running.")
                { ErrorCode = PlatformErrorCode.ReaderBusy };
            }

            // 启动恢复时可能只注册了无厂商扩展的标准 Session。若用户直接从寻卡页
            // 开始盘存，也必须先完成标准 Probe -> 扩展匹配 -> 会话替换，不能依赖用户
            // 先打开设备设置页触发 ActivateAsync。
            try
            {
                await ResolveExtensionsFromProbeAsync(handle, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                handle.Snapshot = handle.Snapshot with { State = ReaderState.Faulted, Error = ex.Message };
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
                    await TryDisconnectQuietlyAsync(handle, CancellationToken.None).ConfigureAwait(false);
                    handle.Snapshot = handle.Snapshot with { State = ReaderState.Disconnected, Error = null };
                    Publish(handle);
                    throw;
                }
                catch (Exception ex)
                {
                    await TryDisconnectQuietlyAsync(handle, CancellationToken.None).ConfigureAwait(false);
                    handle.Snapshot = handle.Snapshot with { State = ReaderState.Faulted, Error = ex.Message };
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
                await TryDisconnectQuietlyAsync(handle, CancellationToken.None).ConfigureAwait(false);
                handle.Snapshot = handle.Snapshot with { State = ReaderState.Disconnected, Error = null };
                Publish(handle);
                throw;
            }
            catch (Exception ex)
            {
                await TryDisconnectQuietlyAsync(handle, CancellationToken.None).ConfigureAwait(false);
                handle.Snapshot = handle.Snapshot with { State = ReaderState.Faulted, Error = ex.Message };
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

            try
            {
                // 盘存启动沿用设备当前配置；只把平台层的可选天线限制覆盖到一份
                // SDK InventorySettings 副本，绝不通过第二个 Session 重连或重建租约。
                ReaderSettingsSnapshot current = await handle.Session.QuerySettingsAsync(ct).ConfigureAwait(false);
                InventorySettings inventory = current.ManagedRoSpec?.Inventory
                    ?? current.Settings.Inventory
                    ?? new InventorySettings();
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
                await CleanupFailedInventoryStartAsync(handle, error: null).ConfigureAwait(false);
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
                handle.InventoryDurationCts = new CancellationTokenSource();
                _ = StopInventoryAfterAsync(readerId, spec.DurationSeconds.Value, handle.InventoryDurationCts.Token);
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

    private async Task StopInventoryCoreAsync(Guid readerId, CancellationToken ct, string stopReason)
    {
        ReaderHandle handle = await AcquireHandleAsync(readerId, ct).ConfigureAwait(false);
        try
        {
            bool hadInventory = handle.InventoryRunning || handle.ActiveRun is not null;
            CancelInventoryDuration(handle);
            ClearActiveInventoryStopTrigger(handle);
            Exception? stopError = null;
            OperationCanceledException? cancellationError = null;
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
            await CompleteRunAsync(handle, stopError is null ? stopReason : "StopFailed").ConfigureAwait(false);

            handle.InventoryRunning = false;
            if (handle.Session.IsConnected)
            {
                await TryDisconnectQuietlyAsync(handle, CancellationToken.None);
            }

            handle.Snapshot = handle.Snapshot with
            {
                State = stopError is null && cancellationError is null
                    ? ReaderState.Disconnected
                    : ReaderState.Faulted,
                Error = cancellationError?.Message ?? stopError?.Message,
            };
            Publish(handle);
            if (hadInventory)
            {
                PublishInventoryStopped(
                    handle,
                    stopError is null && cancellationError is null
                        ? MapStopReason(stopReason)
                        : InventoryStopReason.StopFailed,
                    cancellationError?.Message ?? stopError?.Message);
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

            await EnsureConnectedAsync(handle, ct).ConfigureAwait(false);
            return await handle.Session.GetGpiStatusAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await DisconnectShortOperationAsync(handle).ConfigureAwait(false);
            handle.Gate.Release();
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

            await EnsureConnectedAsync(handle, ct).ConfigureAwait(false);
            return await handle.Session.GetGpoStatusAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await DisconnectShortOperationAsync(handle).ConfigureAwait(false);
            handle.Gate.Release();
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

            await EnsureConnectedAsync(handle, ct).ConfigureAwait(false);
            return await handle.Session.GetGpioStatusAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await DisconnectShortOperationAsync(handle).ConfigureAwait(false);
            handle.Gate.Release();
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

            await EnsureConnectedAsync(handle, ct);
            if (handle.Session.Capabilities?.IsTagAccessAvailable == false)
            {
                return new Tagging.TagAccessResult(
                    false,
                    "Reader does not advertise standard Tag Access capability.")
                { ErrorCode = PlatformErrorCode.Unsupported };
            }

            try
            {
                return await handle.Session.ReadTagMemoryAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new Tagging.TagAccessResult(false, ex.Message)
                { ErrorCode = PlatformErrorCode.DeviceFailed };
            }
        }
        finally
        {
            await DisconnectShortOperationAsync(handle);
            handle.Gate.Release();
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

            await EnsureConnectedAsync(handle, ct);
            if (handle.Session.Capabilities?.IsTagAccessAvailable == false)
            {
                return new Tagging.TagAccessResult(
                    false,
                    "Reader does not advertise standard Tag Access capability.")
                { ErrorCode = PlatformErrorCode.Unsupported };
            }

            try
            {
                return await handle.Session.WriteTagMemoryAsync(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new Tagging.TagAccessResult(false, ex.Message)
                { ErrorCode = PlatformErrorCode.DeviceFailed };
            }
        }
        finally
        {
            await DisconnectShortOperationAsync(handle);
            handle.Gate.Release();
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

            await EnsureConnectedAsync(handle, ct);
            await handle.Session.SetGpoAsync(command.PortNumber, command.State, ct).ConfigureAwait(false);
        }
        finally
        {
            await DisconnectShortOperationAsync(handle);
            handle.Gate.Release();
        }
    }

    // ---------- TagReport 聚合（有界 Channel，防卡死） ----------

    private void OnSessionTagReported(ReaderHandle handle, IReaderSession source, SdkTagReportEventArgs args)
    {
        if (!ReferenceEquals(handle.Session, source) || !handle.AcceptTagReports)
        {
            return;
        }

        Interlocked.Increment(ref handle.PendingTagReports);
        if (!tagChannel.Writer.TryWrite(new TagWorkItem(handle, args.Report)))
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
        GpiChanged?.Invoke(this, new GpiObservedEventArgs(handle.Profile.Id, new GpiPortStatus
        {
            PortNumber = args.PortNumber,
            Configured = true,
            State = args.State,
        }));

        InventoryStopTrigger? stopTrigger = handle.ActiveInventoryStopTrigger;
        if (stopTrigger is { Type: InventoryStopTriggerType.GpiWithTimeout }
            && stopTrigger.GpiPortNumber == args.PortNumber
            && stopTrigger.GpiState == args.State
            && (handle.InventoryRunning || handle.ActiveRun is not null)
            && Interlocked.Exchange(ref handle.GpiStopQueued, 1) == 0)
        {
            QueueGpiTriggeredStop(handle);
        }
    }

    private void QueueGpiTriggeredStop(ReaderHandle handle) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await StopInventoryCoreAsync(handle.Profile.Id, CancellationToken.None, "Gpi")
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
            await foreach (TagWorkItem item in tagChannel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                try
                {
                    // REMOVE 已移除的 reader：丢弃已入队的旧报告，避免重新 Add 时误并入新库。
                    if (!readers.ContainsKey(item.Handle.Profile.Id))
                    {
                        continue;
                    }

                    TagAggregateStore store = aggregates.GetOrAdd(item.Handle.Profile.Id, static _ => new TagAggregateStore());
                    ReaderTagReportProjection projection = ProjectTagReport(item.Handle, item.Report);
                    TagObservation tag = store.Add(item.Report, projection.TidHex, projection.Fields);
                    if (item.Handle.ActiveRun is InventoryRunRecord run
                        && run.LogFilePath is not null)
                    {
                        await EnqueueTagLogAsync(item.Handle, run, tag).ConfigureAwait(false);
                    }
                    TagObserved?.Invoke(this, new TagObservedEventArgs(item.Handle.Profile.Id, tag));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to aggregate a tag report for reader {Name}.",
                        item.Handle.Profile.Name);
                }
                finally
                {
                    OnTagReportConsumed(item.Handle);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
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
        await registryGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
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

    private async Task DisconnectShortOperationAsync(ReaderHandle handle)
    {
        if (!handle.InventoryRunning && handle.Session.IsConnected)
        {
            try
            {
                await handle.Session.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // 短操作后断开失败不阻塞。
            }
        }
    }

    private async Task<ReaderProbeResult> ProbeCoreAsync(ReaderProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();

        IReaderSession session = sessionFactory.Create(profile);
        try
        {
            await session.ConnectAsync(ct).ConfigureAwait(false);
            ReaderIdentity? identity = session.Identity;
            return new ReaderProbeResult(
                identity is null ? null : $"{identity.ManufacturerId}:{identity.ModelId}",
                identity?.FirmwareVersion,
                null,
                identity?.ManufacturerId,
                identity?.ModelId,
                ToContractProtocolVersion(session.NegotiatedVersion));
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
            await TryDisposeQuietlyAsync(session);
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

    private ReaderRuntimeSnapshot NextSnapshot(ReaderProfile profile, bool enabled) => new()
    {
        ReaderId = profile.Id,
        Profile = profile,
        State = ReaderState.Disconnected,
        IsEnabled = enabled,
        IsStale = true,
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
            FeatureCatalog = capture.FeatureCatalog,
            Error = null,
        };
        return capture;
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
            features.AddRange(module.GetFeatures(info));
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

        QueueConnectionFault(handle, "ReaderException", args.Message);
    }

    private IReadOnlyList<IReaderExtensionModule> GetApplicableExtensions(ReaderProbeInfo probeInfo) =>
        extensionModules.Where(module => module.IsApplicable(probeInfo)).ToArray();

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
        await ReplaceSessionAsync(handle, applicable, wasConnected, ct).ConfigureAwait(false);
        handle.NeedsExtensionResolution = false;
    }

    private async Task ResolveExtensionsFromConnectedIdentityAsync(ReaderHandle handle, CancellationToken ct)
    {
        if (!handle.NeedsExtensionResolution || handle.Session.Identity is null)
        {
            return;
        }

        IReadOnlyList<IReaderExtensionModule> applicable = GetApplicableExtensions(
            ReaderProbeInfo.FromIdentity(handle.Session.Identity, handle.Session.NegotiatedVersion));
        await ReplaceSessionAsync(handle, applicable, wasConnected: true, ct).ConfigureAwait(false);
        handle.NeedsExtensionResolution = false;
    }

    private async Task ReplaceSessionAsync(
        ReaderHandle handle,
        IReadOnlyList<IReaderExtensionModule> extensions,
        bool wasConnected,
        CancellationToken ct)
    {
        if (SameExtensions(handle.Extensions, extensions))
        {
            return;
        }

        IReaderSession replacement = sessionFactory.Create(handle.Profile, extensions);
        IReaderSession previous = handle.Session;

        // 先切换当前引用，再关闭旧连接。某些传输实现会在本地断开时异步抛出
        // DeviceInitiatedClosed；此时旧事件必须立即被 source 守卫识别为过期事件。
        handle.Session = replacement;
        handle.Extensions = extensions;
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

        QueueConnectionFault(handle, "ConnectionFaulted", args.Message);
    }

    /// <summary>
    /// SDK 事件可能运行在协议消息泵线程。故障收敛包含 Stop/Drain/Disconnect，
    /// 不能在事件回调线程同步开始这些控制操作，否则设备异常时可能阻塞 KEEPALIVE
    /// 和后续协议消息处理。
    /// </summary>
    private void QueueConnectionFault(ReaderHandle handle, string reason, string error) =>
        _ = Task.Run(() => HandleConnectionFaultAsync(handle, reason, error));

    private async Task HandleConnectionFaultAsync(ReaderHandle handle, string reason, string error)
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
            // 设备断连：盘存租约随之结束，允许后续重新连接/盘存。
            bool hadInventory = handle.InventoryRunning || handle.ActiveRun is not null;
            bool needsFaultTransition = handle.Snapshot.State is not ReaderState.Faulted || hadInventory;
            if (needsFaultTransition)
            {
                CancelInventoryDuration(handle);
                await StopInventorySessionQuietlyAsync(handle, CancellationToken.None).ConfigureAwait(false);
                handle.AcceptTagReports = false;
                await DrainTagReportsAsync(handle).ConfigureAwait(false);
                await CompleteRunAsync(handle, reason).ConfigureAwait(false);
                handle.InventoryRunning = false;
            }

            // ReaderException 可能发生在没有主动断开 TCP 的协议错误路径；无论故障
            // 来源如何，故障收敛完成后都不得把旧 Session 留在 Connected 状态。
            await TryDisconnectQuietlyAsync(handle, CancellationToken.None).ConfigureAwait(false);

            if (needsFaultTransition)
            {
                handle.Snapshot = handle.Snapshot with
                {
                    State = ReaderState.Faulted,
                    Error = error,
                };
                Publish(handle);
                if (hadInventory)
                {
                    PublishInventoryStopped(handle, MapStopReason(reason), error);
                }
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

    private async Task StopInventorySessionQuietlyAsync(ReaderHandle handle, CancellationToken ct)
    {
        ClearActiveInventoryStopTrigger(handle);
        if (!handle.InventoryRunning)
        {
            return;
        }

        try
        {
            await handle.Session.StopInventoryAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to stop inventory for {Id}.", handle.Profile.Id);
        }
    }

    private async Task CleanupFailedInventoryStartAsync(ReaderHandle handle, string? error)
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
        handle.Snapshot = handle.Snapshot with
        {
            State = ReaderState.Disconnected,
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

    private void Publish(ReaderHandle handle) =>
        StateChanged?.Invoke(this, new ReaderStateChangedEventArgs(handle.Snapshot));

    private void PublishInventoryStarted(ReaderHandle handle) =>
        LifecycleChanged?.Invoke(this, new InventoryLifecycleChangedEventArgs(
            handle.Profile.Id,
            InventoryLifecycleState.Started));

    private void PublishInventoryStopped(
        ReaderHandle handle,
        InventoryStopReason reason,
        string? error = null) =>
        LifecycleChanged?.Invoke(this, new InventoryLifecycleChangedEventArgs(
            handle.Profile.Id,
            InventoryLifecycleState.Stopped,
            reason,
            error));

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

                await handle.Session.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                handle.Gate.Release();
                handle.Gate.Dispose();
            }
        }

        readers.Clear();
        registryGate.Dispose();

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
        public ReaderCapabilityCapture? CapabilityCapture { get; set; }
        public bool InventoryRunning { get; set; }
        public bool AcceptTagReports { get; set; }
        public int PendingTagReports;
        public object TagDrainGate { get; } = new();
        public TaskCompletionSource<bool>? TagDrainWaiter { get; set; }
        public int PendingTagLogs;
        public object TagLogGate { get; } = new();
        public TaskCompletionSource<bool>? TagLogWaiter { get; set; }
        public CancellationTokenSource? InventoryDurationCts { get; set; }
        public InventoryStopTrigger? ActiveInventoryStopTrigger { get; set; }
        public int GpiStopQueued;
        public InventoryRunRecord? ActiveRun { get; set; }
    }

    private readonly record struct TagWorkItem(ReaderHandle Handle, TagReport Report);

    private readonly record struct TagLogWorkItem(
        ReaderHandle Handle,
        InventoryRunRecord Run,
        TagObservation Tag);

    /// <summary>平台标签聚合：按 EPC 去重合并（循环后 close on line）。</summary>
    private sealed class TagAggregateStore
    {
        private readonly object gate = new();
        private readonly Dictionary<string, MutableTag> tags = new(StringComparer.OrdinalIgnoreCase);

        public TagObservation Add(
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

                return tag.Snapshot();
            }
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
