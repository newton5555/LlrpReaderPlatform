using System.IO;
using LlrpReaderPlatform.App.Wpf.ViewModels;
using LlrpReaderPlatform.Contracts.Discovery;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Extensions.Impinj;
using LlrpReaderPlatform.Services.Lifecycle;
using LlrpReaderPlatform.TestKit;
using Xunit;

namespace LlrpReaderPlatform.App.Wpf.Tests;

public sealed class AddDataSourceViewModelTests
{
    private sealed class FakeDiscovery : IReaderDiscoveryService
    {
        public IReadOnlyList<DiscoveredReader> Result { get; set; } = [];
        public Exception? Throw { get; set; }
        public Task<IReadOnlyList<DiscoveredReader>> DiscoverAsync(TimeSpan scanDuration, CancellationToken cancellationToken = default)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            return Task.FromResult(Result);
        }
    }

    private sealed class BlockingDiscovery : IReaderDiscoveryService
    {
        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<DiscoveredReader>> DiscoverAsync(
            TimeSpan scanDuration,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }
    }

    [Fact]
    public async Task Submit_success_adds_reader_with_llrp_version()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        await using var manager = new ReaderManager(sessionFactory, store);
        var vm = new AddDataSourceViewModel(manager, new FakeDiscovery())
        {
            Host = "10.0.0.5",
            ReaderName = "R",
            LlrpVersion = LlrpProtocolVersionOption.Force101,
        };
        Guid? added = null;
        vm.DataSourceAdded += (_, id) => added = id;
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe
        sessionFactory.Queue.Enqueue(new FakeSession()); // register

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.NotNull(added);
        IReadOnlyList<ReaderProfile> saved = await store.GetAllAsync();
        Assert.Single(saved);
        Assert.True(saved[0].IsEnabled);
        Assert.Equal(LlrpProtocolVersionOption.Force101, saved[0].LlrpVersion);
        Assert.False(manager.GetSnapshot(saved[0].Id).IsStale);
        Assert.NotEqual(0, manager.GetSnapshot(saved[0].Id).CapabilityRevision);
    }

    [Fact]
    public async Task Submit_success_shows_probe_and_standard_path_diagnostics()
    {
        var sessionFactory = new FakeSessionFactory();
        await using var manager = new ReaderManager(sessionFactory, new FakeProfileStore());
        var probe = new FakeSession
        {
            NegotiatedVersion = LlrpNet.Core.Protocol.LlrpProtocolVersion.Version11,
        };
        probe.SetIdentity(42, 7, "firmware-1");
        sessionFactory.Queue.Enqueue(probe); // probe
        sessionFactory.Queue.Enqueue(new FakeSession()); // register
        var vm = new AddDataSourceViewModel(manager, new FakeDiscovery());

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.True(vm.HasProbeResult);
        Assert.Contains("LLRP 1.1", vm.ProbeSummary);
        Assert.Contains("42:7", vm.ProbeSummary);
        Assert.Equal("扩展匹配：标准 LLRP 路径", vm.ExtensionSummary);
    }

    [Fact]
    public async Task ProbeCommand_reports_device_without_persisting_or_registering_it()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        await using var manager = new ReaderManager(
            sessionFactory,
            store,
            extensions: [new ImpinjReaderExtensionModule()]);
        var probe = new FakeSession
        {
            NegotiatedVersion = LlrpNet.Core.Protocol.LlrpProtocolVersion.Version101,
        };
        probe.SetIdentity(
            ImpinjReaderExtensionModule.ImpinjManufacturerId,
            ImpinjReaderExtensionModule.R420ModelId,
            "6.4.1.240");
        sessionFactory.Queue.Enqueue(probe);
        var vm = new AddDataSourceViewModel(manager, new FakeDiscovery())
        {
            Host = "10.0.0.7",
        };

        await vm.ProbeCommand.ExecuteAsync(null);

        Assert.True(vm.HasProbeResult);
        Assert.Contains("LLRP 1.0.1", vm.ProbeSummary);
        Assert.Contains("25882:2001002", vm.ProbeSummary);
        Assert.Equal("扩展匹配：impinj", vm.ExtensionSummary);
        Assert.False(vm.IsProbing);
        Assert.Empty(await store.GetAllAsync());
        Assert.Empty(manager.Readers);

        vm.Host = "10.0.0.8";

        Assert.False(vm.HasProbeResult);
        Assert.Null(vm.ProbeSummary);
        Assert.Null(vm.ExtensionSummary);
    }

    [Fact]
    public async Task Submit_probe_failure_does_not_raise_event()
    {
        var sessionFactory = new FakeSessionFactory();
        await using var manager = new ReaderManager(sessionFactory, new FakeProfileStore());
        var vm = new AddDataSourceViewModel(manager, new FakeDiscovery()) { Host = "10.0.0.6" };
        bool raised = false;
        vm.DataSourceAdded += (_, _) => raised = true;
        sessionFactory.Queue.Enqueue(new FakeSession { ConnectThrows = new TimeoutException("unreachable") });

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.False(raised);
        Assert.Contains("失败", vm.Status);
    }

    [Fact]
    public async Task Submit_invalid_profile_is_reported_in_status_without_throwing()
    {
        await using var manager = new ReaderManager(new FakeSessionFactory(), new FakeProfileStore());
        var vm = new AddDataSourceViewModel(manager, new FakeDiscovery()) { Host = string.Empty };

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("Host 不能为空。", vm.Status);
        Assert.False(vm.IsSubmitting);
    }

    [Fact]
    public async Task Submit_trims_host_and_name_before_creating_a_profile()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        await using var manager = new ReaderManager(sessionFactory, store);
        var vm = new AddDataSourceViewModel(manager, new FakeDiscovery())
        {
            Host = " 10.0.0.15 ",
            ReaderName = "  R15  ",
        };
        sessionFactory.Queue.Enqueue(new FakeSession());
        sessionFactory.Queue.Enqueue(new FakeSession());

        await vm.SubmitCommand.ExecuteAsync(null);

        ReaderProfile saved = Assert.Single(await store.GetAllAsync());
        Assert.Equal("10.0.0.15", saved.Host);
        Assert.Equal("R15", saved.Name);
    }

    [Fact]
    public async Task Submit_rejects_invalid_port_before_creating_a_session()
    {
        var sessionFactory = new FakeSessionFactory();
        await using var manager = new ReaderManager(sessionFactory, new FakeProfileStore());
        var vm = new AddDataSourceViewModel(manager, new FakeDiscovery())
        {
            Host = "192.0.2.20",
            Port = "not-a-port",
        };
        sessionFactory.Queue.Enqueue(new FakeSession());

        await vm.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("LLRP Port 必须是 1 到 65535 的整数。", vm.Status);
        Assert.Single(sessionFactory.Queue);
    }

    [Fact]
    public async Task Submit_normalizes_bracketed_ipv6_host_before_creating_a_profile()
    {
        var sessionFactory = new FakeSessionFactory();
        var store = new FakeProfileStore();
        await using var manager = new ReaderManager(sessionFactory, store);
        var vm = new AddDataSourceViewModel(manager, new FakeDiscovery())
        {
            Host = " [fe80::10] ",
        };
        sessionFactory.Queue.Enqueue(new FakeSession());
        sessionFactory.Queue.Enqueue(new FakeSession());

        await vm.SubmitCommand.ExecuteAsync(null);

        ReaderProfile saved = Assert.Single(await store.GetAllAsync());
        Assert.Equal("fe80::10", saved.Host);
    }

    [Fact]
    public async Task DiscoverCommand_populates_old_studio_reader_picker_state()
    {
        var discovery = new FakeDiscovery
        {
            Result =
            [
                new DiscoveredReader(
                    DisplayName: "reader.local",
                    Host: "reader.local",
                    IpAddress: "10.0.0.9",
                    Port: 5084,
                    Properties: new Dictionary<string, string>()),
            ],
        };
        await using var manager = new ReaderManager(new FakeSessionFactory(), new FakeProfileStore());
        var vm = new AddDataSourceViewModel(manager, discovery);

        await vm.DiscoverCommand.ExecuteAsync(null);

        Assert.False(vm.IsDiscovering);
        Assert.True(vm.IsDiscoveryPanelOpen);
        Assert.Single(vm.Discovered);
        Assert.Equal("reader.local (10.0.0.9:5084)", vm.Discovered[0].DisplayEndpoint);
        Assert.Contains("发现 1", vm.Status);
    }

    [Fact]
    public async Task DiscoverCommand_normalizes_invalid_and_duplicate_endpoints()
    {
        var discovery = new FakeDiscovery
        {
            Result =
            [
                new DiscoveredReader(
                    DisplayName: " reader.local ",
                    Host: " reader.local ",
                    IpAddress: "10.0.0.9",
                    Port: 0,
                    Properties: new Dictionary<string, string>()),
                new DiscoveredReader(
                    DisplayName: "duplicate",
                    Host: "reader-alias.local",
                    IpAddress: "10.0.0.9",
                    Port: 5084,
                    Properties: new Dictionary<string, string>()),
                new DiscoveredReader(
                    DisplayName: "invalid",
                    Host: string.Empty,
                    IpAddress: string.Empty,
                    Port: 5084,
                    Properties: new Dictionary<string, string>()),
            ],
        };
        await using var manager = new ReaderManager(new FakeSessionFactory(), new FakeProfileStore());
        var vm = new AddDataSourceViewModel(manager, discovery);

        await vm.DiscoverCommand.ExecuteAsync(null);

        var item = Assert.Single(vm.Discovered);
        Assert.Equal("reader.local (10.0.0.9:5084)", item.DisplayEndpoint);
        Assert.Contains("发现 1", vm.Status);
    }

    [Fact]
    public void Discovered_ipv6_endpoint_is_displayed_unambiguously()
    {
        var item = new DiscoveredReaderViewModel(new DiscoveredReader(
            DisplayName: "reader-v6",
            Host: "fe80::10",
            IpAddress: "fe80::10",
            Port: 5084,
            Properties: new Dictionary<string, string>()));

        Assert.Equal("[fe80::10]:5084", item.DisplayEndpoint);
    }

    [Fact]
    public async Task DiscoverCommand_deduplicates_bracketed_and_unbracketed_ipv6_endpoints()
    {
        var discovery = new FakeDiscovery
        {
            Result =
            [
                new DiscoveredReader(
                    DisplayName: "reader-v6",
                    Host: "reader-v6.local",
                    IpAddress: "fe80::10",
                    Port: 5084,
                    Properties: new Dictionary<string, string>()),
                new DiscoveredReader(
                    DisplayName: "reader-v6-duplicate",
                    Host: "reader-v6-alias.local",
                    IpAddress: "[fe80::10]",
                    Port: 5084,
                    Properties: new Dictionary<string, string>()),
            ],
        };
        await using var manager = new ReaderManager(new FakeSessionFactory(), new FakeProfileStore());
        var vm = new AddDataSourceViewModel(manager, discovery);

        await vm.DiscoverCommand.ExecuteAsync(null);

        var item = Assert.Single(vm.Discovered);
        Assert.Equal("reader-v6.local ([fe80::10]:5084)", item.DisplayEndpoint);
    }

    [Fact]
    public async Task DiscoverCommand_reports_discovery_exception_in_status()
    {
        var discovery = new FakeDiscovery { Throw = new IOException("network unavailable") };
        await using var manager = new ReaderManager(new FakeSessionFactory(), new FakeProfileStore());
        var vm = new AddDataSourceViewModel(manager, discovery);

        await vm.DiscoverCommand.ExecuteAsync(null);

        Assert.Contains("发现失败", vm.Status);
        Assert.False(vm.IsDiscovering);
    }

    [Fact]
    public async Task Dispose_cancels_discovery_and_is_idempotent()
    {
        var discovery = new BlockingDiscovery();
        await using var manager = new ReaderManager(new FakeSessionFactory(), new FakeProfileStore());
        var vm = new AddDataSourceViewModel(manager, discovery);

        Task running = vm.DiscoverCommand.ExecuteAsync(null);
        await discovery.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(vm.IsInputEnabled);

        vm.Dispose();
        vm.Dispose();
        await running;

        Assert.False(vm.IsDiscovering);
        Assert.False(vm.IsInputEnabled);
    }

    [Fact]
    public async Task Use_discovered_is_ignored_while_discovery_is_in_flight()
    {
        var discovery = new BlockingDiscovery();
        await using var manager = new ReaderManager(new FakeSessionFactory(), new FakeProfileStore());
        var vm = new AddDataSourceViewModel(manager, discovery) { Host = "10.0.0.1" };
        var item = new DiscoveredReaderViewModel(new DiscoveredReader(
            DisplayName: "reader.local",
            Host: "reader.local",
            IpAddress: "10.0.0.9",
            Port: 5084,
            Properties: new Dictionary<string, string>()));

        Task running = vm.DiscoverCommand.ExecuteAsync(null);
        await discovery.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        vm.UseDiscoveredCommand.Execute(item);

        Assert.Equal("10.0.0.1", vm.Host);
        vm.Dispose();
        await running;
    }

    [Fact]
    public async Task Probe_command_is_blocked_while_discovery_is_in_flight()
    {
        var discovery = new BlockingDiscovery();
        await using var manager = new ReaderManager(new FakeSessionFactory(), new FakeProfileStore());
        using var vm = new AddDataSourceViewModel(manager, discovery);

        Task running = vm.DiscoverCommand.ExecuteAsync(null);
        await discovery.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await vm.ProbeCommand.ExecuteAsync(null);

        Assert.False(vm.IsProbing);
        Assert.Equal("正在扫描 _llrp._tcp...", vm.Status);
        vm.CancelCommand.Execute(null);
        await running.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Cancel_command_cancels_active_discovery_before_navigating_away()
    {
        var discovery = new BlockingDiscovery();
        await using var manager = new ReaderManager(new FakeSessionFactory(), new FakeProfileStore());
        var vm = new AddDataSourceViewModel(manager, discovery);
        bool cancelRequested = false;
        vm.CancelRequested += (_, _) => cancelRequested = true;

        Task running = vm.DiscoverCommand.ExecuteAsync(null);
        await discovery.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        vm.CancelCommand.Execute(null);
        await running.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(cancelRequested);
        Assert.False(vm.IsDiscovering);
        Assert.False(vm.IsDiscoveryPanelOpen);
        Assert.False(vm.HasProbeResult);
        Assert.Equal("发现已取消。", vm.Status);
    }
}
