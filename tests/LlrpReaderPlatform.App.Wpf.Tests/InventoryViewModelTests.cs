using System.IO;
using LlrpReaderPlatform.App.Wpf.ViewModels;
using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Tagging;
using LlrpReaderPlatform.Services.Lifecycle;
using LlrpReaderPlatform.Services.Persistence;
using LlrpReaderPlatform.TestKit;
using Sdk = LlrpSdk;
using Xunit;

namespace LlrpReaderPlatform.App.Wpf.Tests;

public sealed class InventoryViewModelTests
{
    [Fact]
    public void Inventory_column_selection_includes_epc_visibility_without_changing_identity_model()
    {
        using var vm = new InventoryViewModel(new BurstInventoryService());

        Assert.True(vm.ShowEpcColumn);
        Assert.False(vm.ShowTidColumn);
        Assert.False(vm.ShowPcBitsColumn);
        vm.ShowEpcColumn = false;

        Assert.False(vm.ShowEpcColumn);
    }

    [Fact]
    public void Inventory_duration_mode_text_tracks_continuous_timed_and_invalid_input()
    {
        using var vm = new InventoryViewModel(new BurstInventoryService());

        Assert.Equal("Continuous Mode - Runs Forever", vm.DurationModeText);

        vm.DurationSecondsText = "5";
        Assert.Equal("Duration Mode - 5 seconds", vm.DurationModeText);

        vm.DurationSecondsText = "not-a-duration";
        Assert.Equal("Invalid Duration", vm.DurationModeText);

        vm.DurationSecondsText = "0";
        Assert.Equal("Continuous Mode - Runs Forever", vm.DurationModeText);
    }

    [Fact]
    public void Changing_reader_context_reloads_only_the_selected_readers_tags()
    {
        Guid firstReaderId = Guid.NewGuid();
        Guid secondReaderId = Guid.NewGuid();
        var service = new BurstInventoryService();
        service.Seed(firstReaderId, "3001");
        service.Seed(secondReaderId, "3002");
        using var vm = new InventoryViewModel(service);

        vm.SetReaderContext(CreateReader(firstReaderId, "Reader A"));

        Assert.Equal("3001", Assert.Single(vm.Tags).Epc);

        vm.SetReaderContext(CreateReader(secondReaderId, "Reader B"));

        Assert.Equal("3002", Assert.Single(vm.Tags).Epc);

        vm.SetReaderContext(null);

        Assert.Empty(vm.Tags);
        Assert.Equal(0, vm.UniqueTagCount);
    }

    [Fact]
    public async Task Refreshing_tag_lists_reprojects_existing_inventory_rows_without_restart()
    {
        Guid readerId = Guid.NewGuid();
        var service = new BurstInventoryService();
        service.Seed(readerId, "3001");
        var store = new InMemoryTagListStore();
        using var vm = new InventoryViewModel(service, store);

        vm.SetReaderContext(CreateReader(readerId, "Reader A"));

        TagRowViewModel row = Assert.Single(vm.Tags);
        Assert.Empty(row.TagListName);

        await store.SaveAsync(new TagListDefinition
        {
            Id = Guid.NewGuid(),
            Name = "Doors",
            Entries =
            [
                new TagListEntry
                {
                    Id = Guid.NewGuid(),
                    TagListId = Guid.NewGuid(),
                    EpcHex = "3001",
                    DisplayName = "Door 1",
                },
            ],
        });

        await vm.RefreshTagLabelsAsync();

        Assert.Equal("Doors: Door 1", Assert.Single(vm.Tags).TagListName);
        Assert.Equal(0, service.StartCount);
        Assert.Equal(0, service.StopCount);
    }

    private static ReaderItemViewModel CreateReader(Guid id, string name) => new(new ReaderRuntimeSnapshot
    {
        ReaderId = id,
        Profile = new ReaderProfile { Id = id, Name = name, Host = "192.0.2.1" },
        State = ReaderState.Disconnected,
    });

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

        var recoveryProbe = new FakeSession();
        var replacement = new FakeSession();
        factory.Queue.Enqueue(recoveryProbe); // Recovery probe
        factory.Queue.Enqueue(replacement);   // Clean runtime session
        await vm.StartCommand.ExecuteAsync(profile.Id);

        Assert.True(vm.IsInventoryRunning);
        Assert.True(recoveryProbe.ConnectCount > 0);
        Assert.True(replacement.InventoryRunning);
        Assert.False(session.IsConnected);
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
    public async Task Lifecycle_stop_published_before_start_returns_is_not_overwritten()
    {
        Guid readerId = Guid.NewGuid();
        var service = new BurstInventoryService { StopBeforeStartReturns = true };
        using var vm = new InventoryViewModel(service);

        await vm.StartCommand.ExecuteAsync(readerId);

        Assert.False(vm.IsInventoryRunning);
        Assert.Contains("GPI 触发", vm.Status);
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
    public async Task Global_inventory_reports_the_reader_name_when_one_start_fails()
    {
        var factory = new FakeSessionFactory();
        await using var manager = new ReaderManager(factory, new FakeProfileStore());
        var failedProfile = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Host = "192.0.2.52",
            Name = "Reader without antenna",
            IsEnabled = true,
        };
        var healthyProfile = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Host = "192.0.2.53",
            Name = "Reader healthy",
            IsEnabled = true,
        };

        factory.Queue.Enqueue(new FakeSession());
        var failedSession = new FakeSession
        {
            StartInventoryThrows = new IOException("no antenna")
        };
        factory.Queue.Enqueue(failedSession);
        await manager.AddAsync(failedProfile, enableAfterAdding: false);

        factory.Queue.Enqueue(new FakeSession());
        var healthySession = new FakeSession();
        factory.Queue.Enqueue(healthySession);
        await manager.AddAsync(healthyProfile, enableAfterAdding: false);
        await manager.SetEnabledAsync(failedProfile.Id, enabled: true);
        await manager.SetEnabledAsync(healthyProfile.Id, enabled: true);

        using var vm = new InventoryViewModel(manager, readerManager: manager);
        await vm.StartAllCommand.ExecuteAsync(null);

        Assert.True(vm.IsInventoryRunning);
        Assert.Contains("Reader without antenna", vm.Status);
        Assert.Contains("no antenna", vm.Status);
        Assert.True(healthySession.InventoryRunning);

        await vm.StopAllCommand.ExecuteAsync(null);
        Assert.False(vm.IsInventoryRunning);
    }

    [Fact]
    public async Task Global_inventory_does_not_restart_ui_clock_after_all_readers_stop_during_start()
    {
        var factory = new FakeSessionFactory();
        await using var readerManager = new ReaderManager(factory, new FakeProfileStore());
        var profile = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Host = "192.0.2.54",
            Name = "Reader immediate stop",
            IsEnabled = true,
        };
        factory.Queue.Enqueue(new FakeSession());
        factory.Queue.Enqueue(new FakeSession());
        Assert.True((await readerManager.AddAsync(profile, enableAfterAdding: false)).Succeeded);
        await readerManager.SetEnabledAsync(profile.Id, enabled: true);

        using var vm = new InventoryViewModel(
            new BurstInventoryService { StopBeforeStartReturns = true },
            readerManager: readerManager);

        await vm.StartAllCommand.ExecuteAsync(null);
        await Task.Delay(1_100);
        vm.RefreshCommand.Execute(null);

        Assert.False(vm.IsInventoryRunning);
        Assert.Contains("GPI 触发", vm.Status);
        Assert.Equal("0.00 s", vm.Elapsed);
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
        vm.DurationSecondsText = "3";

        await vm.StartCommand.ExecuteAsync(Guid.NewGuid());

        Assert.False(vm.IsInventoryRunning);
        Assert.Equal(3, service.LastSpec?.DurationSeconds);
        Assert.Equal((ushort)1, service.LastSpec?.Report?.ReportEveryNTags);
        Assert.Contains("启动失败", vm.Status);
        Assert.Contains("设备错误", vm.Status);

        vm.DurationSecondsText = "not-a-duration";
        await vm.StartCommand.ExecuteAsync(Guid.NewGuid());

        Assert.Contains("0～86400", vm.Status);
        Assert.Equal(1, service.StartCount);
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
        Assert.True(vm.Tags.Count <= 1_000);
        await vm.StopCommand.ExecuteAsync(readerId);
    }

    private sealed class ThrowingInventoryService : IInventoryService
    {
        public bool ThrowOnStart { get; init; }
        public bool ThrowOnStop { get; init; }
        public TaskCompletionSource? StopStarted { get; init; }
        public TaskCompletionSource? StopRelease { get; init; }
        public int StopCount { get; private set; }
        public int StartCount { get; private set; }
        public InventorySpec? LastSpec { get; private set; }
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
            StartCount++;
            LastSpec = spec;
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
        private readonly Dictionary<Guid, List<TagObservation>> tags = [];

        public bool StopBeforeStartReturns { get; init; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }

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
            StartCount++;
            this.readerId = readerId;
            _ = tags.TryGetValue(readerId, out _);
            LifecycleChanged?.Invoke(this, new InventoryLifecycleChangedEventArgs(
                readerId,
                InventoryLifecycleState.Started));
            if (StopBeforeStartReturns)
            {
                LifecycleChanged?.Invoke(this, new InventoryLifecycleChangedEventArgs(
                    readerId,
                    InventoryLifecycleState.Stopped,
                    InventoryStopReason.Gpi));
            }

            return Task.FromResult(new StartInventoryResult(true));
        }

        public Task StopInventoryAsync(Guid readerId, CancellationToken ct = default)
        {
            StopCount++;
            LifecycleChanged?.Invoke(this, new InventoryLifecycleChangedEventArgs(
                readerId,
                InventoryLifecycleState.Stopped,
                InventoryStopReason.Manual));
            return Task.CompletedTask;
        }

        public IReadOnlyList<TagObservation> GetTags(Guid readerId) =>
            tags.TryGetValue(readerId, out List<TagObservation>? values) ? values : [];

        public void ClearTags(Guid readerId)
        {
            if (tags.TryGetValue(readerId, out List<TagObservation>? values))
            {
                values.Clear();
            }
        }

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
            if (!tags.TryGetValue(readerId, out List<TagObservation>? values))
            {
                values = [];
                tags[readerId] = values;
            }

            values.Add(tag);
            TagObserved?.Invoke(this, new TagObservedEventArgs(readerId, tag));
        }

        public void Seed(Guid readerId, string epc)
        {
            if (!tags.TryGetValue(readerId, out List<TagObservation>? values))
            {
                values = [];
                tags[readerId] = values;
            }

            values.Add(new TagObservation
            {
                Epc = epc,
                ReadCount = 1,
                FirstSeen = DateTimeOffset.UtcNow,
                LastSeen = DateTimeOffset.UtcNow,
            });
        }
    }
}
