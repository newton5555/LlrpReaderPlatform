using LlrpReaderPlatform.App.Wpf.ViewModels;
using LlrpReaderPlatform.Contracts.Errors;
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
    public async Task Queued_gpo_operation_is_dropped_when_reader_context_changes()
    {
        var service = new SerializingInventoryService();
        using var vm = new DiagnosticsViewModel(service);
        Guid firstReaderId = Guid.NewGuid();
        Guid secondReaderId = Guid.NewGuid();
        vm.SelectReader(firstReaderId);

        Task first = Task.Run(async () => await vm.SetGpoCommand.ExecuteAsync(firstReaderId));
        await service.FirstOperationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task queued = Task.Run(async () => await vm.SetGpoCommand.ExecuteAsync(firstReaderId));
        await Task.Delay(25);

        vm.SelectReader(secondReaderId);
        service.ReleaseFirstOperation.TrySetResult();
        await Task.WhenAll(first, queued).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, service.GpoSetCount);
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
    public async Task Failed_older_gpo_intent_does_not_rollback_a_newer_intent()
    {
        var factory = new FakeSessionFactory();
        await using var manager = new ReaderManager(factory, new FakeProfileStore());
        var profile = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Name = "Queued GPO Reader",
            Host = "192.0.2.95",
        };
        factory.Queue.Enqueue(new FakeSession()); // Probe
        var firstOperationEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstOperation = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSession
        {
            SetGpoThrows = new IOException("GPO rejected"),
            BeforeSetGpoAsync = async () =>
            {
                firstOperationEntered.TrySetResult(true);
                await releaseFirstOperation.Task.ConfigureAwait(false);
            },
        };
        factory.Factory = static _ => new FakeSession
        {
            SetGpoThrows = new IOException("GPO rejected"),
        };
        factory.Queue.Enqueue(session); // Registered session
        await manager.AddAsync(profile, enableAfterAdding: false);

        using var vm = new DiagnosticsViewModel(manager);
        vm.SelectReader(profile.Id);
        vm.Gpo1 = true;
        await firstOperationEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // The second switch is queued behind the first and is also rejected.
        // The final UI value must return to the last confirmed device value
        // (OFF), not the first operation's old value (ON).
        vm.Gpo1 = false;
        releaseFirstOperation.TrySetResult(true);

        for (int i = 0; i < 80 && vm.IsBusy; i++)
        {
            await Task.Delay(25);
        }

        Assert.False(vm.IsBusy);
        Assert.False(vm.Gpo1);
        Assert.Contains("GPO 1 操作失败", vm.Status);
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
        Assert.True(vm.IsGpiStatusAvailable);
        Assert.Equal("已读取 1 个 GPI、1 个 GPO 状态。", vm.Status);
        Assert.False(session.IsConnected);
        Assert.Equal(1, session.SettingsQueryCount);
    }

    [Fact]
    public async Task Reapplying_same_reader_context_preserves_gpio_projection()
    {
        var service = new SerializingInventoryService();
        using var vm = new DiagnosticsViewModel(service);
        Guid readerId = Guid.NewGuid();
        var catalog = new ReaderFeatureCatalog
        {
            SupportedFeatures =
            [
                ReaderFeatures.StandardSettings,
                ReaderFeatures.StandardInventory,
                ReaderFeatures.StandardGpo,
            ],
        };

        vm.SelectReader(readerId, catalog, gpiCount: null, gpoCount: 1);
        vm.Gpis.Add(new GpiPortStatus
        {
            PortNumber = 1,
            Configured = true,
            State = true,
        });
        service.ReleaseFirstOperation.TrySetResult();
        vm.Gpo1 = true;

        for (int i = 0; i < 40 && vm.IsBusy; i++)
        {
            await Task.Delay(25);
        }

        vm.SelectReader(readerId, catalog, gpiCount: null, gpoCount: 1);

        Assert.True(vm.Gpo1);
        Assert.True(Assert.Single(vm.Gpis).State);
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
    public async Task Refresh_gpio_preserves_platform_unsupported_error()
    {
        var service = new SerializingInventoryService
        {
            GpioException = new PlatformOperationException(
                PlatformErrorCode.Unsupported,
                "Reader does not advertise standard GPIO capability."),
        };
        using var vm = new DiagnosticsViewModel(service);
        Guid readerId = Guid.NewGuid();
        vm.SelectReader(readerId);

        await vm.RefreshGpioCommand.ExecuteAsync(readerId);

        Assert.Equal(
            "读取 GPI/GPO失败（设备不支持）：Reader does not advertise standard GPIO capability.",
            vm.Status);
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
        DateTimeOffset timestamp = new(2026, 8, 10, 12, 34, 56, TimeSpan.Zero);
        session.RaiseGpiChanged(1, state: true, timestamp);

        for (int i = 0; i < 40 && (!vm.Gpis.Any() || !vm.Gpis[0].State); i++)
        {
            await Task.Delay(25);
        }

        Assert.Equal(4, vm.Gpis.Count);
        var status = vm.Gpis[0];
        Assert.Equal((ushort)1, status.PortNumber);
        Assert.True(status.State);
        Assert.Equal(timestamp, status.Timestamp);
        Assert.Contains("GPI 1 已变为 High", vm.Status);
        Assert.Contains("Reader 时间：", vm.Status);
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
        Assert.False(vm.IsGpoControlVisible);
        Assert.False(vm.IsGpioRefreshAvailable);
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
        Assert.True(vm.IsGpoControlVisible);
        Assert.True(vm.IsGpioRefreshAvailable);
        Assert.False(vm.IsGpo3Available);
        Assert.False(vm.IsGpo4Available);
        Assert.False(vm.Gpo3);
        Assert.Equal("当前 Reader 只有 2 个 GPO，端口 3 不可用。", vm.Status);
    }

    [Fact]
    public void Zero_gpi_count_disables_gpi_status_surface_but_keeps_gpo_surface()
    {
        using var vm = new DiagnosticsViewModel(new SerializingInventoryService());
        vm.SelectReader(
            Guid.NewGuid(),
            new ReaderFeatureCatalog
            {
                SupportedFeatures =
                [
                    ReaderFeatures.StandardSettings,
                    ReaderFeatures.StandardInventory,
                    ReaderFeatures.StandardGpi,
                    ReaderFeatures.StandardGpo,
                ],
            },
            gpiCount: 0,
            gpoCount: 2);

        Assert.False(vm.IsGpiStatusAvailable);
        Assert.True(vm.IsGpo1Available);
        Assert.True(vm.IsGpo2Available);
        Assert.False(vm.IsGpiStatusAvailable);
        Assert.True(vm.IsGpoControlVisible);
        Assert.True(vm.IsGpioRefreshAvailable);
        Assert.False(vm.IsGpo3Available);
    }

    [Fact]
    public async Task Zero_gpo_port_is_rejected_before_calling_service()
    {
        var service = new SerializingInventoryService();
        using var vm = new DiagnosticsViewModel(service);
        Guid readerId = Guid.NewGuid();
        vm.SelectReader(readerId, new ReaderFeatureCatalog
        {
            SupportedFeatures = [ReaderFeatures.StandardGpo],
        }, gpoCount: 2);
        vm.PortNumber = 0;

        await vm.SetGpoCommand.ExecuteAsync(readerId);

        Assert.Equal("GPO 端口必须从 1 开始。", vm.Status);
        Assert.Equal(0, service.GpoSetCount);
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

        public Exception? GpioException { get; init; }

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
            CancellationToken ct = default) => GpioException is null
            ? Task.FromResult(new GpioStatusSnapshot { Gpis = [], Gpos = [] })
            : Task.FromException<GpioStatusSnapshot>(GpioException);

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
