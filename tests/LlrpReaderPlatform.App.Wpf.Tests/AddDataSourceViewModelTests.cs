using System.IO;
using LlrpReaderPlatform.App.Wpf.ViewModels;
using LlrpReaderPlatform.Contracts.Discovery;
using LlrpReaderPlatform.Contracts.Readers;
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

        Assert.Contains("失败", vm.Status);
        Assert.False(vm.IsSubmitting);
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
    public async Task DiscoverCommand_reports_discovery_exception_in_status()
    {
        var discovery = new FakeDiscovery { Throw = new IOException("network unavailable") };
        await using var manager = new ReaderManager(new FakeSessionFactory(), new FakeProfileStore());
        var vm = new AddDataSourceViewModel(manager, discovery);

        await vm.DiscoverCommand.ExecuteAsync(null);

        Assert.Contains("发现失败", vm.Status);
        Assert.False(vm.IsDiscovering);
    }
}
