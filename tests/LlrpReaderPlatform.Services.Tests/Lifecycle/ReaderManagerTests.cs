using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Contracts.Tagging;
using LlrpReaderPlatform.Services.Lifecycle;
using LlrpReaderPlatform.TestKit;
using LlrpSdk;
using Xunit;

namespace LlrpReaderPlatform.Services.Tests.Lifecycle;

public sealed class ReaderManagerTests
{
    private static ReaderProfile NewProfile() => new()
    {
        Id = Guid.NewGuid(),
        Name = "TestReader",
        Host = "192.0.2.10",
        Port = 5084,
        IsEnabled = false,
    };

    [Fact]
    public async Task AddAsync_without_enable_registers_and_persists()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        await using var manager = new ReaderManager(sessionFactory, store);

        ReaderProfile profile = NewProfile();
        ReaderAddResult result = await manager.AddAsync(profile, enableAfterAdding: false);

        Assert.Equal(ReaderAddStatus.Added, result.Status);
        Assert.NotNull(await store.GetAsync(profile.Id));
        ReaderRuntimeSnapshot snapshot = manager.GetSnapshot(profile.Id);
        Assert.Equal(ReaderState.Disconnected, snapshot.State);
        Assert.False(snapshot.IsEnabled);
        Assert.True(snapshot.IsStale);
    }

    [Fact]
    public async Task ActivateAsync_does_not_advertise_tag_access_when_reader_capability_is_false()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        await using var manager = new ReaderManager(sessionFactory, store);

        ReaderProfile profile = NewProfile();
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe
        FakeSession registered = new();
        registered.SetCapabilities(isTagAccessAvailable: false, gpiCount: 0, gpoCount: 0);
        sessionFactory.Queue.Enqueue(registered);
        ReaderAddResult added = await manager.AddAsync(profile, enableAfterAdding: false);

        Assert.True(added.Succeeded);
        ReaderActivationResult activation = await manager.ActivateAsync(profile.Id);

        Assert.True(activation.Succeeded);
        ReaderRuntimeSnapshot snapshot = manager.GetSnapshot(profile.Id);
        Assert.Equal((ushort)0, snapshot.GpiCount);
        Assert.Equal((ushort)0, snapshot.GpoCount);
        Assert.DoesNotContain(
            ReaderFeatures.StandardTagAccess,
            snapshot.FeatureCatalog.SupportedFeatures);
        Assert.DoesNotContain(
            ReaderFeatures.StandardGpi,
            snapshot.FeatureCatalog.SupportedFeatures);
        Assert.DoesNotContain(
            ReaderFeatures.StandardGpo,
            snapshot.FeatureCatalog.SupportedFeatures);
    }

    [Fact]
    public async Task ActivateAsync_reads_gpio_counts_from_llrp_11_capabilities()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        await using var manager = new ReaderManager(sessionFactory, store);

        ReaderProfile profile = NewProfile();
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe
        FakeSession registered = new();
        registered.SetCapabilities(gpiCount: 3, gpoCount: 2, useProtocol11: true);
        registered.NegotiatedVersion = LlrpNet.Core.Protocol.LlrpProtocolVersion.Version11;
        sessionFactory.Queue.Enqueue(registered);

        ReaderAddResult added = await manager.AddAsync(profile, enableAfterAdding: false);
        Assert.True(added.Succeeded);

        ReaderActivationResult activation = await manager.ActivateAsync(profile.Id);

        Assert.True(activation.Succeeded);
        ReaderRuntimeSnapshot snapshot = manager.GetSnapshot(profile.Id);
        Assert.Equal((ushort)3, snapshot.GpiCount);
        Assert.Equal((ushort)2, snapshot.GpoCount);
        Assert.Equal(LlrpProtocolVersion.Version11, snapshot.NegotiatedProtocolVersion);
        Assert.Contains(ReaderFeatures.StandardGpi, snapshot.FeatureCatalog.SupportedFeatures);
        Assert.Contains(ReaderFeatures.StandardGpo, snapshot.FeatureCatalog.SupportedFeatures);
    }

    [Fact]
    public async Task ProbeAsync_returns_negotiated_protocol_version_without_exposing_sdk_types()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        await using var manager = new ReaderManager(sessionFactory, store);
        sessionFactory.Queue.Enqueue(new FakeSession
        {
            NegotiatedVersion = LlrpNet.Core.Protocol.LlrpProtocolVersion.Version11,
        });

        ReaderProbeResult result = await manager.ProbeAsync(NewProfile());

        Assert.True(result.Succeeded);
        Assert.Equal(LlrpProtocolVersion.Version11, result.NegotiatedProtocolVersion);
    }

    [Fact]
    public async Task InitializeAsync_restores_persisted_reader_without_requiring_it_to_be_online()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        ReaderProfile profile = NewProfile() with { IsEnabled = false };
        await store.SaveAsync(profile);
        sessionFactory.Queue.Enqueue(new FakeSession()); // startup probe
        var restored = new FakeSession();
        sessionFactory.Queue.Enqueue(restored); // registered session
        await using var manager = new ReaderManager(sessionFactory, store);

        await manager.InitializeAsync();

        Assert.Single(manager.Readers);
        Assert.Equal(profile.Id, manager.Readers[0].ReaderId);
        Assert.True(manager.GetSnapshot(profile.Id).IsStale);
        Assert.False(restored.IsConnected);
    }

    [Fact]
    public async Task AddAsync_persists_enable_flag_when_enable_requested()
    {
        var manager = CreateManager(out FakeProfileStore store, out _);

        await manager.AddAsync(NewProfile(), enableAfterAdding: true);

        IReadOnlyList<ReaderProfile> saved = await store.GetAllAsync();
        Assert.Single(saved);
        Assert.True(saved[0].IsEnabled);
    }

    [Fact]
    public async Task AddAsync_probe_failure_returns_ProbeFailed_and_does_not_register()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        await using var manager = new ReaderManager(sessionFactory, store);

        ReaderProfile profile = NewProfile();
        sessionFactory.Factory = _ => new FakeSession { ConnectThrows = new TimeoutException("unreachable") };

        ReaderAddResult result = await manager.AddAsync(profile, enableAfterAdding: false);

        Assert.Equal(ReaderAddStatus.ProbeFailed, result.Status);
        Assert.Empty(await store.GetAllAsync());
        Assert.Empty(manager.Readers);
    }

    [Fact]
    public async Task ProbeAsync_cancellation_propagates_instead_of_becoming_probe_failure()
    {
        var sessionFactory = new FakeSessionFactory
        {
            Factory = _ => new FakeSession { ConnectThrows = new OperationCanceledException() },
        };
        await using var manager = new ReaderManager(sessionFactory, new FakeProfileStore());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => manager.ProbeAsync(NewProfile(), cancellation.Token));
    }

    [Fact]
    public async Task InitializeAsync_cancellation_propagates_instead_of_skipping_restore()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        ReaderProfile profile = NewProfile();
        await store.SaveAsync(profile);
        using var cancellation = new CancellationTokenSource();
        sessionFactory.Factory = _ => new FakeSession
        {
            BeforeConnect = cancellation.Cancel,
            ConnectThrows = new OperationCanceledException(),
        };
        await using var manager = new ReaderManager(sessionFactory, store);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => manager.InitializeAsync(cancellation.Token));
    }

    [Fact]
    public async Task AddAsync_persist_failure_returns_PersistFailed_and_does_not_register()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore { SaveThrows = new IOException("disk full") };
        await using var manager = new ReaderManager(sessionFactory, store);

        ReaderAddResult result = await manager.AddAsync(NewProfile(), enableAfterAdding: false);

        Assert.Equal(ReaderAddStatus.PersistFailed, result.Status);
        Assert.Empty(manager.Readers);
    }

    [Fact]
    public async Task AddAsync_activation_failure_rolls_back_IsEnabled_but_keeps_profile()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        await using var manager = new ReaderManager(sessionFactory, store);

        ReaderProfile profile = NewProfile();
        // probe 用第一个（成功），register 用第二个（激活时连接失败）。
        sessionFactory.Queue.Enqueue(new FakeSession());
        sessionFactory.Queue.Enqueue(new FakeSession { ConnectThrows = new TimeoutException("no reader") });

        ReaderAddResult result = await manager.AddAsync(profile, enableAfterAdding: true);

        Assert.Equal(ReaderAddStatus.ActivationFailed, result.Status);
        Assert.Equal(profile.Id, result.ReaderId);
        // 补偿：profile 保留，但 IsEnabled 回滚为 false。
        ReaderProfile? saved = await store.GetAsync(profile.Id);
        Assert.NotNull(saved);
        Assert.False(saved.IsEnabled);
    }

    [Fact]
    public async Task ActivateAsync_captures_capability_and_ends_disconnected()
    {
        var manager = CreateManager(out _, out FakeSessionFactory sessionFactory);
        ReaderProfile profile = NewProfile();
        var session = new FakeSession();
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe
        sessionFactory.Queue.Enqueue(session);           // register -> handle.Session
        await manager.AddAsync(profile, enableAfterAdding: false);

        ReaderActivationResult result = await manager.ActivateAsync(profile.Id);

        Assert.True(result.Succeeded);
        ReaderRuntimeSnapshot s = manager.GetSnapshot(profile.Id);
        Assert.NotNull(s.CapturedAt);
        Assert.False(s.IsStale);
        Assert.True(s.CapabilityRevision > 0);
        // 短连接：最终断开。
        Assert.Equal(ReaderState.Disconnected, s.State);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task ActivateAsync_rejects_running_inventory_without_replacing_or_disconnecting_session()
    {
        var manager = CreateManager(out _, out FakeSessionFactory sessionFactory);
        ReaderProfile profile = NewProfile();
        var session = new FakeSession();
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe
        sessionFactory.Queue.Enqueue(session);           // register
        await manager.AddAsync(profile, enableAfterAdding: false);
        await manager.StartInventoryAsync(profile.Id, new InventorySpec());

        ReaderActivationResult result = await manager.ActivateAsync(profile.Id);

        Assert.False(result.Succeeded);
        Assert.Contains("inventory is running", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(session.IsConnected);
        Assert.True(session.InventoryRunning);
        Assert.Equal(0, session.DisconnectCount);
        Assert.Equal(ReaderState.Inventorying, manager.GetSnapshot(profile.Id).State);

        await manager.StopInventoryAsync(profile.Id);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task DeactivateAsync_stops_inventory_before_disconnect()
    {
        var manager = CreateManager(out _, out FakeSessionFactory sessionFactory);
        ReaderProfile profile = NewProfile();
        var session = new FakeSession();
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe
        sessionFactory.Queue.Enqueue(session);           // register
        await manager.AddAsync(profile, enableAfterAdding: false);
        await manager.StartInventoryAsync(profile.Id, new InventorySpec());

        await manager.DeactivateAsync(profile.Id);

        Assert.Equal(1, session.StopInventoryCount);
        Assert.False(session.InventoryRunning);
        Assert.False(session.IsConnected);
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task ActivateAsync_connection_failure_sets_Faulted_and_returns_failure()
    {
        var manager = CreateManager(out _, out FakeSessionFactory sessionFactory);
        ReaderProfile profile = NewProfile();
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe ok
        sessionFactory.Queue.Enqueue(new FakeSession { ConnectThrows = new IOException("connect refused") });
        await manager.AddAsync(profile, enableAfterAdding: false);

        ReaderActivationResult result = await manager.ActivateAsync(profile.Id);

        Assert.False(result.Succeeded);
        ReaderRuntimeSnapshot s = manager.GetSnapshot(profile.Id);
        Assert.Equal(ReaderState.Faulted, s.State);
        Assert.NotNull(s.Error);
    }

    [Fact]
    public async Task ActivateAsync_cancellation_cleans_up_and_propagates()
    {
        var manager = CreateManager(out _, out FakeSessionFactory sessionFactory);
        ReaderProfile profile = NewProfile();
        using var cancellation = new CancellationTokenSource();
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe ok
        sessionFactory.Queue.Enqueue(new FakeSession
        {
            BeforeConnect = cancellation.Cancel,
            ConnectThrows = new OperationCanceledException(),
        });
        await manager.AddAsync(profile, enableAfterAdding: false);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => manager.ActivateAsync(profile.Id, cancellation.Token));

        ReaderRuntimeSnapshot snapshot = manager.GetSnapshot(profile.Id);
        Assert.Equal(ReaderState.Disconnected, snapshot.State);
        Assert.Null(snapshot.Error);
    }

    [Fact]
    public async Task RemoveAsync_disposes_session_and_deletes_profile()
    {
        var manager = CreateManager(out FakeProfileStore store, out FakeSessionFactory sessionFactory);
        ReaderProfile profile = NewProfile();
        var session = new FakeSession();
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe
        sessionFactory.Queue.Enqueue(session);           // register -> handle.Session
        await manager.AddAsync(profile, enableAfterAdding: false);
        await manager.ActivateAsync(profile.Id); // ensure a connected attempt occurred
        await manager.StartInventoryAsync(profile.Id, new InventorySpec());

        await manager.RemoveAsync(profile.Id);

        Assert.Equal(1, session.StopInventoryCount);
        Assert.Empty(manager.Readers);
        Assert.Null(await store.GetAsync(profile.Id));
        Assert.Throws<KeyNotFoundException>(() => manager.GetSnapshot(profile.Id));
    }

    [Fact]
    public async Task SetEnabledAsync_updates_snapshot_and_persists()
    {
        var manager = CreateManager(out FakeProfileStore store, out _);
        ReaderProfile profile = NewProfile();
        await manager.AddAsync(profile, enableAfterAdding: false);

        await manager.SetEnabledAsync(profile.Id, enabled: true);

        Assert.True(manager.GetSnapshot(profile.Id).IsEnabled);
        ReaderProfile? saved = await store.GetAsync(profile.Id);
        Assert.NotNull(saved);
        Assert.True(saved.IsEnabled);
    }

    [Fact]
    public async Task Device_initiated_close_marks_reader_Faulted()
    {
        var manager = CreateManager(out _, out FakeSessionFactory sessionFactory);
        ReaderProfile profile = NewProfile();
        var session = new FakeSession();
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe
        sessionFactory.Queue.Enqueue(session);           // register
        await manager.AddAsync(profile, enableAfterAdding: false);
        await manager.ActivateAsync(profile.Id);

        session.RaiseDeviceInitiatedClosed();
        await Task.Delay(50);

        Assert.Equal(ReaderState.Faulted, manager.GetSnapshot(profile.Id).State);
    }

    [Fact]
    public async Task Faulted_reader_can_be_activated_again_after_device_initiated_close()
    {
        var manager = CreateManager(out _, out FakeSessionFactory sessionFactory);
        ReaderProfile profile = NewProfile();
        var session = new FakeSession();
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe
        sessionFactory.Queue.Enqueue(session);           // register
        await manager.AddAsync(profile, enableAfterAdding: false);
        await manager.StartInventoryAsync(profile.Id, new InventorySpec());

        session.RaiseDeviceInitiatedClosed();
        for (int i = 0; i < 20 && manager.GetSnapshot(profile.Id).State != ReaderState.Faulted; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(ReaderState.Faulted, manager.GetSnapshot(profile.Id).State);
        ReaderActivationResult result = await manager.ActivateAsync(profile.Id);

        Assert.True(result.Succeeded);
        ReaderRuntimeSnapshot snapshot = manager.GetSnapshot(profile.Id);
        Assert.Equal(ReaderState.Disconnected, snapshot.State);
        Assert.False(snapshot.IsStale);
        Assert.True(session.ConnectCount >= 2);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task Transport_fault_completes_inventory_and_allows_a_new_start()
    {
        var manager = CreateManager(out _, out FakeSessionFactory sessionFactory);
        ReaderProfile profile = NewProfile();
        var session = new FakeSession();
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe
        sessionFactory.Queue.Enqueue(session);           // register
        await manager.AddAsync(profile, enableAfterAdding: false);

        var lifecycleEvents = new List<InventoryLifecycleChangedEventArgs>();
        manager.LifecycleChanged += (_, args) => lifecycleEvents.Add(args);

        StartInventoryResult started = await manager.StartInventoryAsync(profile.Id, new InventorySpec());
        Assert.True(started.Succeeded);
        Assert.Equal(ReaderState.Inventorying, manager.GetSnapshot(profile.Id).State);

        session.RaiseConnectionFaulted("socket reset by peer");
        for (int i = 0; i < 40 && (manager.GetSnapshot(profile.Id).State != ReaderState.Faulted
            || !lifecycleEvents.Any(args => args.State == InventoryLifecycleState.Stopped)); i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(ReaderState.Faulted, manager.GetSnapshot(profile.Id).State);
        InventoryLifecycleChangedEventArgs stopped = Assert.Single(
            lifecycleEvents,
            args => args.ReaderId == profile.Id
                && args.State == InventoryLifecycleState.Stopped);
        Assert.Equal(InventoryStopReason.ConnectionFaulted, stopped.StopReason);
        Assert.Contains("socket reset by peer", stopped.Error);
        StartInventoryResult restarted = await manager.StartInventoryAsync(profile.Id, new InventorySpec());

        Assert.True(restarted.Succeeded);
        Assert.Equal(ReaderState.Inventorying, manager.GetSnapshot(profile.Id).State);
        Assert.True(session.ConnectCount >= 2);
        await manager.StopInventoryAsync(profile.Id);
    }

    [Fact]
    public async Task Reader_exception_stops_inventory_and_disconnects_the_session()
    {
        var manager = CreateManager(out _, out FakeSessionFactory sessionFactory);
        ReaderProfile profile = NewProfile();
        var session = new FakeSession();
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe
        sessionFactory.Queue.Enqueue(session);           // register
        await manager.AddAsync(profile, enableAfterAdding: false);

        StartInventoryResult started = await manager.StartInventoryAsync(profile.Id, new InventorySpec());
        Assert.True(started.Succeeded);
        Assert.True(session.IsConnected);

        session.RaiseReaderException("protocol error");
        for (int i = 0; i < 40 && manager.GetSnapshot(profile.Id).State != ReaderState.Faulted; i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(ReaderState.Faulted, manager.GetSnapshot(profile.Id).State);
        Assert.False(session.IsConnected);
        Assert.False(session.InventoryRunning);
    }

    [Fact]
    public async Task Matching_gpi_stop_trigger_completes_inventory_and_disconnects_the_session()
    {
        var manager = CreateManager(out _, out FakeSessionFactory sessionFactory);
        ReaderProfile profile = NewProfile();
        var session = new FakeSession();
        InventorySettings inventory = new()
        {
            StopTrigger = new InventoryStopTrigger
            {
                Type = InventoryStopTriggerType.GpiWithTimeout,
                GpiPortNumber = 2,
                GpiState = true,
                TimeoutMilliseconds = 1000,
            },
        };
        session.SettingsSnapshot = new ReaderSettingsSnapshot(
            new ReaderSettings { Inventory = inventory },
            new ManagedRoSpecSnapshot(inventory, InventoryRuntimeState.Disabled));
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe
        sessionFactory.Queue.Enqueue(session);           // register
        await manager.AddAsync(profile, enableAfterAdding: false);

        var lifecycleEvents = new List<InventoryLifecycleChangedEventArgs>();
        var eventOrder = new List<string>();
        manager.LifecycleChanged += (_, args) =>
        {
            lifecycleEvents.Add(args);
            if (args.ReaderId == profile.Id && args.State == InventoryLifecycleState.Stopped)
            {
                eventOrder.Add("stopped");
            }
        };
        manager.GpiChanged += (_, args) =>
        {
            if (args.ReaderId == profile.Id && args.Status.PortNumber == 2)
            {
                eventOrder.Add("gpi");
            }
        };

        StartInventoryResult started = await manager.StartInventoryAsync(profile.Id, new InventorySpec());
        Assert.True(started.Succeeded);
        Assert.True(session.IsConnected);

        session.RaiseGpiChanged(2, state: true);
        for (int i = 0; i < 40 && (session.StopInventoryCount == 0
            || !lifecycleEvents.Any(args => args.State == InventoryLifecycleState.Stopped)); i++)
        {
            await Task.Delay(10);
        }

        Assert.Equal(1, session.StopInventoryCount);
        Assert.False(session.InventoryRunning);
        Assert.False(session.IsConnected);
        Assert.Equal(ReaderState.Disconnected, manager.GetSnapshot(profile.Id).State);
        Assert.Contains(lifecycleEvents, args =>
            args.ReaderId == profile.Id
            && args.State == InventoryLifecycleState.Started);
        InventoryLifecycleChangedEventArgs stopped = Assert.Single(
            lifecycleEvents,
            args => args.State == InventoryLifecycleState.Stopped);
        Assert.Equal(InventoryStopReason.Gpi, stopped.StopReason);
        Assert.Equal(["gpi", "stopped"], eventOrder);
    }

    [Fact]
    public async Task AddAsync_same_id_twice_returns_RegisterFailed_without_deleting_existing()
    {
        var manager = CreateManager(out FakeProfileStore store, out FakeSessionFactory sessionFactory);
        ReaderProfile profile = NewProfile();
        await manager.AddAsync(profile, enableAfterAdding: false);

        ReaderAddResult second = await manager.AddAsync(
            profile with { Name = "Replacement", Host = "192.0.2.99" },
            enableAfterAdding: false);

        Assert.Equal(ReaderAddStatus.RegisterFailed, second.Status);
        // 已注册的同 Id reader 不得被误删。
        ReaderProfile? saved = await store.GetAsync(profile.Id);
        Assert.Equal(profile.Name, saved?.Name);
        Assert.Equal(profile.Host, saved?.Host);
        Assert.Single(manager.Readers);
    }

    [Fact]
    public async Task Remove_then_device_close_does_not_throw()
    {
        var manager = CreateManager(out _, out FakeSessionFactory sessionFactory);
        ReaderProfile profile = NewProfile();
        var session = new FakeSession();
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe
        sessionFactory.Queue.Enqueue(session);           // register
        await manager.AddAsync(profile, enableAfterAdding: false);
        await manager.RemoveAsync(profile.Id);

        // Gate 已 Dispose；此消息应被守卫忽略，不抛未观察异常。
        session.RaiseDeviceInitiatedClosed();
        await Task.Delay(30);
        Assert.Empty(manager.Readers);
    }

    [Fact]
    public async Task Remove_serializes_same_id_replacement_until_profile_delete_finishes()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new BlockingDeleteProfileStore();
        await using var manager = new ReaderManager(sessionFactory, store);
        ReaderProfile profile = NewProfile();
        await manager.AddAsync(profile, enableAfterAdding: false);

        Task remove = manager.RemoveAsync(profile.Id);
        await store.DeleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        sessionFactory.Queue.Enqueue(new FakeSession()); // replacement probe
        sessionFactory.Queue.Enqueue(new FakeSession()); // replacement register
        Task<ReaderAddResult> add = manager.AddAsync(
            profile with { Name = "Replacement" },
            enableAfterAdding: false);

        await Task.Delay(50);
        Assert.False(add.IsCompleted);

        store.AllowDelete.TrySetResult(true);
        await remove;
        ReaderAddResult result = await add;

        Assert.Equal(ReaderAddStatus.Added, result.Status);
        Assert.Equal("Replacement", (await store.GetAsync(profile.Id))?.Name);
        Assert.Single(manager.Readers);
    }

    [Fact]
    public async Task SetEnabled_serializes_with_remove_while_persistence_is_in_flight()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new BlockingSaveProfileStore();
        await using var manager = new ReaderManager(sessionFactory, store);
        ReaderProfile profile = NewProfile();
        await manager.AddAsync(profile, enableAfterAdding: false);

        store.BlockSaves = true;
        Task setEnabled = manager.SetEnabledAsync(profile.Id, enabled: true);
        await store.SaveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task remove = manager.RemoveAsync(profile.Id);
        await Task.Delay(50);
        Assert.False(remove.IsCompleted);

        store.AllowSave.TrySetResult(true);
        await setEnabled;
        await remove;

        Assert.Empty(manager.Readers);
        Assert.Null(await store.GetAsync(profile.Id));
    }

    private static ReaderManager CreateManager(out FakeProfileStore store, out FakeSessionFactory sessionFactory)
    {
        store = new FakeProfileStore();
        sessionFactory = new FakeSessionFactory();
        return new ReaderManager(sessionFactory, store);
    }

    private sealed class BlockingDeleteProfileStore : IReaderProfileStore
    {
        private readonly FakeProfileStore inner = new();

        public TaskCompletionSource<bool> DeleteStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> AllowDelete { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ReaderProfile>> GetAllAsync(CancellationToken ct = default) =>
            inner.GetAllAsync(ct);

        public Task<ReaderProfile?> GetAsync(Guid readerId, CancellationToken ct = default) =>
            inner.GetAsync(readerId, ct);

        public Task SaveAsync(ReaderProfile profile, CancellationToken ct = default) =>
            inner.SaveAsync(profile, ct);

        public async Task DeleteAsync(Guid readerId, CancellationToken ct = default)
        {
            DeleteStarted.TrySetResult(true);
            await AllowDelete.Task.WaitAsync(ct);
            await inner.DeleteAsync(readerId, ct);
        }
    }

    private sealed class BlockingSaveProfileStore : IReaderProfileStore
    {
        private readonly FakeProfileStore inner = new();

        public bool BlockSaves { get; set; }

        public TaskCompletionSource<bool> SaveStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> AllowSave { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<ReaderProfile>> GetAllAsync(CancellationToken ct = default) =>
            inner.GetAllAsync(ct);

        public Task<ReaderProfile?> GetAsync(Guid readerId, CancellationToken ct = default) =>
            inner.GetAsync(readerId, ct);

        public async Task SaveAsync(ReaderProfile profile, CancellationToken ct = default)
        {
            if (BlockSaves)
            {
                SaveStarted.TrySetResult(true);
                await AllowSave.Task.WaitAsync(ct);
            }

            await inner.SaveAsync(profile, ct);
        }

        public Task DeleteAsync(Guid readerId, CancellationToken ct = default) =>
            inner.DeleteAsync(readerId, ct);
    }
}
