using LlrpReaderPlatform.App.Wpf.ViewModels;
using LlrpReaderPlatform.Contracts.Discovery;
using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Contracts.Tagging;
using LlrpReaderPlatform.Services.Lifecycle;
using LlrpReaderPlatform.Services.Sdk;
using LlrpReaderPlatform.Services.Settings;
using LlrpReaderPlatform.TestKit;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using Xunit;

namespace LlrpReaderPlatform.App.Wpf.Tests;

public sealed class MainViewModelSmokeTests
{
    private static ServiceCollection BuildServices()
    {
        ServiceCollection services = new();
        services.AddSingleton<FakeSessionFactory>();
        services.AddSingleton<FakeProfileStore>();
        services.AddSingleton<IReaderSessionFactory>(sp => sp.GetRequiredService<FakeSessionFactory>());
        services.AddSingleton<ReaderManager>();
        services.AddSingleton<IReaderManager>(sp => sp.GetRequiredService<ReaderManager>());
        services.AddSingleton<IInventoryService>(sp => sp.GetRequiredService<ReaderManager>());
        services.AddSingleton<IReaderSettingsRuntime>(sp => sp.GetRequiredService<ReaderManager>());
        services.AddSingleton<ISettingsCompiler, StandardSettingsCompiler>();
        services.AddSingleton<IReaderSettingsService>(sp => new SettingsService(
            sp.GetRequiredService<IReaderManager>(),
            sp.GetRequiredService<ISettingsCompiler>(),
            sp.GetRequiredService<IReaderSettingsRuntime>()));
        services.AddSingleton<IReaderDiscoveryService, FakeDiscovery>();
        return services;
    }

    [Fact]
    public async Task AddCommand_registers_reader_and_refreshes_list()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        await using var manager = new ReaderManager(sessionFactory, store);
        var vm = new MainViewModel(
            manager,
            new SettingsService(manager, new StandardSettingsCompiler()),
            manager,
            new FakeDiscovery())
        {
            Host = "10.0.0.5",
            Port = 5084,
            ReaderName = "Backend",
        };

        await vm.AddCommand.ExecuteAsync(null);

        Assert.Single(vm.Readers);
        ReaderItemViewModel item = vm.Readers[0];
        Assert.Equal("Backend", item.Name);
        Assert.Equal("10.0.0.5", item.Host);
        Assert.True(item.IsEnabled);
        Assert.Equal("Disconnected", item.State);
        Assert.Equal("已同步能力", item.StatusText);
    }

    [Fact]
    public async Task InitializeAsync_restores_readers_before_refreshing_the_wpf_list()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        var profile = new LlrpReaderPlatform.Contracts.Readers.ReaderProfile
        {
            Id = Guid.NewGuid(),
            Name = "Restored",
            Host = "192.0.2.90",
            IsEnabled = false,
        };
        await store.SaveAsync(profile);

        await using var manager = new ReaderManager(sessionFactory, store);
        var vm = new MainViewModel(
            manager,
            new SettingsService(manager, new StandardSettingsCompiler()),
            manager,
            new FakeDiscovery());

        await vm.InitializeAsync();

        var item = Assert.Single(vm.Readers);
        Assert.Equal(profile.Id, item.ReaderId);
        Assert.Same(item, vm.SelectedReader);
        Assert.Equal(item.ReaderId, vm.TagMemory.ReaderId);
        Assert.Equal(item.Name, vm.TagMemory.ReaderName);
        Assert.False(vm.IsBusy);
        Assert.Contains("已就绪", vm.Status);
    }

    [Fact]
    public async Task RemoveCommand_removes_and_refreshes()
    {
        var sessionFactory = new FakeSessionFactory();
        await using var manager = new ReaderManager(sessionFactory, new FakeProfileStore());
        var vm = new MainViewModel(
            manager,
            new SettingsService(manager, new StandardSettingsCompiler()),
            manager,
            new FakeDiscovery())
        { Host = "10.0.0.6", ReaderName = "Temp" };

        await vm.AddCommand.ExecuteAsync(null);
        Guid id = vm.Readers[0].ReaderId;
        await vm.OpenReaderSettingsCommand.ExecuteAsync(vm.Readers[0]);
        Assert.Same(vm.Settings, vm.CurrentPage);

        await vm.RemoveCommand.ExecuteAsync(id);

        Assert.Empty(vm.Readers);
        Assert.Same(vm.Inventory, vm.CurrentPage);
        Assert.Null(vm.Settings.ReaderId);
    }

    [Fact]
    public async Task ReaderSettings_loads_layout_rows()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        await using var manager = new ReaderManager(sessionFactory, store);
        var profile = new LlrpReaderPlatform.Contracts.Readers.ReaderProfile
        {
            Id = Guid.NewGuid(),
            Host = "10.0.0.7",
            Name = "S",
        };
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe
        FakeSession registerSession = new() { ConnectThrows = new IOException("offline") };
        sessionFactory.Queue.Enqueue(registerSession); // register
        await manager.AddAsync(profile, enableAfterAdding: false);
        var settings = new SettingsService(manager, new StandardSettingsCompiler());
        var vm = new ReaderSettingsViewModel(settings);

        await vm.LoadCommand.ExecuteAsync(profile.Id);

        // 未连接/无能力 → 只读占位行。
        Assert.Single(vm.Rows);
        Assert.True(vm.Rows[0].IsReadOnly);
        Assert.Contains("连接", vm.Status);
    }

    [Fact]
    public void Reader_list_exposes_negotiated_protocol_version_in_details()
    {
        var snapshot = new ReaderRuntimeSnapshot
        {
            ReaderId = Guid.NewGuid(),
            Profile = new ReaderProfile
            {
                Id = Guid.NewGuid(),
                Host = "192.0.2.70",
                Name = "Protocol Reader",
            },
            State = ReaderState.Disconnected,
            IsEnabled = true,
            IsStale = false,
            NegotiatedProtocolVersion = LlrpProtocolVersion.Version11,
        };

        var item = new ReaderItemViewModel(snapshot);

        Assert.Equal("LLRP 1.1", item.Protocol);
        Assert.Contains("LLRP 1.1", item.Details);
    }

    [Fact]
    public async Task ReaderSettings_load_defaults_command_updates_rows_without_applying()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        await using var manager = new ReaderManager(sessionFactory, store);
        var profile = new LlrpReaderPlatform.Contracts.Readers.ReaderProfile
        {
            Id = Guid.NewGuid(),
            Host = "10.0.0.71",
            Name = "Defaults",
        };
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe
        FakeSession session = new();
        session.SettingsDefaults = session.SettingsDefaults with
        {
            Settings = new LlrpSdk.ReaderSettings
            {
                Inventory = new LlrpSdk.InventorySettings { Session = 2 },
            },
        };
        sessionFactory.Queue.Enqueue(session); // register
        await manager.AddAsync(profile, enableAfterAdding: false);
        await manager.ActivateAsync(profile.Id);

        var vm = new ReaderSettingsViewModel(new SettingsService(manager, new StandardSettingsCompiler()));
        await vm.LoadCommand.ExecuteAsync(profile.Id);
        await vm.LoadDefaultsCommand.ExecuteAsync(null);

        Assert.Contains(vm.Rows, row => row.Key == SettingsKeys.Session && row.ValueText == "2");
        Assert.Equal(0, session.SettingsApplyCount);
        Assert.Contains("默认", vm.Status);
        Assert.Equal("SDK defaults (not applied)", vm.SettingsOrigin);
    }

    [Fact]
    public async Task Inventory_start_command_sets_status()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        await using var manager = new ReaderManager(sessionFactory, store);
        var profile = new LlrpReaderPlatform.Contracts.Readers.ReaderProfile
        {
            Id = Guid.NewGuid(),
            Host = "10.0.0.8",
            Name = "I",
        };
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe
        sessionFactory.Queue.Enqueue(new FakeSession()); // register
        await manager.AddAsync(profile, enableAfterAdding: false);
        var vm = new InventoryViewModel(manager);

        await vm.StartCommand.ExecuteAsync(profile.Id);

        Assert.Contains("启动", vm.Status);
    }

    [Fact]
    public async Task Disabling_reader_from_wpf_list_stops_active_inventory_and_persists_state()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        await using var manager = new ReaderManager(sessionFactory, store);
        ReaderProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Host = "10.0.0.83",
            Name = "Toggleable",
        };
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe
        FakeSession session = new();
        sessionFactory.Queue.Enqueue(session); // registered session
        await manager.AddAsync(profile, enableAfterAdding: false);
        await manager.SetEnabledAsync(profile.Id, enabled: true);

        var vm = new MainViewModel(
            manager,
            new SettingsService(manager, new StandardSettingsCompiler()),
            manager,
            new FakeDiscovery());
        vm.Refresh();
        await vm.Inventory.StartCommand.ExecuteAsync(profile.Id);
        Assert.True(session.InventoryRunning);

        ReaderItemViewModel item = Assert.Single(vm.Readers);
        item.IsEnabled = false;

        for (int i = 0; i < 50 && session.InventoryRunning; i++)
        {
            await Task.Delay(10);
        }

        Assert.False(session.InventoryRunning);
        Assert.False(session.IsConnected);
        Assert.False(manager.GetSnapshot(profile.Id).IsEnabled);
        Assert.False((await store.GetAsync(profile.Id))!.IsEnabled);
    }

    [Fact]
    public async Task Opening_settings_recovers_a_faulted_reader_before_querying()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        await using var manager = new ReaderManager(sessionFactory, store);
        ReaderProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Host = "10.0.0.81",
            Name = "Recoverable",
        };
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe
        var session = new FakeSession();
        sessionFactory.Queue.Enqueue(session); // register
        await manager.AddAsync(profile, enableAfterAdding: false);

        var vm = new MainViewModel(
            manager,
            new SettingsService(manager, new StandardSettingsCompiler()),
            manager,
            new FakeDiscovery());
        vm.Refresh();
        await vm.Inventory.StartCommand.ExecuteAsync(profile.Id);
        session.RaiseDeviceInitiatedClosed();
        for (int i = 0; i < 20 && manager.GetSnapshot(profile.Id).State != ReaderState.Faulted; i++)
        {
            await Task.Delay(10);
        }

        vm.Refresh();
        ReaderItemViewModel item = Assert.Single(vm.Readers);
        await vm.OpenReaderSettingsCommand.ExecuteAsync(item);

        Assert.Equal(ReaderState.Disconnected, manager.GetSnapshot(profile.Id).State);
        Assert.Same(vm.Settings, vm.CurrentPage);
        Assert.Equal(profile.Host, vm.Settings.ReaderHost);
        Assert.Contains("已连接", vm.Status);
    }

    [Fact]
    public async Task Opening_settings_keeps_a_readonly_page_when_activation_fails()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        await using var manager = new ReaderManager(sessionFactory, store);
        ReaderProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Host = "10.0.0.82",
            Name = "Offline",
        };
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe
        var session = new FakeSession();
        sessionFactory.Queue.Enqueue(session); // register
        await manager.AddAsync(profile, enableAfterAdding: false);
        session.ConnectThrows = new IOException("offline");

        var vm = new MainViewModel(
            manager,
            new SettingsService(manager, new StandardSettingsCompiler()),
            manager,
            new FakeDiscovery());
        vm.Refresh();
        await vm.OpenReaderSettingsCommand.ExecuteAsync(vm.Readers[0]);

        Assert.Same(vm.Settings, vm.CurrentPage);
        Assert.Single(vm.Settings.Rows);
        Assert.True(vm.Settings.Rows[0].IsReadOnly);
        Assert.Contains("连接失败", vm.Status);
    }

    [Fact]
    public async Task Reader_list_reconnect_command_reactivates_a_faulted_reader()
    {
        var sessionFactory = new FakeSessionFactory();
        await using var manager = new ReaderManager(sessionFactory, new FakeProfileStore());
        ReaderProfile profile = new()
        {
            Id = Guid.NewGuid(),
            Host = "10.0.0.83",
            Name = "Reconnectable",
        };
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe
        var session = new FakeSession();
        sessionFactory.Queue.Enqueue(session); // register
        await manager.AddAsync(profile, enableAfterAdding: false);

        var vm = new MainViewModel(
            manager,
            new SettingsService(manager, new StandardSettingsCompiler()),
            manager,
            new FakeDiscovery());
        vm.Refresh();
        await vm.Inventory.StartCommand.ExecuteAsync(profile.Id);
        session.RaiseConnectionFaulted("socket reset");
        for (int i = 0; i < 30 && manager.GetSnapshot(profile.Id).State != ReaderState.Faulted; i++)
        {
            await Task.Delay(10);
        }

        vm.Refresh();
        await vm.ActivateCommand.ExecuteAsync(Assert.Single(vm.Readers));

        Assert.Equal(ReaderState.Disconnected, manager.GetSnapshot(profile.Id).State);
        Assert.False(manager.GetSnapshot(profile.Id).IsStale);
        Assert.Equal("激活成功", vm.Status);
    }

    [Fact]
    public async Task MainViewModel_is_di_resolvable()
    {
        ServiceCollection services = BuildServices();
        services.AddSingleton<MainViewModel>();

        await using ServiceProvider provider = services.BuildServiceProvider();
        MainViewModel vm = provider.GetRequiredService<MainViewModel>();

        Assert.NotNull(vm);
        Assert.NotNull(vm.Settings);
        Assert.NotNull(vm.Inventory);
        Assert.NotNull(vm.Discovered);
        Assert.NotNull(vm.About);
        Assert.NotNull(vm.AppSettings);
        Assert.NotNull(vm.TagLists);
        Assert.NotNull(vm.InventoryRuns);
    }

    [Fact]
    public async Task ReaderSettings_groups_legacy_tab1_sections_without_changing_platform_keys()
    {
        Guid readerId = Guid.NewGuid();
        var service = new StubSettingsService(readerId);
        var vm = new ReaderSettingsViewModel(service);

        await vm.LoadCommand.ExecuteAsync(readerId);

        Assert.Contains(vm.ManualRows, row => row.Key == "session");
        Assert.Contains(vm.PowerRows, row => row.Key == "tx-power-dbm");
        Assert.Contains(vm.GpiRows, row => row.Key == "start-gpi-enabled");
        Assert.Contains(vm.FilterRows, row => row.Key == "filter-1-enabled");
        Assert.Contains(vm.StateAwareRows, row => row.Key == "state-aware-target");
        Assert.Contains(vm.FrequencyRows, row => row.Key == "impinj.fixed-frequency-mode");
        Assert.Contains(vm.LowDutyRows, row => row.Key == "impinj.low-duty-cycle");
        Assert.Contains(vm.ReportRows, row => row.Key == "report-rssi");
        Assert.Equal(4, vm.GpiSettings.Count);
        Assert.Single(vm.Filter1Rows);
        Assert.Empty(vm.Filter2Rows);
        Assert.Equal(8, vm.Rows.Count);
    }

    private sealed class FakeDiscovery : IReaderDiscoveryService
    {
        public IReadOnlyList<DiscoveredReader> Result { get; set; } = [];

        public Task<IReadOnlyList<DiscoveredReader>> DiscoverAsync(
            TimeSpan scanDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(Result);
    }

    private sealed class StubSettingsService(Guid readerId) : IReaderSettingsService
    {
        private readonly SettingsEditorModel model = BuildModel(readerId);

        public Task<SettingsEditorModel> QueryAsync(Guid id, CancellationToken ct = default) => Task.FromResult(model);

        public Task<SettingsEditorModel> GetDefaultsAsync(Guid id, CancellationToken ct = default) => Task.FromResult(model);

        public SettingsValidationResult Validate(SettingsDraft draft) => new(true);

        public Task<SettingsApplyResult> ApplyAsync(Guid id, SettingsDraft draft, CancellationToken ct = default) =>
            Task.FromResult(new SettingsApplyResult(true));

        private static SettingsEditorModel BuildModel(Guid id)
        {
            SettingsEntry[] entries =
            [
                new() { Key = "session", Title = "Session", EditorKind = EditorKind.Choice, ValueType = typeof(int), Options = [new(0, "S0")], CurrentValue = 0 },
                new() { Key = "tx-power-dbm", Title = "Tx Power", EditorKind = EditorKind.Decimal, ValueType = typeof(decimal), CurrentValue = 20m },
                new() { Key = "start-gpi-enabled", Title = "Start GPI", EditorKind = EditorKind.Boolean, ValueType = typeof(bool), CurrentValue = false },
                new() { Key = "filter-1-enabled", Title = "Filter 1", EditorKind = EditorKind.Boolean, ValueType = typeof(bool), CurrentValue = false },
                new() { Key = "state-aware-target", Title = "State Target", EditorKind = EditorKind.Choice, ValueType = typeof(int), Options = [new(0, "A")], CurrentValue = 0 },
                new() { Key = "impinj.fixed-frequency-mode", Title = "Frequency", EditorKind = EditorKind.Choice, ValueType = typeof(int), Options = [new(-1, "Disabled")], CurrentValue = -1 },
                new() { Key = "impinj.low-duty-cycle", Title = "Low Duty", EditorKind = EditorKind.Boolean, ValueType = typeof(bool), CurrentValue = false },
                new() { Key = "report-rssi", Title = "RSSI", EditorKind = EditorKind.Boolean, ValueType = typeof(bool), CurrentValue = true },
            ];
            return new SettingsEditorModel(
                new EffectiveSettingsLayout { ReaderId = id, CapabilityRevision = 1, Entries = entries },
                new SettingsSnapshot { ReaderId = id, CapabilityRevision = 1, Values = new Dictionary<string, object?>() });
        }
    }
}
