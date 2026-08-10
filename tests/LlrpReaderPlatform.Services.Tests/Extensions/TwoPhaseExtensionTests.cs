using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Tagging;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Services.Extensions;
using LlrpReaderPlatform.Services.Lifecycle;
using LlrpReaderPlatform.TestKit;
using Xunit;

namespace LlrpReaderPlatform.Services.Tests.Extensions;

public sealed class TwoPhaseExtensionTests
{
    private sealed class AlwaysMatchModule : IReaderExtensionModule
    {
        public string Id => "always";
        public bool Configured { get; set; }
        public bool IsApplicable(ReaderProbeInfo info) => true;
        public void ConfigureBuilder(ReaderBuilderContext context) => Configured = true;
    }

    private sealed class NeverMatchModule : IReaderExtensionModule
    {
        public string Id => "never";
        public bool IsApplicable(ReaderProbeInfo info) => false;
        public void ConfigureBuilder(ReaderBuilderContext context) { }
    }

    private sealed class ManufacturerMatchModule : IReaderExtensionModule
    {
        public string Id => "manufacturer-42";
        public bool IsApplicable(ReaderProbeInfo info) => info.ManufacturerId == 42;
        public void ConfigureBuilder(ReaderBuilderContext context) { }
        public IReadOnlyList<Feature> GetFeatures(ReaderProbeInfo info) =>
            IsApplicable(info) ? [new Feature("test-capability", "test-vendor")] : [];
    }

    [Fact]
    public async Task AddAsync_uses_matching_extension_module_in_register_session()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        var module = new AlwaysMatchModule();
        await using var manager = new ReaderManager(sessionFactory, store, null, [module]);
        ReaderProfile profile = new() { Id = Guid.NewGuid(), Host = "192.0.2.4" };
        var probe = new FakeSession
        {
            NegotiatedVersion = LlrpNet.Core.Protocol.LlrpProtocolVersion.Version11,
        };
        probe.SetIdentity(42, 7, "firmware-1");
        sessionFactory.Queue.Enqueue(probe); // probe
        sessionFactory.Queue.Enqueue(new FakeSession()); // register

        ReaderAddResult result = await manager.AddAsync(profile, enableAfterAdding: false);

        Assert.True(result.Succeeded);
        Assert.Equal("42:7", result.Model);
        Assert.Equal("firmware-1", result.Firmware);
        Assert.Equal((uint)42, result.ManufacturerId);
        Assert.Equal((uint)7, result.ModelId);
        Assert.Equal(LlrpProtocolVersion.Version11, result.NegotiatedProtocolVersion);
        Assert.Equal(["always"], result.MatchedExtensionIds);
        // 注册会话（第二次 Create）应携带匹配的扩展模块。
        (_, IReadOnlyList<IReaderExtensionModule> registerExtensions) = sessionFactory.Created[1];
        Assert.Single(registerExtensions);
        Assert.Same(module, registerExtensions[0]);
    }

    [Fact]
    public async Task AddAsync_without_matching_extension_uses_standard_session()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        var module = new NeverMatchModule();
        await using var manager = new ReaderManager(sessionFactory, store, null, [module]);
        ReaderProfile profile = new() { Id = Guid.NewGuid(), Host = "192.0.2.4" };
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe
        sessionFactory.Queue.Enqueue(new FakeSession()); // register

        await manager.AddAsync(profile, enableAfterAdding: false);

        (_, IReadOnlyList<IReaderExtensionModule> registerExtensions) = sessionFactory.Created[1];
        Assert.Empty(registerExtensions);
    }

    [Fact]
    public async Task Offline_startup_restore_resolves_extension_on_later_activation()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        var module = new ManufacturerMatchModule();
        var profile = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Host = "192.0.2.44",
            IsEnabled = false,
        };
        await store.SaveAsync(profile);

        sessionFactory.Queue.Enqueue(new FakeSession
        {
            ConnectThrows = new TimeoutException("offline during startup"),
        });
        var restoredStandardSession = new FakeSession();
        sessionFactory.Queue.Enqueue(restoredStandardSession);
        var onlineProbe = new FakeSession();
        onlineProbe.SetIdentity(42, 7, "firmware");
        sessionFactory.Queue.Enqueue(onlineProbe);
        var extensionSession = new FakeSession();
        extensionSession.SetIdentity(42, 7, "firmware");
        sessionFactory.Queue.Enqueue(extensionSession);

        await using var manager = new ReaderManager(sessionFactory, store, null, [module]);
        await manager.InitializeAsync();

        ReaderActivationResult activation = await manager.ActivateAsync(profile.Id);

        Assert.True(activation.Succeeded);
        Assert.Equal((uint)42, manager.GetSnapshot(profile.Id).ManufacturerId);
        Assert.Contains(
            new Feature("test-capability", "test-vendor"),
            manager.GetSnapshot(profile.Id).FeatureCatalog.SupportedFeatures);
        Assert.False(restoredStandardSession.IsConnected);
        Assert.Single(sessionFactory.Created[3].Extensions);
        Assert.Same(module, sessionFactory.Created[3].Extensions[0]);
        Assert.False(extensionSession.IsConnected);

        // Session 替换后，旧标准 Session 的迟到事件不得污染当前生命周期。
        restoredStandardSession.RaiseDeviceInitiatedClosed();
        await Task.Delay(50);
        Assert.Equal(ReaderState.Disconnected, manager.GetSnapshot(profile.Id).State);
    }

    [Fact]
    public async Task Connected_identity_extension_swap_ignores_old_disconnect_event()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        var module = new ManufacturerMatchModule();
        var profile = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Host = "192.0.2.45",
            IsEnabled = false,
        };
        await store.SaveAsync(profile);

        sessionFactory.Queue.Enqueue(new FakeSession
        {
            ConnectThrows = new TimeoutException("offline during startup"),
        });
        var restoredStandardSession = new FakeSession
        {
            DeviceInitiatedCloseOnDisconnect = true,
        };
        restoredStandardSession.SetIdentity(42, 7, "firmware");
        sessionFactory.Queue.Enqueue(restoredStandardSession);
        sessionFactory.Queue.Enqueue(new FakeSession
        {
            ConnectThrows = new TimeoutException("transient reprobe failure"),
        });
        var extensionSession = new FakeSession();
        extensionSession.SetIdentity(42, 7, "firmware");
        sessionFactory.Queue.Enqueue(extensionSession);

        await using var manager = new ReaderManager(sessionFactory, store, null, [module]);
        await manager.InitializeAsync();

        ReaderActivationResult activation = await manager.ActivateAsync(profile.Id);

        Assert.True(activation.Succeeded);
        Assert.False(restoredStandardSession.IsConnected);
        Assert.False(extensionSession.IsConnected);
        await Task.Delay(50);
        Assert.Equal(ReaderState.Disconnected, manager.GetSnapshot(profile.Id).State);
    }

    [Fact]
    public async Task Inventory_start_resolves_extension_after_offline_startup_restore()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        var module = new ManufacturerMatchModule();
        var profile = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Host = "192.0.2.46",
            IsEnabled = false,
        };
        await store.SaveAsync(profile);

        sessionFactory.Queue.Enqueue(new FakeSession
        {
            ConnectThrows = new TimeoutException("offline during startup"),
        });
        var restoredStandardSession = new FakeSession();
        sessionFactory.Queue.Enqueue(restoredStandardSession);
        var onlineProbe = new FakeSession();
        onlineProbe.SetIdentity(42, 7, "firmware");
        sessionFactory.Queue.Enqueue(onlineProbe);
        var extensionSession = new FakeSession();
        extensionSession.SetIdentity(42, 7, "firmware");
        sessionFactory.Queue.Enqueue(extensionSession);

        await using var manager = new ReaderManager(sessionFactory, store, null, [module]);
        await manager.InitializeAsync();

        StartInventoryResult result = await manager.StartInventoryAsync(profile.Id, new InventorySpec());

        Assert.True(result.Succeeded);
        Assert.Single(sessionFactory.Created[3].Extensions);
        Assert.Same(module, sessionFactory.Created[3].Extensions[0]);
        Assert.False(restoredStandardSession.IsConnected);
        Assert.True(extensionSession.InventoryRunning);
        ReaderRuntimeSnapshot snapshot = manager.GetSnapshot(profile.Id);
        Assert.Equal((uint)42, snapshot.ManufacturerId);
        Assert.True(snapshot.CapabilityRevision > 0);
        Assert.Contains(new Feature("test-capability", "test-vendor"), snapshot.FeatureCatalog.SupportedFeatures);

        await manager.StopInventoryAsync(profile.Id);
    }

    [Fact]
    public async Task Inventory_start_resolves_extension_from_connected_identity_when_reprobe_fails()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        var module = new ManufacturerMatchModule();
        var profile = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Host = "192.0.2.47",
            IsEnabled = false,
        };
        await store.SaveAsync(profile);

        sessionFactory.Queue.Enqueue(new FakeSession
        {
            ConnectThrows = new TimeoutException("offline during startup"),
        });
        var restoredStandardSession = new FakeSession();
        restoredStandardSession.SetIdentity(42, 7, "firmware");
        sessionFactory.Queue.Enqueue(restoredStandardSession);
        sessionFactory.Queue.Enqueue(new FakeSession
        {
            ConnectThrows = new TimeoutException("transient reprobe failure"),
        });
        var extensionSession = new FakeSession();
        extensionSession.SetIdentity(42, 7, "firmware");
        sessionFactory.Queue.Enqueue(extensionSession);

        await using var manager = new ReaderManager(sessionFactory, store, null, [module]);
        await manager.InitializeAsync();

        StartInventoryResult result = await manager.StartInventoryAsync(profile.Id, new InventorySpec());

        Assert.True(result.Succeeded, result.Message);
        Assert.Single(sessionFactory.Created[3].Extensions);
        Assert.Same(module, sessionFactory.Created[3].Extensions[0]);
        Assert.False(restoredStandardSession.IsConnected);
        Assert.True(extensionSession.InventoryRunning);

        await manager.StopInventoryAsync(profile.Id);
    }
}
