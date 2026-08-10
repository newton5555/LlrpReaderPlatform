using LlrpReaderPlatform.App.Wpf.ViewModels;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Contracts.Tagging;
using LlrpReaderPlatform.Services.Lifecycle;
using LlrpReaderPlatform.TestKit;
using System.IO;
using Xunit;

namespace LlrpReaderPlatform.App.Wpf.Tests;

public sealed class DiagnosticsViewModelTests
{
    [Fact]
    public async Task Gpo_switch_uses_platform_service_and_reports_completion()
    {
        var factory = new FakeSessionFactory();
        await using var manager = new ReaderManager(factory, new FakeProfileStore());
        var profile = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Name = "Diagnostics Reader",
            Host = "192.0.2.90",
        };
        factory.Queue.Enqueue(new FakeSession()); // Probe
        var session = new FakeSession();
        factory.Queue.Enqueue(session); // Registered session
        await manager.AddAsync(profile, enableAfterAdding: false);

        using var vm = new DiagnosticsViewModel(manager);
        vm.SelectReader(profile.Id);
        vm.Gpo1 = true;

        for (int i = 0; i < 40 && session.LastGpoState != ((ushort)1, true); i++)
        {
            await Task.Delay(25);
        }

        Assert.Equal(((ushort)1, true), session.LastGpoState);
        Assert.Equal("GPO 1 已设置为 ON。", vm.Status);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task Gpo_operation_exposes_busy_state_and_serializes_reentry()
    {
        var service = new SerializingInventoryService();
        using var vm = new DiagnosticsViewModel(service);
        Guid readerId = Guid.NewGuid();
        vm.SelectReader(readerId);
        Task first = Task.Run(async () => await vm.SetGpoCommand.ExecuteAsync(readerId));
        await service.FirstOperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(vm.IsBusy);
        Task second = Task.Run(async () => await vm.SetGpoCommand.ExecuteAsync(readerId));
        await Task.Delay(25);

        Assert.False(second.IsCompleted);
        Assert.True(vm.IsBusy);
        Assert.Equal(0, service.GpoSetCount);

        service.ReleaseFirstOperation.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        for (int i = 0; i < 40 && vm.IsBusy; i++)
        {
            await Task.Delay(25);
        }

        Assert.False(vm.IsBusy);
        Assert.Equal(2, service.GpoSetCount);
    }

    [Fact]
    public async Task Gpo_switch_reverts_when_platform_operation_fails()
    {
        var factory = new FakeSessionFactory();
        await using var manager = new ReaderManager(factory, new FakeProfileStore());
        var profile = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Name = "Failed Diagnostics Reader",
            Host = "192.0.2.94",
        };
        factory.Queue.Enqueue(new FakeSession()); // Probe
        var session = new FakeSession { SetGpoThrows = new IOException("GPO rejected") };
        factory.Queue.Enqueue(session); // Registered session
        await manager.AddAsync(profile, enableAfterAdding: false);

        using var vm = new DiagnosticsViewModel(manager);
        vm.SelectReader(profile.Id);
        vm.Gpo1 = true;

        for (int i = 0; i < 40 && vm.IsBusy; i++)
        {
            await Task.Delay(25);
        }

        Assert.False(vm.Gpo1);
        Assert.Contains("GPO 1 操作失败", vm.Status);
        Assert.Equal(0, session.GpoSetCount);
    }

    [Fact]
    public async Task Refresh_gpi_reads_status_through_platform_service()
    {
        var factory = new FakeSessionFactory();
        await using var manager = new ReaderManager(factory, new FakeProfileStore());
        var profile = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Name = "GPI Reader",
            Host = "192.0.2.91",
        };
        factory.Queue.Enqueue(new FakeSession()); // Probe
        var session = new FakeSession
        {
            SettingsSnapshot = new LlrpSdk.ReaderSettingsSnapshot(
                new LlrpSdk.ReaderSettings
                {
                    Configuration = new LlrpSdk.ReaderConfiguration
                    {
                        Gpis = [new LlrpSdk.GpiStatus
                        {
                            GpiPortNumber = 1,
                            Configured = true,
                            State = LlrpSdk.GpiState.High,
                        }],
                        Gpos = [new LlrpSdk.GpoConfiguration
                        {
                            GpoPortNumber = 1,
                            GpoData = true,
                        }],
                    },
                },
                new LlrpSdk.ManagedRoSpecSnapshot(new LlrpSdk.InventorySettings(), LlrpSdk.InventoryRuntimeState.Disabled)),
        };
        factory.Queue.Enqueue(session); // Registered session
        await manager.AddAsync(profile, enableAfterAdding: false);

        using var vm = new DiagnosticsViewModel(manager);
        vm.SelectReader(profile.Id);
        await vm.RefreshGpioCommand.ExecuteAsync(profile.Id);

        var status = Assert.Single(vm.Gpis);
        Assert.Equal((ushort)1, status.PortNumber);
        Assert.True(status.Configured);
        Assert.True(status.State);
        Assert.True(vm.Gpo1);
        Assert.Equal("已读取 1 个 GPI、1 个 GPO 状态。", vm.Status);
        Assert.False(session.IsConnected);
        Assert.Equal(1, session.SettingsQueryCount);
    }

    [Fact]
    public async Task Refresh_gpi_without_reader_prompts_without_calling_service()
    {
        await using var manager = new ReaderManager(new FakeSessionFactory(), new FakeProfileStore());
        using var vm = new DiagnosticsViewModel(manager);

        await vm.RefreshGpioCommand.ExecuteAsync(null);

        Assert.Equal("请先从左侧选择 Reader。", vm.Status);
    }

    [Fact]
    public async Task Device_gpi_event_updates_tab_two_status_table()
    {
        var factory = new FakeSessionFactory();
        await using var manager = new ReaderManager(factory, new FakeProfileStore());
        var profile = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Name = "GPI Event Reader",
            Host = "192.0.2.93",
        };
        factory.Queue.Enqueue(new FakeSession()); // Probe
        var session = new FakeSession();
        factory.Queue.Enqueue(session); // Registered session
        await manager.AddAsync(profile, enableAfterAdding: false);

        using var vm = new DiagnosticsViewModel(manager);
        vm.SelectReader(profile.Id);
        session.RaiseGpiChanged(1, state: true);

        for (int i = 0; i < 40 && (!vm.Gpis.Any() || !vm.Gpis[0].State); i++)
        {
            await Task.Delay(25);
        }

        var status = Assert.Single(vm.Gpis);
        Assert.Equal((ushort)1, status.PortNumber);
        Assert.True(status.State);
    }

    [Fact]
    public async Task Known_missing_gpo_capability_reverts_switch_without_calling_service()
    {
        await using var manager = new ReaderManager(new FakeSessionFactory(), new FakeProfileStore());
        using var vm = new DiagnosticsViewModel(manager);
        vm.SelectReader(
            Guid.NewGuid(),
            new ReaderFeatureCatalog
            {
                SupportedFeatures =
                [
                    ReaderFeatures.StandardSettings,
                    ReaderFeatures.StandardInventory,
                ],
            });

        vm.Gpo1 = true;
        for (int i = 0; i < 40 && vm.Status is null; i++)
        {
            await Task.Delay(10);
        }

        Assert.False(vm.IsGpoAvailable);
        Assert.False(vm.Gpo1);
        Assert.Equal("当前 Reader 未声明标准 GPO 能力。", vm.Status);
    }

    [Fact]
    public async Task Known_gpo_count_disables_ports_above_device_count()
    {
        await using var manager = new ReaderManager(new FakeSessionFactory(), new FakeProfileStore());
        using var vm = new DiagnosticsViewModel(manager);
        vm.SelectReader(
            Guid.NewGuid(),
            new ReaderFeatureCatalog
            {
                SupportedFeatures =
                [
                    ReaderFeatures.StandardSettings,
                    ReaderFeatures.StandardInventory,
                    ReaderFeatures.StandardGpo,
                ],
            },
            gpoCount: 2);

        vm.Gpo3 = true;
        for (int i = 0; i < 40 && vm.Status is null; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(vm.IsGpo1Available);
        Assert.True(vm.IsGpo2Available);
        Assert.False(vm.IsGpo3Available);
        Assert.False(vm.IsGpo4Available);
        Assert.False(vm.Gpo3);
        Assert.Equal("当前 Reader 只有 2 个 GPO，端口 3 不可用。", vm.Status);
    }

    private sealed class SerializingInventoryService : IInventoryService
    {
        private int setGpoCalls;
        private int gpoSetCount;

        public TaskCompletionSource FirstOperationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstOperation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int GpoSetCount => Volatile.Read(ref gpoSetCount);

        public long DroppedTagReportCount => 0;

        public event EventHandler<TagObservedEventArgs>? TagObserved
        {
            add { }
            remove { }
        }

        public event EventHandler<InventoryLifecycleChangedEventArgs>? LifecycleChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<GpiObservedEventArgs>? GpiChanged
        {
            add { }
            remove { }
        }

        public Task<StartInventoryResult> StartInventoryAsync(
            Guid readerId,
            InventorySpec spec,
            CancellationToken ct = default) =>
            Task.FromResult(new StartInventoryResult(true));

        public Task StopInventoryAsync(Guid readerId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public IReadOnlyList<TagObservation> GetTags(Guid readerId) => [];

        public void ClearTags(Guid readerId) { }

        public Task<IReadOnlyList<GpiPortStatus>> GetGpiStatusAsync(
            Guid readerId,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GpiPortStatus>>([]);

        public Task<IReadOnlyList<GpoPortStatus>> GetGpoStatusAsync(
            Guid readerId,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GpoPortStatus>>([]);

        public Task<GpioStatusSnapshot> GetGpioStatusAsync(
            Guid readerId,
            CancellationToken ct = default) =>
            Task.FromResult(new GpioStatusSnapshot { Gpis = [], Gpos = [] });

        public Task<TagAccessResult> ReadTagMemoryAsync(
            Guid readerId,
            TagReadRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new TagAccessResult(true));

        public Task<TagAccessResult> WriteTagMemoryAsync(
            Guid readerId,
            TagWriteRequest request,
            CancellationToken ct = default) =>
            Task.FromResult(new TagAccessResult(true));

        public async Task SetGpoAsync(Guid readerId, GpioCommand command, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref setGpoCalls) == 1)
            {
                FirstOperationStarted.TrySetResult();
                await ReleaseFirstOperation.Task.ConfigureAwait(false);
            }

            Interlocked.Increment(ref gpoSetCount);
        }
    }
}
