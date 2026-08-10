using System.IO;
using LlrpReaderPlatform.App.Wpf.ViewModels;
using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Tagging;
using LlrpReaderPlatform.Services.Lifecycle;
using LlrpReaderPlatform.TestKit;
using Sdk = LlrpSdk;
using Xunit;

namespace LlrpReaderPlatform.App.Wpf.Tests;

public sealed class InventoryViewModelTests
{
    [Fact]
    public async Task TagReport_is_aggregated_by_services_and_projected_to_wpf_rows()
    {
        var factory = new FakeSessionFactory();
        await using var manager = new ReaderManager(factory, new FakeProfileStore());
        var profile = new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.44", Name = "Inventory" };
        factory.Queue.Enqueue(new FakeSession()); // Probe
        var session = new FakeSession();
        factory.Queue.Enqueue(session); // Registered session
        await manager.AddAsync(profile, enableAfterAdding: false);

        using var vm = new InventoryViewModel(manager);
        await vm.StartCommand.ExecuteAsync(profile.Id);

        session.EmitTag([0x30, 0x01], seenCount: 2, antenna: 1, rssi: -42);
        for (int i = 0; i < 20 && manager.GetTags(profile.Id).Count == 0; i++)
        {
            await Task.Delay(25);
        }

        vm.RefreshCommand.Execute(null);

        TagRowViewModel tag = Assert.Single(vm.Tags);
        Assert.Equal(1, tag.Index);
        Assert.Equal("3001", tag.Epc);
        Assert.Equal(2, tag.ReadCount);
        Assert.Equal((ushort)1, tag.LastAntenna);
        Assert.Equal((sbyte)-42, tag.LastRssi);
        Assert.True(vm.UniqueTagCount == 1);

        await vm.StopCommand.ExecuteAsync(profile.Id);

        Assert.False(vm.IsInventoryRunning);
        Assert.Equal("已停止盘存", vm.Status);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task TagReport_emitted_during_start_is_counted_by_wpf_metrics()
    {
        var factory = new FakeSessionFactory();
        await using var manager = new ReaderManager(factory, new FakeProfileStore());
        var profile = new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.45", Name = "EarlyTag" };
        factory.Queue.Enqueue(new FakeSession()); // Probe
        var session = new FakeSession
        {
            TagToEmitOnStart = [0x30, 0x02],
        };
        factory.Queue.Enqueue(session); // Registered session
        await manager.AddAsync(profile, enableAfterAdding: false);

        using var vm = new InventoryViewModel(manager);
        await vm.StartCommand.ExecuteAsync(profile.Id);

        for (int i = 0; i < 20 && manager.GetTags(profile.Id).Count == 0; i++)
        {
            await Task.Delay(25);
        }

        vm.RefreshCommand.Execute(null);

        Assert.Single(vm.Tags);
        Assert.NotEqual("0 tags/s", vm.TagRate);
        await vm.StopCommand.ExecuteAsync(profile.Id);
    }

    [Fact]
    public async Task Device_close_updates_wpf_inventory_state_and_stops_timer()
    {
        var factory = new FakeSessionFactory();
        await using var manager = new ReaderManager(factory, new FakeProfileStore());
        var profile = new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.46", Name = "Faulted" };
        factory.Queue.Enqueue(new FakeSession()); // Probe
        var session = new FakeSession();
        factory.Queue.Enqueue(session); // Registered session
        await manager.AddAsync(profile, enableAfterAdding: false);

        using var vm = new InventoryViewModel(manager, readerManager: manager);
        await vm.StartCommand.ExecuteAsync(profile.Id);

        session.RaiseDeviceInitiatedClosed();
        for (int i = 0; i < 20 && vm.IsInventoryRunning; i++)
        {
            await Task.Delay(25);
        }

        Assert.False(vm.IsInventoryRunning);
        Assert.Contains("连接异常", vm.Status);

        await vm.StartCommand.ExecuteAsync(profile.Id);

        Assert.True(vm.IsInventoryRunning);
        Assert.True(session.InventoryRunning);
        await vm.StopCommand.ExecuteAsync(profile.Id);
    }

    [Fact]
    public async Task Gpi_stop_lifecycle_event_updates_wpf_without_button_polling()
    {
        var factory = new FakeSessionFactory();
        await using var manager = new ReaderManager(factory, new FakeProfileStore());
        var profile = new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.51", Name = "GPI Stop" };
        factory.Queue.Enqueue(new FakeSession()); // Probe
        var session = new FakeSession
        {
            SettingsSnapshot = new Sdk.ReaderSettingsSnapshot(
                new Sdk.ReaderSettings
                {
                    Inventory = new Sdk.InventorySettings
                    {
                        StopTrigger = new Sdk.InventoryStopTrigger
                        {
                            Type = Sdk.InventoryStopTriggerType.GpiWithTimeout,
                            GpiPortNumber = 2,
                            GpiState = true,
                            TimeoutMilliseconds = 1000,
                        },
                    },
                },
                new Sdk.ManagedRoSpecSnapshot(
                    new Sdk.InventorySettings
                    {
                        StopTrigger = new Sdk.InventoryStopTrigger
                        {
                            Type = Sdk.InventoryStopTriggerType.GpiWithTimeout,
                            GpiPortNumber = 2,
                            GpiState = true,
                            TimeoutMilliseconds = 1000,
                        },
                    },
                    Sdk.InventoryRuntimeState.Disabled)),
        };
        factory.Queue.Enqueue(session); // Registered session
        await manager.AddAsync(profile, enableAfterAdding: false);

        using var vm = new InventoryViewModel(manager, readerManager: manager);
        await vm.StartCommand.ExecuteAsync(profile.Id);

        session.RaiseGpiChanged(2, state: true);
        for (int i = 0; i < 40 && vm.IsInventoryRunning; i++)
        {
            await Task.Delay(25);
        }

        Assert.False(vm.IsInventoryRunning);
        Assert.Contains("GPI 触发", vm.Status);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task Global_inventory_starts_enabled_readers_and_merges_same_epc_with_reader_names()
    {
        var factory = new FakeSessionFactory();
        await using var manager = new ReaderManager(factory, new FakeProfileStore());
        var firstProfile = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Host = "192.0.2.47",
            Name = "Reader A",
            IsEnabled = true,
        };
        var secondProfile = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Host = "192.0.2.48",
            Name = "Reader B",
            IsEnabled = true,
        };

        factory.Queue.Enqueue(new FakeSession()); // First reader probe
        var firstSession = new FakeSession();
        factory.Queue.Enqueue(firstSession); // First registered session
        await manager.AddAsync(firstProfile, enableAfterAdding: false);

        factory.Queue.Enqueue(new FakeSession()); // Second reader probe
        var secondSession = new FakeSession();
        factory.Queue.Enqueue(secondSession); // Second registered session
        await manager.AddAsync(secondProfile, enableAfterAdding: false);

        // AddAsync(false) intentionally registers the Reader as disabled. The old WPF
        // global inventory command only targets enabled Readers, so enable both profiles
        // explicitly before exercising Start All.
        await manager.SetEnabledAsync(firstProfile.Id, enabled: true);
        await manager.SetEnabledAsync(secondProfile.Id, enabled: true);

        using var vm = new InventoryViewModel(manager, readerManager: manager);
        await vm.StartAllCommand.ExecuteAsync(null);

        firstSession.EmitTag([0x30, 0x0A], seenCount: 1, antenna: 1, rssi: -40);
        secondSession.EmitTag([0x30, 0x0A], seenCount: 2, antenna: 2, rssi: -41);
        for (int i = 0; i < 40 &&
             (manager.GetTags(firstProfile.Id).Count == 0 || manager.GetTags(secondProfile.Id).Count == 0); i++)
        {
            await Task.Delay(25);
        }

        vm.RefreshCommand.Execute(null);

        TagRowViewModel row = Assert.Single(vm.Tags);
        Assert.Equal(1, row.Index);
        Assert.Equal("300A", row.Epc);
        Assert.Equal(3, row.ReadCount);
        Assert.Contains("Reader A", row.ReaderName);
        Assert.Contains("Reader B", row.ReaderName);

        await vm.StopAllCommand.ExecuteAsync(null);

        Assert.False(vm.IsInventoryRunning);
        Assert.Contains("所有 Reader", vm.Status);
        Assert.False(firstSession.IsConnected);
        Assert.False(secondSession.IsConnected);
    }

    [Fact]
    public async Task Global_inventory_keeps_other_reader_running_when_one_reader_closes()
    {
        var factory = new FakeSessionFactory();
        await using var manager = new ReaderManager(factory, new FakeProfileStore());
        var firstProfile = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Host = "192.0.2.49",
            Name = "Reader A",
            IsEnabled = true,
        };
        var secondProfile = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Host = "192.0.2.50",
            Name = "Reader B",
            IsEnabled = true,
        };

        factory.Queue.Enqueue(new FakeSession());
        var firstSession = new FakeSession();
        factory.Queue.Enqueue(firstSession);
        await manager.AddAsync(firstProfile, enableAfterAdding: false);

        factory.Queue.Enqueue(new FakeSession());
        var secondSession = new FakeSession();
        factory.Queue.Enqueue(secondSession);
        await manager.AddAsync(secondProfile, enableAfterAdding: false);
        await manager.SetEnabledAsync(firstProfile.Id, enabled: true);
        await manager.SetEnabledAsync(secondProfile.Id, enabled: true);

        using var vm = new InventoryViewModel(manager, readerManager: manager);
        await vm.StartAllCommand.ExecuteAsync(null);

        firstSession.RaiseDeviceInitiatedClosed();
        for (int i = 0; i < 40 && manager.GetSnapshot(firstProfile.Id).State != ReaderState.Faulted; i++)
        {
            await Task.Delay(25);
        }

        Assert.Equal(ReaderState.Faulted, manager.GetSnapshot(firstProfile.Id).State);
        Assert.True(vm.IsInventoryRunning);
        Assert.True(secondSession.InventoryRunning);

        secondSession.EmitTag([0x30, 0x0B], seenCount: 1, antenna: 2, rssi: -43);
        for (int i = 0; i < 40 && manager.GetTags(secondProfile.Id).Count == 0; i++)
        {
            await Task.Delay(25);
        }

        vm.RefreshCommand.Execute(null);
        TagRowViewModel row = Assert.Single(vm.Tags);
        Assert.Equal("300B", row.Epc);
        Assert.Equal("Reader B", row.ReaderName);

        await vm.StopAllCommand.ExecuteAsync(null);

        Assert.False(vm.IsInventoryRunning);
        Assert.False(secondSession.IsConnected);
    }

    [Fact]
    public async Task Start_exception_clears_active_state_and_reports_failure()
    {
        var service = new ThrowingInventoryService { ThrowOnStart = true };
        using var vm = new InventoryViewModel(service);

        await vm.StartCommand.ExecuteAsync(Guid.NewGuid());

        Assert.False(vm.IsInventoryRunning);
        Assert.Contains("启动失败", vm.Status);
    }

    [Fact]
    public async Task Stop_exception_keeps_reader_active_for_retry()
    {
        Guid readerId = Guid.NewGuid();
        var service = new ThrowingInventoryService { ThrowOnStop = true };
        using var vm = new InventoryViewModel(service);

        await vm.StartCommand.ExecuteAsync(readerId);
        await vm.StopCommand.ExecuteAsync(readerId);

        Assert.True(vm.IsInventoryRunning);
        Assert.Contains("停止 Reader", vm.Status);
    }

    [Fact]
    public async Task Stop_commands_are_rejected_while_the_first_stop_is_in_flight()
    {
        Guid readerId = Guid.NewGuid();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new ThrowingInventoryService
        {
            StopStarted = started,
            StopRelease = release,
        };
        using var vm = new InventoryViewModel(service);

        await vm.StartCommand.ExecuteAsync(readerId);
        Task firstStop = vm.StopCommand.ExecuteAsync(readerId);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(vm.IsBusy);
        await vm.StopCommand.ExecuteAsync(readerId);

        Assert.Equal(1, service.StopCount);
        Assert.Equal("盘存操作进行中，请稍候。", vm.Status);

        release.TrySetResult();
        await firstStop;

        Assert.False(vm.IsBusy);
        Assert.False(vm.IsInventoryRunning);
    }

    [Fact]
    public async Task High_frequency_tag_events_are_bounded_at_the_wpf_boundary()
    {
        Guid readerId = Guid.NewGuid();
        var service = new BurstInventoryService();
        using var vm = new InventoryViewModel(service);

        await vm.StartCommand.ExecuteAsync(readerId);
        for (int index = 0; index < 20_500; index++)
        {
            service.Emit(new TagObservation
            {
                Epc = index.ToString("X8"),
                ReadCount = 1,
                FirstSeen = DateTimeOffset.UtcNow,
                LastSeen = DateTimeOffset.UtcNow,
            });
        }

        vm.RefreshCommand.Execute(null);

        Assert.True(vm.DroppedTagReportCount >= 500);
        Assert.True(vm.Tags.Count <= 10_000);
        await vm.StopCommand.ExecuteAsync(readerId);
    }

    private sealed class ThrowingInventoryService : IInventoryService
    {
        public bool ThrowOnStart { get; init; }
        public bool ThrowOnStop { get; init; }
        public TaskCompletionSource? StopStarted { get; init; }
        public TaskCompletionSource? StopRelease { get; init; }
        public int StopCount { get; private set; }
        public long DroppedTagReportCount => 0;

        public event EventHandler<InventoryLifecycleChangedEventArgs>? LifecycleChanged;

        public event EventHandler<TagObservedEventArgs>? TagObserved
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
            CancellationToken ct = default)
        {
            if (ThrowOnStart)
            {
                throw new IOException("start failed");
            }

            LifecycleChanged?.Invoke(this, new InventoryLifecycleChangedEventArgs(
                readerId,
                InventoryLifecycleState.Started));
            return Task.FromResult(new StartInventoryResult(true));
        }

        public async Task StopInventoryAsync(Guid readerId, CancellationToken ct = default)
        {
            StopCount++;
            StopStarted?.TrySetResult();
            if (StopRelease is not null)
            {
                await StopRelease.Task;
            }

            if (ThrowOnStop)
            {
                throw new IOException("stop failed");
            }

            LifecycleChanged?.Invoke(this, new InventoryLifecycleChangedEventArgs(
                readerId,
                InventoryLifecycleState.Stopped,
                InventoryStopReason.Manual));
        }

        public IReadOnlyList<TagObservation> GetTags(Guid readerId) => [];
        public void ClearTags(Guid readerId) { }
        public Task<IReadOnlyList<GpiPortStatus>> GetGpiStatusAsync(Guid readerId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GpiPortStatus>>([]);
        public Task<IReadOnlyList<GpoPortStatus>> GetGpoStatusAsync(Guid readerId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GpoPortStatus>>([]);
        public Task<GpioStatusSnapshot> GetGpioStatusAsync(Guid readerId, CancellationToken ct = default) =>
            Task.FromResult(new GpioStatusSnapshot { Gpis = [], Gpos = [] });
        public Task<TagAccessResult> ReadTagMemoryAsync(Guid readerId, TagReadRequest request, CancellationToken ct = default) =>
            Task.FromResult(new TagAccessResult(true));
        public Task<TagAccessResult> WriteTagMemoryAsync(Guid readerId, TagWriteRequest request, CancellationToken ct = default) =>
            Task.FromResult(new TagAccessResult(true));
        public Task SetGpoAsync(Guid readerId, GpioCommand command, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class BurstInventoryService : IInventoryService
    {
        private Guid readerId;
        private readonly List<TagObservation> tags = [];

        public long DroppedTagReportCount => 0;

        public event EventHandler<InventoryLifecycleChangedEventArgs>? LifecycleChanged;

        public event EventHandler<TagObservedEventArgs>? TagObserved;
        public event EventHandler<GpiObservedEventArgs>? GpiChanged
        {
            add { }
            remove { }
        }

        public Task<StartInventoryResult> StartInventoryAsync(
            Guid readerId,
            InventorySpec spec,
            CancellationToken ct = default)
        {
            this.readerId = readerId;
            LifecycleChanged?.Invoke(this, new InventoryLifecycleChangedEventArgs(
                readerId,
                InventoryLifecycleState.Started));
            return Task.FromResult(new StartInventoryResult(true));
        }

        public Task StopInventoryAsync(Guid readerId, CancellationToken ct = default)
        {
            LifecycleChanged?.Invoke(this, new InventoryLifecycleChangedEventArgs(
                readerId,
                InventoryLifecycleState.Stopped,
                InventoryStopReason.Manual));
            return Task.CompletedTask;
        }

        public IReadOnlyList<TagObservation> GetTags(Guid readerId) => tags;
        public void ClearTags(Guid readerId) { }

        public Task<IReadOnlyList<GpiPortStatus>> GetGpiStatusAsync(
            Guid readerId,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GpiPortStatus>>([]);

        public Task<IReadOnlyList<GpoPortStatus>> GetGpoStatusAsync(
            Guid readerId,
            CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GpoPortStatus>>([]);

        public Task<GpioStatusSnapshot> GetGpioStatusAsync(
            Guid readerId,
            CancellationToken ct = default) => Task.FromResult(new GpioStatusSnapshot { Gpis = [], Gpos = [] });

        public Task<TagAccessResult> ReadTagMemoryAsync(
            Guid readerId,
            TagReadRequest request,
            CancellationToken ct = default) => Task.FromResult(new TagAccessResult(true));

        public Task<TagAccessResult> WriteTagMemoryAsync(
            Guid readerId,
            TagWriteRequest request,
            CancellationToken ct = default) => Task.FromResult(new TagAccessResult(true));

        public Task SetGpoAsync(Guid readerId, GpioCommand command, CancellationToken ct = default) => Task.CompletedTask;

        public void Emit(TagObservation tag)
        {
            tags.Add(tag);
            TagObserved?.Invoke(this, new TagObservedEventArgs(readerId, tag));
        }
    }
}
