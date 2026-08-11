using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Errors;
using LlrpReaderPlatform.Contracts.Tagging;
using LlrpReaderPlatform.Services.Lifecycle;
using LlrpReaderPlatform.Services.Settings;
using LlrpReaderPlatform.Services.Extensions;
using LlrpReaderPlatform.TestKit;
using LlrpSdk;
using Xunit;
using Tagging = LlrpReaderPlatform.Contracts.Tagging;

namespace LlrpReaderPlatform.Services.Tests.Lifecycle;

public sealed class InventoryReaderManagerTests
{
    private sealed class Harness
    {
        public Harness()
        {
            SessionFactory = new FakeSessionFactory();
            Manager = new ReaderManager(SessionFactory, new FakeProfileStore());
            Profile = new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.3" };
        }

        public FakeSessionFactory SessionFactory { get; }
        public ReaderManager Manager { get; }
        public ReaderProfile Profile { get; }

        public FakeSession Register()
        {
            var session = new FakeSession();
            SessionFactory.Queue.Enqueue(new FakeSession()); // probe
            SessionFactory.Queue.Enqueue(session);           // register
            Manager.AddAsync(Profile, enableAfterAdding: false).GetAwaiter().GetResult();
            return session;
        }
    }

    private static readonly Tagging.InventorySpec Spec = new() { Antennas = [1] };

    [Fact]
    public async Task StartInventory_sets_inventorying_and_holds_connection()
    {
        var h = new Harness();
        FakeSession session = h.Register();

        Tagging.StartInventoryResult result = await h.Manager.StartInventoryAsync(h.Profile.Id, Spec);

        Assert.True(result.Succeeded);
        Assert.True(session.IsConnected);
        Assert.True(session.IsConnected);
        Assert.Equal(1, session.ConnectCount);
        Assert.Equal(0, session.DisconnectCount);
        Assert.Equal(new ushort[] { 1 }, session.LastStartedInventorySettings?.AntennaIds);
        Assert.Equal(ReaderState.Inventorying, h.Manager.GetSnapshot(h.Profile.Id).State);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(86_401)]
    public async Task StartInventory_invalid_duration_returns_invalid_settings_without_connecting(int durationSeconds)
    {
        var h = new Harness();
        FakeSession session = h.Register();

        StartInventoryResult result = await h.Manager.StartInventoryAsync(
            h.Profile.Id,
            new InventorySpec { DurationSeconds = durationSeconds });

        Assert.False(result.Succeeded);
        Assert.Equal(InventoryError.InvalidSettings, result.Error);
        Assert.Equal(PlatformErrorCode.InvalidSettings, result.ErrorCode);
        Assert.Equal(0, session.ConnectCount);
        Assert.Equal(ReaderState.Disconnected, h.Manager.GetSnapshot(h.Profile.Id).State);
    }

    [Fact]
    public async Task StartInventory_rejects_mixed_all_and_specific_antennas_without_connecting()
    {
        var h = new Harness();
        FakeSession session = h.Register();

        StartInventoryResult result = await h.Manager.StartInventoryAsync(
            h.Profile.Id,
            new InventorySpec { Antennas = [0, 1] });

        Assert.False(result.Succeeded);
        Assert.Equal(InventoryError.InvalidSettings, result.Error);
        Assert.Equal(PlatformErrorCode.InvalidSettings, result.ErrorCode);
        Assert.Contains("全部天线", result.Message);
        Assert.Equal(0, session.ConnectCount);
    }

    [Fact]
    public async Task StartInventory_rejects_antenna_above_advertised_count_after_connecting_for_capabilities()
    {
        var h = new Harness();
        FakeSession session = new();
        session.SetCapabilities(maxNumberOfAntennas: 2);
        h.SessionFactory.Queue.Enqueue(new FakeSession()); // probe
        h.SessionFactory.Queue.Enqueue(session);           // register
        Assert.True((await h.Manager.AddAsync(h.Profile, enableAfterAdding: false)).Succeeded);

        StartInventoryResult result = await h.Manager.StartInventoryAsync(
            h.Profile.Id,
            new InventorySpec { Antennas = [3] });

        Assert.False(result.Succeeded);
        Assert.Equal(InventoryError.InvalidSettings, result.Error);
        Assert.Equal(PlatformErrorCode.InvalidSettings, result.ErrorCode);
        Assert.Contains("天线 3", result.Message);
        Assert.Equal(1, session.ConnectCount);
        Assert.Equal(1, session.DisconnectCount);
        Assert.Null(session.LastStartedInventorySettings);
        Assert.Equal(ReaderState.Disconnected, h.Manager.GetSnapshot(h.Profile.Id).State);
    }

    [Fact]
    public async Task StopInventory_publishes_stopping_and_disconnecting_states()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        Tagging.StartInventoryResult started = await h.Manager.StartInventoryAsync(h.Profile.Id, Spec);
        Assert.True(started.Succeeded);

        List<ReaderState> states = [];
        h.Manager.StateChanged += (_, args) => states.Add(args.Snapshot.State);

        await h.Manager.StopInventoryAsync(h.Profile.Id);

        Assert.Equal(
            [ReaderState.Stopping, ReaderState.Disconnecting, ReaderState.Disconnected],
            states);
        Assert.False(session.IsConnected);
        await h.Manager.DisposeAsync();
    }

    [Fact]
    public async Task StartInventory_applies_report_field_override_without_reconnecting()
    {
        var h = new Harness();
        FakeSession session = h.Register();

        Tagging.StartInventoryResult result = await h.Manager.StartInventoryAsync(h.Profile.Id, new Tagging.InventorySpec
        {
            Report = new Tagging.InventoryReportSpec
            {
                IncludeAntennaId = false,
                IncludePeakRssi = false,
                IncludePcBits = true,
            },
        });

        Assert.True(result.Succeeded);
        Assert.False(session.LastStartedInventorySettings?.Report.IncludeAntennaId);
        Assert.False(session.LastStartedInventorySettings?.Report.IncludePeakRssi);
        Assert.True(session.LastStartedInventorySettings?.Report.IncludePcBits);
        Assert.Equal(1, session.ConnectCount);
    }

    [Fact]
    public async Task StartInventory_uses_configured_gpi_start_and_stop_triggers()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        InventorySettings configured = new()
        {
            StartTrigger = new InventoryStartTrigger
            {
                Type = InventoryStartTriggerType.Gpi,
                GpiPortNumber = 2,
                GpiState = true,
            },
            StopTrigger = new InventoryStopTrigger
            {
                Type = InventoryStopTriggerType.GpiWithTimeout,
                GpiPortNumber = 3,
                GpiState = false,
                TimeoutMilliseconds = 1500,
            },
        };
        session.SettingsSnapshot = new ReaderSettingsSnapshot(
            new ReaderSettings { Inventory = configured },
            new ManagedRoSpecSnapshot(configured, InventoryRuntimeState.Disabled));

        Tagging.StartInventoryResult result = await h.Manager.StartInventoryAsync(h.Profile.Id, Spec);

        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull(session.LastStartedInventorySettings);
        InventorySettings started = session.LastStartedInventorySettings!;
        Assert.Equal(InventoryStartTriggerType.Gpi, started.StartTrigger.Type);
        Assert.Equal((ushort)2, started.StartTrigger.GpiPortNumber);
        Assert.True(started.StartTrigger.GpiState);
        Assert.Equal(InventoryStopTriggerType.GpiWithTimeout, started.StopTrigger.Type);
        Assert.Equal((ushort)3, started.StopTrigger.GpiPortNumber);
        Assert.False(started.StopTrigger.GpiState);
        Assert.Equal((uint)1500, started.StopTrigger.TimeoutMilliseconds);

        await h.Manager.StopInventoryAsync(h.Profile.Id);
    }

    [Fact]
    public async Task StartInventory_antenna_override_trims_unselected_antenna_configurations()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        InventorySettings baseline = new()
        {
            AntennaIds = [1, 2],
            AntennaConfigurations =
            [
                new InventoryAntennaConfiguration { AntennaId = 0, TransmitPowerIndex = 1 },
                new InventoryAntennaConfiguration { AntennaId = 1, TransmitPowerIndex = 2 },
                new InventoryAntennaConfiguration { AntennaId = 2, TransmitPowerIndex = 3 },
            ],
        };
        session.SettingsSnapshot = new ReaderSettingsSnapshot(
            new ReaderSettings { Inventory = baseline },
            new ManagedRoSpecSnapshot(baseline, InventoryRuntimeState.Disabled));

        Tagging.StartInventoryResult result = await h.Manager.StartInventoryAsync(
            h.Profile.Id,
            new Tagging.InventorySpec { Antennas = [1] });

        Assert.True(result.Succeeded);
        Assert.Equal(new ushort[] { 1 }, session.LastStartedInventorySettings?.AntennaIds);
        Assert.Equal(
            new ushort[] { 0, 1 },
            session.LastStartedInventorySettings?.AntennaConfigurations.Select(static x => x.AntennaId));
        await h.Manager.StopInventoryAsync(h.Profile.Id);
    }

    [Fact]
    public async Task StartInventory_twice_returns_ReaderBusy()
    {
        var h = new Harness();
        h.Register();
        await h.Manager.StartInventoryAsync(h.Profile.Id, Spec);

        Tagging.StartInventoryResult second = await h.Manager.StartInventoryAsync(h.Profile.Id, Spec);

        Assert.False(second.Succeeded);
        Assert.Equal(Tagging.InventoryError.ReaderBusy, second.Error);
    }

    [Fact]
    public async Task StartInventory_device_failure_returns_DeviceFailed()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        session.StartInventoryThrows = new IOException("rospect failed");

        Tagging.StartInventoryResult result = await h.Manager.StartInventoryAsync(h.Profile.Id, Spec);

        Assert.False(result.Succeeded);
        Assert.Equal(Tagging.InventoryError.DeviceFailed, result.Error);
        Assert.False(session.IsConnected);
        ReaderRuntimeSnapshot snapshot = h.Manager.GetSnapshot(h.Profile.Id);
        Assert.Equal(ReaderState.Faulted, snapshot.State);
        Assert.True(snapshot.IsStale);
        Assert.Contains("rospect failed", snapshot.Error);
    }

    [Fact]
    public async Task StartInventory_connection_failure_cleans_half_open_session()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        session.ConnectThrows = new IOException("connect refused");

        Tagging.StartInventoryResult result = await h.Manager.StartInventoryAsync(h.Profile.Id, Spec);

        Assert.False(result.Succeeded);
        Assert.Equal(Tagging.InventoryError.DeviceFailed, result.Error);
        Assert.False(session.IsConnected);
        Assert.Equal(ReaderState.Faulted, h.Manager.GetSnapshot(h.Profile.Id).State);
    }

    [Fact]
    public async Task StartInventory_cancellation_cleans_up_and_propagates()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        using var cancellation = new CancellationTokenSource();
        session.BeforeStartInventory = cancellation.Cancel;
        session.StartInventoryThrows = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Manager.StartInventoryAsync(h.Profile.Id, Spec, cancellation.Token));

        Assert.False(session.IsConnected);
        Assert.False(session.InventoryRunning);
        Assert.Equal(ReaderState.Disconnected, h.Manager.GetSnapshot(h.Profile.Id).State);
    }

    [Fact]
    public async Task StartInventory_cancellation_during_extension_probe_cleans_up_and_propagates()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        session.RaiseConnectionFaulted("force extension reprobe");
        for (int i = 0; i < 40 && h.Manager.GetSnapshot(h.Profile.Id).State != ReaderState.Faulted; i++)
        {
            await Task.Delay(10);
        }

        using var cancellation = new CancellationTokenSource();
        FakeSession probe = new()
        {
            BeforeConnect = cancellation.Cancel,
            ConnectThrows = new OperationCanceledException(),
        };
        h.SessionFactory.Queue.Enqueue(probe);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Manager.StartInventoryAsync(h.Profile.Id, Spec, cancellation.Token));

        Assert.False(session.IsConnected);
        Assert.False(probe.IsConnected);
        Assert.False(session.InventoryRunning);
        ReaderRuntimeSnapshot snapshot = h.Manager.GetSnapshot(h.Profile.Id);
        Assert.Equal(ReaderState.Disconnected, snapshot.State);
        Assert.True(snapshot.IsStale);
        Assert.True(session.DisposeCount > 0);
        Assert.True(probe.DisposeCount > 0);
    }

    [Fact]
    public async Task Activate_cancellation_during_extension_probe_recreates_session_and_marks_capabilities_stale()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        session.RaiseConnectionFaulted("force extension reprobe");
        for (int i = 0; i < 40 && h.Manager.GetSnapshot(h.Profile.Id).State != ReaderState.Faulted; i++)
        {
            await Task.Delay(10);
        }

        using var cancellation = new CancellationTokenSource();
        FakeSession probe = new()
        {
            BeforeConnect = cancellation.Cancel,
            ConnectThrows = new OperationCanceledException(),
        };
        h.SessionFactory.Queue.Enqueue(probe);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Manager.ActivateAsync(h.Profile.Id, cancellation.Token));

        ReaderRuntimeSnapshot snapshot = h.Manager.GetSnapshot(h.Profile.Id);
        Assert.False(session.IsConnected);
        Assert.False(probe.IsConnected);
        Assert.True(session.DisposeCount > 0);
        Assert.Equal(ReaderState.Disconnected, snapshot.State);
        Assert.True(snapshot.IsStale);
        Assert.Null(snapshot.Error);
        await h.Manager.DisposeAsync();
    }

    [Fact]
    public async Task Activate_after_cancellation_reprobes_and_recovers_capabilities()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        session.RaiseConnectionFaulted("force extension reprobe");
        for (int i = 0; i < 40 && h.Manager.GetSnapshot(h.Profile.Id).State != ReaderState.Faulted; i++)
        {
            await Task.Delay(10);
        }

        using var cancellation = new CancellationTokenSource();
        FakeSession cancelledProbe = new()
        {
            BeforeConnect = cancellation.Cancel,
            ConnectThrows = new OperationCanceledException(),
        };
        h.SessionFactory.Queue.Enqueue(cancelledProbe);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Manager.ActivateAsync(h.Profile.Id, cancellation.Token));

        FakeSession recoveryProbe = new();
        h.SessionFactory.Queue.Enqueue(recoveryProbe);

        ReaderActivationResult result = await h.Manager.ActivateAsync(h.Profile.Id);

        Assert.True(result.Succeeded);
        Assert.True(recoveryProbe.DisposeCount > 0);
        ReaderRuntimeSnapshot snapshot = h.Manager.GetSnapshot(h.Profile.Id);
        Assert.Equal(ReaderState.Disconnected, snapshot.State);
        Assert.False(snapshot.IsStale);
        Assert.True(snapshot.CapabilityRevision > 0);
        await h.Manager.DisposeAsync();
    }

    [Fact]
    public async Task Gpio_short_operation_cancellation_during_extension_probe_recreates_session()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        session.RaiseConnectionFaulted("force extension reprobe");
        for (int i = 0; i < 40 && h.Manager.GetSnapshot(h.Profile.Id).State != ReaderState.Faulted; i++)
        {
            await Task.Delay(10);
        }

        using var cancellation = new CancellationTokenSource();
        FakeSession probe = new()
        {
            BeforeConnect = cancellation.Cancel,
            ConnectThrows = new OperationCanceledException(),
        };
        h.SessionFactory.Queue.Enqueue(probe);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Manager.GetGpioStatusAsync(h.Profile.Id, cancellation.Token));

        ReaderRuntimeSnapshot snapshot = h.Manager.GetSnapshot(h.Profile.Id);
        Assert.False(session.IsConnected);
        Assert.False(probe.IsConnected);
        Assert.True(session.DisposeCount > 0);
        Assert.True(probe.DisposeCount > 0);
        Assert.Equal(ReaderState.Disconnected, snapshot.State);
        Assert.True(snapshot.IsStale);
        Assert.Null(snapshot.Error);
        await h.Manager.DisposeAsync();
    }

    [Fact]
    public async Task StopInventory_disconnect_failure_marks_reader_faulted_and_stale()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        await h.Manager.StartInventoryAsync(h.Profile.Id, Spec);
        session.DisconnectThrows = new IOException("disconnect failed");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Manager.StopInventoryAsync(h.Profile.Id));

        Assert.Contains("disconnect", exception.Message, StringComparison.OrdinalIgnoreCase);
        ReaderRuntimeSnapshot snapshot = h.Manager.GetSnapshot(h.Profile.Id);
        Assert.Equal(ReaderState.Faulted, snapshot.State);
        Assert.True(snapshot.IsStale);
        Assert.Contains("disconnect failed", snapshot.Error);
        Assert.False(session.InventoryRunning);
    }

    [Fact]
    public async Task Activate_disconnect_failure_returns_device_failure_and_stale_snapshot()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        session.DisconnectThrows = new IOException("activation disconnect failed");

        ReaderActivationResult result = await h.Manager.ActivateAsync(h.Profile.Id);

        Assert.False(result.Succeeded);
        Assert.Equal(PlatformErrorCode.DeviceFailed, result.ErrorCode);
        ReaderRuntimeSnapshot snapshot = h.Manager.GetSnapshot(h.Profile.Id);
        Assert.Equal(ReaderState.Faulted, snapshot.State);
        Assert.True(snapshot.IsStale);
        Assert.Contains("activation disconnect failed", snapshot.Error);
    }

    [Fact]
    public async Task Deactivate_disconnect_failure_marks_reader_faulted_and_stale()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        await h.Manager.StartInventoryAsync(h.Profile.Id, Spec);
        session.DisconnectThrows = new IOException("deactivation disconnect failed");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Manager.DeactivateAsync(h.Profile.Id));

        Assert.Contains("deactivate", exception.Message, StringComparison.OrdinalIgnoreCase);
        ReaderRuntimeSnapshot snapshot = h.Manager.GetSnapshot(h.Profile.Id);
        Assert.Equal(ReaderState.Faulted, snapshot.State);
        Assert.True(snapshot.IsStale);
        Assert.Contains("deactivation disconnect failed", snapshot.Error);
        Assert.False(session.InventoryRunning);
    }

    [Fact]
    public async Task Deactivate_stop_failure_marks_reader_faulted_and_stale()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        await h.Manager.StartInventoryAsync(h.Profile.Id, Spec);
        session.StopInventoryThrows = new IOException("deactivation stop failed");

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Manager.DeactivateAsync(h.Profile.Id));

        Assert.Contains("deactivate", exception.Message, StringComparison.OrdinalIgnoreCase);
        ReaderRuntimeSnapshot snapshot = h.Manager.GetSnapshot(h.Profile.Id);
        Assert.Equal(ReaderState.Faulted, snapshot.State);
        Assert.True(snapshot.IsStale);
        Assert.Contains("deactivation stop failed", snapshot.Error);
        Assert.False(session.InventoryRunning);
    }

    [Fact]
    public async Task ReadTagMemory_during_inventory_returns_busy()
    {
        var h = new Harness();
        h.Register();
        await h.Manager.StartInventoryAsync(h.Profile.Id, Spec);

        Tagging.TagAccessResult result = await h.Manager.ReadTagMemoryAsync(
            h.Profile.Id, new Tagging.TagReadRequest { Epc = "00" });

        Assert.False(result.Succeeded);
        Assert.Contains("busy", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PlatformErrorCode.ReaderBusy, result.ErrorCode);
    }

    [Fact]
    public async Task ReadTagMemory_invalid_request_returns_before_connecting()
    {
        var h = new Harness();
        FakeSession session = h.Register();

        Tagging.TagAccessResult result = await h.Manager.ReadTagMemoryAsync(
            h.Profile.Id,
            new Tagging.TagReadRequest { Epc = "3001", WordCount = 0 });

        Assert.False(result.Succeeded);
        Assert.Contains("字数", result.Error);
        Assert.Equal(PlatformErrorCode.InvalidSettings, result.ErrorCode);
        Assert.Equal(0, session.ConnectCount);
        Assert.Equal(0, session.ReadTagMemoryCount);
    }

    [Fact]
    public async Task ReadTagMemory_returns_explicit_unsupported_error_when_reader_lacks_tag_access()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        session.SetCapabilities(isTagAccessAvailable: false);

        Tagging.TagAccessResult result = await h.Manager.ReadTagMemoryAsync(
            h.Profile.Id,
            new Tagging.TagReadRequest { Epc = "3001" });

        Assert.False(result.Succeeded);
        Assert.Contains("Tag Access", result.Error, StringComparison.Ordinal);
        Assert.Equal(PlatformErrorCode.Unsupported, result.ErrorCode);
        Assert.Equal(0, session.ReadTagMemoryCount);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task WriteTagMemory_invalid_request_returns_before_connecting()
    {
        var h = new Harness();
        FakeSession session = h.Register();

        Tagging.TagAccessResult result = await h.Manager.WriteTagMemoryAsync(
            h.Profile.Id,
            new Tagging.TagWriteRequest { Epc = "", DataHex = "0AB0" });

        Assert.False(result.Succeeded);
        Assert.Contains("目标", result.Error);
        Assert.Equal(0, session.ConnectCount);
        Assert.Equal(0, session.WriteTagMemoryCount);
    }

    [Fact]
    public async Task WriteTagMemory_success_uses_short_operation_and_returns_to_disconnected()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        session.TagAccessResult = new Tagging.TagAccessResult(true);

        Tagging.TagWriteRequest request = new()
        {
            Epc = "E201E24F3E0B0E1CFAAF8700",
            SelectionBank = Tagging.TagMemoryBank.Epc,
            MemoryBank = Tagging.TagMemoryBank.User,
            OffsetWords = 4,
            DataHex = "1234ABCD",
            AccessPasswordHex = "00000000",
        };

        Tagging.TagAccessResult result = await h.Manager.WriteTagMemoryAsync(h.Profile.Id, request);

        Assert.True(result.Succeeded);
        Assert.Equal(1, session.WriteTagMemoryCount);
        Assert.Equal(request, session.LastTagWriteRequest);
        Assert.False(session.IsConnected);
        ReaderRuntimeSnapshot snapshot = h.Manager.GetSnapshot(h.Profile.Id);
        Assert.Equal(ReaderState.Disconnected, snapshot.State);
        Assert.False(snapshot.IsStale);
        Assert.True(snapshot.CapabilityRevision > 0);
    }

    [Theory]
    [InlineData(Tagging.TagMemoryBank.Reserved)]
    [InlineData(Tagging.TagMemoryBank.Epc)]
    [InlineData(Tagging.TagMemoryBank.Tid)]
    [InlineData(Tagging.TagMemoryBank.User)]
    public async Task TagAccess_supports_each_standard_memory_bank(Tagging.TagMemoryBank memoryBank)
    {
        var h = new Harness();
        await using ReaderManager manager = h.Manager;
        FakeSession session = h.Register();
        session.TagAccessResult = new Tagging.TagAccessResult(true, DataHex: "A55A");
        string target = memoryBank == Tagging.TagMemoryBank.Tid ? "E2003412" : "3001";
        Tagging.TagMemoryBank selectionBank = memoryBank == Tagging.TagMemoryBank.Tid
            ? Tagging.TagMemoryBank.Tid
            : Tagging.TagMemoryBank.Epc;

        Tagging.TagAccessResult read = await manager.ReadTagMemoryAsync(
            h.Profile.Id,
            new Tagging.TagReadRequest
            {
                Epc = target,
                SelectionBank = selectionBank,
                MemoryBank = memoryBank,
                OffsetWords = 1,
                WordCount = 1,
            });
        Tagging.TagAccessResult write = await manager.WriteTagMemoryAsync(
            h.Profile.Id,
            new Tagging.TagWriteRequest
            {
                Epc = target,
                SelectionBank = selectionBank,
                MemoryBank = memoryBank,
                OffsetWords = 1,
                DataHex = "A55A",
            });

        Assert.True(read.Succeeded);
        Assert.Equal("A55A", read.DataHex);
        Assert.True(write.Succeeded);
        Assert.Equal(memoryBank, session.LastTagReadRequest?.MemoryBank);
        Assert.Equal(memoryBank, session.LastTagWriteRequest?.MemoryBank);
        Assert.Equal((ushort)1, session.LastTagReadRequest?.OffsetWords);
        Assert.Equal((ushort)1, session.LastTagWriteRequest?.OffsetWords);
        Assert.Equal(2, session.ConnectCount);
        Assert.Equal(2, session.DisconnectCount);
    }

    [Fact]
    public async Task StopInventory_stops_and_disconnects()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        await h.Manager.StartInventoryAsync(h.Profile.Id, Spec);

        await h.Manager.StopInventoryAsync(h.Profile.Id);

        Assert.False(session.InventoryRunning);
        Assert.False(session.IsConnected);
        Assert.Equal(1, session.DisconnectCount);
        Assert.Equal(ReaderState.Disconnected, h.Manager.GetSnapshot(h.Profile.Id).State);
    }

    [Fact]
    public async Task StopInventory_failure_is_reported_and_reader_becomes_faulted()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        await h.Manager.StartInventoryAsync(h.Profile.Id, Spec);
        session.StopInventoryThrows = new IOException("stop command rejected");

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => h.Manager.StopInventoryAsync(h.Profile.Id));

        Assert.Contains("Failed to stop inventory", error.Message);
        Assert.Equal(1, session.StopInventoryCount);
        Assert.False(session.IsConnected);
        Assert.Equal(ReaderState.Faulted, h.Manager.GetSnapshot(h.Profile.Id).State);
        Assert.Contains("stop command rejected", h.Manager.GetSnapshot(h.Profile.Id).Error);
    }

    [Fact]
    public async Task StopInventory_cancellation_cleans_up_and_propagates()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        await h.Manager.StartInventoryAsync(h.Profile.Id, Spec);
        using var cancellation = new CancellationTokenSource();
        session.BeforeStopInventory = cancellation.Cancel;
        session.StopInventoryThrows = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Manager.StopInventoryAsync(h.Profile.Id, cancellation.Token));

        Assert.False(session.IsConnected);
        Assert.False(session.InventoryRunning);
        Assert.Equal(ReaderState.Faulted, h.Manager.GetSnapshot(h.Profile.Id).State);
    }

    [Fact]
    public async Task Start_and_stop_persist_one_complete_inventory_run()
    {
        var factory = new FakeSessionFactory();
        var profileStore = new FakeProfileStore();
        var runStore = new FakeInventoryRunStore();
        var probe = new FakeSession();
        var session = new FakeSession();
        session.TagToEmitOnStart = [0x30, 0x08, 0x33, 0xB2];
        factory.Queue.Enqueue(probe);
        factory.Queue.Enqueue(session);
        await using var manager = new ReaderManager(factory, profileStore, runStore: runStore);
        var profile = new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.70" };

        await manager.AddAsync(profile, enableAfterAdding: false);
        Tagging.StartInventoryResult started = await manager.StartInventoryAsync(profile.Id, new Tagging.InventorySpec());
        Assert.True(started.Succeeded);
        Assert.Single(runStore.Runs);
        Assert.Null(runStore.Runs[0].EndedAtUtc);

        await manager.StopInventoryAsync(profile.Id);

        InventoryRunRecord completed = Assert.Single(runStore.Runs);
        Assert.NotNull(completed.EndedAtUtc);
        Assert.Equal("Manual", completed.StopReason);
        Assert.Equal(1, completed.UniqueTagCount);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task Timed_inventory_stop_persists_duration_reason()
    {
        var factory = new FakeSessionFactory();
        var profileStore = new FakeProfileStore();
        var runStore = new FakeInventoryRunStore();
        factory.Queue.Enqueue(new FakeSession()); // probe
        factory.Queue.Enqueue(new FakeSession()); // registered session
        await using var manager = new ReaderManager(factory, profileStore, runStore: runStore);
        var profile = new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.72" };

        await manager.AddAsync(profile, enableAfterAdding: false);
        Tagging.StartInventoryResult started = await manager.StartInventoryAsync(
            profile.Id,
            new Tagging.InventorySpec { DurationSeconds = 1 });
        Assert.True(started.Succeeded);

        for (int i = 0; i < 30 && (await runStore.GetForReaderAsync(profile.Id)).Single().EndedAtUtc is null; i++)
        {
            await Task.Delay(100);
        }

        InventoryRunRecord completed = Assert.Single(runStore.Runs);
        Assert.NotNull(completed.EndedAtUtc);
        Assert.Equal("Duration", completed.StopReason);
    }

    [Fact]
    public async Task Cancelling_timed_inventory_does_not_stop_the_next_run()
    {
        var h = new Harness();
        FakeSession session = h.Register();

        Tagging.StartInventoryResult timed = await h.Manager.StartInventoryAsync(
            h.Profile.Id,
            new Tagging.InventorySpec { DurationSeconds = 1 });
        Assert.True(timed.Succeeded);

        await h.Manager.StopInventoryAsync(h.Profile.Id);

        Tagging.StartInventoryResult restarted = await h.Manager.StartInventoryAsync(
            h.Profile.Id,
            new Tagging.InventorySpec());
        Assert.True(restarted.Succeeded);

        // Let the cancelled timer from the first run reach its original deadline.
        // It must not tear down the new run or reuse a disposed cancellation source.
        await Task.Delay(TimeSpan.FromMilliseconds(1_250));

        Assert.True(session.IsConnected);
        Assert.True(session.InventoryRunning);

        await h.Manager.StopInventoryAsync(h.Profile.Id);
    }

    [Fact]
    public async Task StopInventory_waits_for_bounded_tag_log_consumer_before_completion()
    {
        var factory = new FakeSessionFactory();
        var profileStore = new FakeProfileStore();
        var tagLog = new BlockingInventoryTagLog();
        var probe = new FakeSession();
        var session = new FakeSession { TagToEmitOnStart = [0x30, 0x08, 0x33, 0xB2] };
        factory.Queue.Enqueue(probe);
        factory.Queue.Enqueue(session);
        await using var manager = new ReaderManager(
            factory,
            profileStore,
            tagLog: tagLog);
        var profile = new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.71" };

        await manager.AddAsync(profile, enableAfterAdding: false);
        Tagging.StartInventoryResult started = await manager.StartInventoryAsync(profile.Id, new Tagging.InventorySpec());
        Assert.True(started.Succeeded);
        await tagLog.AppendEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task stop = manager.StopInventoryAsync(profile.Id);
        await Task.Delay(50);
        Assert.False(stop.IsCompleted);

        tagLog.ReleaseAppend.TrySetResult(true);
        await stop;

        Assert.Equal(1, tagLog.AppendCount);
        Assert.True(tagLog.Completed);
    }

    [Fact]
    public async Task DisposeAsync_waits_for_active_inventory_tag_log_before_releasing_session()
    {
        var factory = new FakeSessionFactory();
        var profileStore = new FakeProfileStore();
        var tagLog = new BlockingInventoryTagLog();
        var probe = new FakeSession();
        var session = new FakeSession { TagToEmitOnStart = [0x30, 0x08, 0x33, 0xB2] };
        factory.Queue.Enqueue(probe);
        factory.Queue.Enqueue(session);
        var manager = new ReaderManager(factory, profileStore, tagLog: tagLog);
        var profile = new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.73" };

        await manager.AddAsync(profile, enableAfterAdding: false);
        Tagging.StartInventoryResult started = await manager.StartInventoryAsync(profile.Id, Spec);
        Assert.True(started.Succeeded);
        await tagLog.AppendEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task dispose = manager.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.False(dispose.IsCompleted);
        Assert.False(session.InventoryRunning);
        Assert.True(session.IsConnected);

        tagLog.ReleaseAppend.TrySetResult(true);
        await dispose;

        Assert.False(session.InventoryRunning);
        Assert.False(session.IsConnected);
        Assert.True(tagLog.Completed);
    }

    [Fact]
    public async Task DisposeAsync_is_idempotent_when_called_concurrently()
    {
        var h = new Harness();
        await using ReaderManager manager = h.Manager;
        FakeSession session = h.Register();
        Tagging.StartInventoryResult started = await manager.StartInventoryAsync(h.Profile.Id, Spec);
        Assert.True(started.Succeeded);

        Task first = manager.DisposeAsync().AsTask();
        Task second = manager.DisposeAsync().AsTask();

        await Task.WhenAll(first, second);

        Assert.False(session.InventoryRunning);
        Assert.False(session.IsConnected);
        Assert.Equal(1, session.DisconnectCount);
    }

    [Fact]
    public async Task Operations_started_after_dispose_are_rejected_before_creating_sessions()
    {
        var factory = new FakeSessionFactory();
        await using var manager = new ReaderManager(factory, new FakeProfileStore());

        await manager.DisposeAsync();

        ReaderProfile profile = new() { Id = Guid.NewGuid(), Host = "192.0.2.200" };
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.ProbeAsync(profile));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.AddAsync(profile, enableAfterAdding: false));
        Assert.Empty(factory.Created);
    }

    [Fact]
    public async Task DisposeAsync_continues_releasing_other_readers_when_one_session_dispose_fails()
    {
        var factory = new FakeSessionFactory();
        await using var manager = new ReaderManager(factory, new FakeProfileStore());
        var firstProfile = new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.74" };
        var secondProfile = new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.75" };
        var firstSession = new FakeSession { DisposeThrows = new IOException("first dispose failed") };
        var secondSession = new FakeSession();
        factory.Queue.Enqueue(new FakeSession());
        factory.Queue.Enqueue(firstSession);
        factory.Queue.Enqueue(new FakeSession());
        factory.Queue.Enqueue(secondSession);

        Assert.True((await manager.AddAsync(firstProfile, enableAfterAdding: false)).Succeeded);
        Assert.True((await manager.AddAsync(secondProfile, enableAfterAdding: false)).Succeeded);

        await manager.DisposeAsync();

        Assert.Equal(1, firstSession.DisposeCount);
        Assert.Equal(1, secondSession.DisposeCount);
        Assert.Empty(manager.Readers);
    }

    [Fact]
    public async Task SetGpo_performs_short_operation_and_disconnects()
    {
        var h = new Harness();
        FakeSession session = h.Register();

        await h.Manager.SetGpoAsync(h.Profile.Id, new Tagging.GpioCommand { PortNumber = 1, State = true });

        Assert.Equal(((ushort)1, true), session.LastGpoState);
        Assert.False(session.IsConnected); // 短操作后断开
    }

    [Fact]
    public async Task SetGpo_rejects_a_port_outside_declared_capability_without_faulting_reader()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        session.SetCapabilities(gpiCount: 0, gpoCount: 2);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => h.Manager.SetGpoAsync(
            h.Profile.Id,
            new Tagging.GpioCommand { PortNumber = 3, State = true }));

        ReaderRuntimeSnapshot snapshot = h.Manager.GetSnapshot(h.Profile.Id);
        Assert.Equal(ReaderState.Disconnected, snapshot.State);
        Assert.False(snapshot.IsStale);
        Assert.Equal(0, session.GpoSetCount);
        Assert.False(session.IsConnected);
        await h.Manager.DisposeAsync();
    }

    [Fact]
    public async Task SetGpo_reports_unsupported_when_reader_has_no_gpo_capability()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        session.SetCapabilities(gpiCount: 0, gpoCount: 0);

        PlatformOperationException exception = await Assert.ThrowsAsync<PlatformOperationException>(() =>
            h.Manager.SetGpoAsync(
                h.Profile.Id,
                new Tagging.GpioCommand { PortNumber = 1, State = true }));

        Assert.Equal(PlatformErrorCode.Unsupported, exception.ErrorCode);
        ReaderRuntimeSnapshot snapshot = h.Manager.GetSnapshot(h.Profile.Id);
        Assert.Equal(ReaderState.Disconnected, snapshot.State);
        Assert.False(snapshot.IsStale);
        Assert.Equal(0, session.GpoSetCount);
        Assert.False(session.IsConnected);
        await h.Manager.DisposeAsync();
    }

    [Fact]
    public async Task GetGpiStatus_reports_unsupported_when_reader_has_no_gpi_capability()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        session.SetCapabilities(gpiCount: 0, gpoCount: 1);

        PlatformOperationException exception = await Assert.ThrowsAsync<PlatformOperationException>(() =>
            h.Manager.GetGpiStatusAsync(h.Profile.Id));

        Assert.Equal(PlatformErrorCode.Unsupported, exception.ErrorCode);
        ReaderRuntimeSnapshot snapshot = h.Manager.GetSnapshot(h.Profile.Id);
        Assert.Equal(ReaderState.Disconnected, snapshot.State);
        Assert.False(snapshot.IsStale);
        Assert.False(session.IsConnected);
        await h.Manager.DisposeAsync();
    }

    [Fact]
    public async Task GetGpoStatus_reports_unsupported_when_reader_has_no_gpo_capability()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        session.SetCapabilities(gpiCount: 1, gpoCount: 0);

        PlatformOperationException exception = await Assert.ThrowsAsync<PlatformOperationException>(() =>
            h.Manager.GetGpoStatusAsync(h.Profile.Id));

        Assert.Equal(PlatformErrorCode.Unsupported, exception.ErrorCode);
        ReaderRuntimeSnapshot snapshot = h.Manager.GetSnapshot(h.Profile.Id);
        Assert.Equal(ReaderState.Disconnected, snapshot.State);
        Assert.False(snapshot.IsStale);
        Assert.False(session.IsConnected);
        await h.Manager.DisposeAsync();
    }

    [Fact]
    public async Task GetGpioStatus_reports_unsupported_when_reader_has_no_gpi_capability()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        session.SetCapabilities(gpiCount: 0, gpoCount: 0);

        PlatformOperationException exception = await Assert.ThrowsAsync<PlatformOperationException>(() =>
            h.Manager.GetGpioStatusAsync(h.Profile.Id));

        Assert.Equal(PlatformErrorCode.Unsupported, exception.ErrorCode);
        ReaderRuntimeSnapshot snapshot = h.Manager.GetSnapshot(h.Profile.Id);
        Assert.Equal(ReaderState.Disconnected, snapshot.State);
        Assert.False(snapshot.IsStale);
        Assert.False(session.IsConnected);
        await h.Manager.DisposeAsync();
    }

    [Fact]
    public async Task GetGpioStatus_keeps_supported_side_for_partial_gpio_capability()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        session.SetCapabilities(gpiCount: 0, gpoCount: 1);
        session.SettingsSnapshot = session.SettingsSnapshot with
        {
            Settings = new ReaderSettings
            {
                Configuration = new ReaderConfiguration
                {
                    Gpos = [new GpoConfiguration { GpoPortNumber = 1, GpoData = true }],
                },
            },
        };

        Tagging.GpioStatusSnapshot statuses = await h.Manager.GetGpioStatusAsync(h.Profile.Id);

        Assert.Empty(statuses.Gpis);
        Assert.True(Assert.Single(statuses.Gpos).State);
        ReaderRuntimeSnapshot snapshot = h.Manager.GetSnapshot(h.Profile.Id);
        Assert.Equal((ushort)0, snapshot.GpiCount);
        Assert.Equal((ushort)1, snapshot.GpoCount);
        Assert.Equal(ReaderState.Disconnected, snapshot.State);
        Assert.False(session.IsConnected);
        await h.Manager.DisposeAsync();
    }

    [Fact]
    public async Task Gpio_short_operations_return_busy_without_stealing_inventory_lease()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        Tagging.StartInventoryResult started = await h.Manager.StartInventoryAsync(h.Profile.Id, Spec);
        Assert.True(started.Succeeded);

        await Assert.ThrowsAsync<ReaderBusyException>(() =>
            h.Manager.SetGpoAsync(h.Profile.Id, new Tagging.GpioCommand { PortNumber = 1, State = true }));
        await Assert.ThrowsAsync<ReaderBusyException>(() => h.Manager.GetGpiStatusAsync(h.Profile.Id));

        Assert.True(session.IsConnected);
        Assert.True(session.InventoryRunning);
        Assert.Equal(0, session.DisconnectCount);

        await h.Manager.StopInventoryAsync(h.Profile.Id);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task GetGpiStatus_reads_standard_configuration_as_platform_contract()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        session.SettingsSnapshot = session.SettingsSnapshot with
        {
            Settings = new ReaderSettings
            {
                Configuration = new ReaderConfiguration
                {
                    Gpis = [new GpiStatus { GpiPortNumber = 1, Configured = true, State = GpiState.High }],
                },
            },
        };

        IReadOnlyList<Tagging.GpiPortStatus> statuses = await h.Manager.GetGpiStatusAsync(h.Profile.Id);

        var status = Assert.Single(statuses);
        Assert.Equal((ushort)1, status.PortNumber);
        Assert.True(status.State);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task GetGpoStatus_reads_standard_configuration_as_platform_contract()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        session.SettingsSnapshot = session.SettingsSnapshot with
        {
            Settings = new ReaderSettings
            {
                Configuration = new ReaderConfiguration
                {
                    Gpos = [new GpoConfiguration { GpoPortNumber = 1, GpoData = true }],
                },
            },
        };

        IReadOnlyList<Tagging.GpoPortStatus> statuses = await h.Manager.GetGpoStatusAsync(h.Profile.Id);

        var status = Assert.Single(statuses);
        Assert.Equal((ushort)1, status.PortNumber);
        Assert.True(status.State);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task GetGpioStatus_reads_gpi_and_gpo_from_one_short_operation()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        session.SettingsSnapshot = session.SettingsSnapshot with
        {
            Settings = new ReaderSettings
            {
                Configuration = new ReaderConfiguration
                {
                    Gpis = [new GpiStatus { GpiPortNumber = 1, Configured = true, State = GpiState.High }],
                    Gpos = [new GpoConfiguration { GpoPortNumber = 1, GpoData = true }],
                },
            },
        };

        Tagging.GpioStatusSnapshot statuses = await h.Manager.GetGpioStatusAsync(h.Profile.Id);

        Assert.True(Assert.Single(statuses.Gpis).State);
        Assert.True(Assert.Single(statuses.Gpos).State);
        Assert.Equal(1, session.ConnectCount);
        Assert.Equal(1, session.DisconnectCount);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task Gpio_status_query_enriches_unknown_gpio_counts_from_observed_ports()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        session.SettingsSnapshot = session.SettingsSnapshot with
        {
            Settings = new ReaderSettings
            {
                Configuration = new ReaderConfiguration
                {
                    Gpis =
                    [
                        new GpiStatus { GpiPortNumber = 1, Configured = true },
                        new GpiStatus { GpiPortNumber = 4, Configured = true },
                    ],
                    Gpos =
                    [
                        new GpoConfiguration { GpoPortNumber = 2 },
                        new GpoConfiguration { GpoPortNumber = 4 },
                    ],
                },
            },
        };

        Tagging.GpioStatusSnapshot statuses = await h.Manager.GetGpioStatusAsync(h.Profile.Id);

        Assert.Equal(4, statuses.Gpis.Max(static status => status.PortNumber));
        Assert.Equal(4, statuses.Gpos.Max(static status => status.PortNumber));
        ReaderRuntimeSnapshot snapshot = h.Manager.GetSnapshot(h.Profile.Id);
        Assert.Equal((ushort)4, snapshot.GpiCount);
        Assert.Equal((ushort)4, snapshot.GpoCount);
        Assert.False(snapshot.IsStale);
        Assert.Equal(ReaderState.Disconnected, snapshot.State);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task GpiChanged_event_projects_session_event_to_platform_contract()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        var observed = new TaskCompletionSource<Tagging.GpiPortStatus>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        h.Manager.GpiChanged += (_, args) =>
        {
            if (args.ReaderId == h.Profile.Id)
            {
                observed.TrySetResult(args.Status);
            }
        };

        Tagging.StartInventoryResult started = await h.Manager.StartInventoryAsync(h.Profile.Id, Spec);
        Assert.True(started.Succeeded);

        DateTimeOffset timestamp = new(2026, 8, 10, 12, 34, 56, TimeSpan.Zero);
        session.RaiseGpiChanged(portNumber: 2, state: true, timestamp);

        Tagging.GpiPortStatus status = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((ushort)2, status.PortNumber);
        Assert.True(status.Configured);
        Assert.True(status.State);
        Assert.Equal(timestamp, status.Timestamp);

        await h.Manager.StopInventoryAsync(h.Profile.Id);
        await h.Manager.DisposeAsync();
    }

    [Fact]
    public async Task Gpi_subscriber_failure_does_not_prevent_gpi_stop()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        var configured = new InventorySettings
        {
            StopTrigger = new InventoryStopTrigger
            {
                Type = InventoryStopTriggerType.GpiWithTimeout,
                GpiPortNumber = 1,
                GpiState = true,
                TimeoutMilliseconds = 1000,
            },
        };
        session.SettingsSnapshot = new ReaderSettingsSnapshot(
            new ReaderSettings { Inventory = configured },
            new ManagedRoSpecSnapshot(configured, InventoryRuntimeState.Disabled));
        h.Manager.GpiChanged += (_, _) => throw new InvalidOperationException("UI observer failed");
        var lifecycleEvents = new List<InventoryLifecycleChangedEventArgs>();
        h.Manager.LifecycleChanged += (_, args) => lifecycleEvents.Add(args);

        Tagging.StartInventoryResult started = await h.Manager.StartInventoryAsync(h.Profile.Id, Spec);
        Assert.True(started.Succeeded);

        session.RaiseGpiChanged(portNumber: 1, state: true);
        for (int i = 0; i < 50 && session.InventoryRunning; i++)
        {
            await Task.Delay(10);
        }

        Assert.False(session.InventoryRunning);
        Assert.Equal(1, session.StopInventoryCount);
        Assert.Equal(InventoryStopReason.Gpi, Assert.Single(
            lifecycleEvents,
            args => args.State == InventoryLifecycleState.Stopped).StopReason);
    }

    [Fact]
    public async Task Gpio_short_operation_releases_reader_gate_when_disconnect_state_observer_fails()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        session.DisconnectThrows = new IOException("disconnect observer test");

        EventHandler<ReaderStateChangedEventArgs> throwingObserver = (_, args) =>
        {
            if (args.Snapshot.State == ReaderState.Faulted)
            {
                throw new InvalidOperationException("state observer failed");
            }
        };
        h.Manager.StateChanged += throwingObserver;
        try
        {
            await h.Manager.GetGpioStatusAsync(h.Profile.Id);
        }
        finally
        {
            h.Manager.StateChanged -= throwingObserver;
        }

        Assert.Equal(ReaderState.Faulted, h.Manager.GetSnapshot(h.Profile.Id).State);

        // The failed event subscriber must not strand the Reader Gate. A second
        // short operation should be able to recover with a clean Session.
        Tagging.GpioStatusSnapshot recovered = await h.Manager
            .GetGpioStatusAsync(h.Profile.Id)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.NotNull(recovered);
        Assert.Equal(ReaderState.Disconnected, h.Manager.GetSnapshot(h.Profile.Id).State);
    }

    [Fact]
    public async Task TagReports_aggregate_and_raise_event()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        await h.Manager.StartInventoryAsync(h.Profile.Id, Spec);
        var tcs = new TaskCompletionSource<Tagging.TagObservation>();
        h.Manager.TagObserved += (_, args) =>
        {
            if (args.ReaderId == h.Profile.Id)
            {
                tcs.TrySetResult(args.Tag);
            }
        };

        session.EmitTag(new byte[] { 0x30, 0x01 }, seenCount: 2, antenna: 1, rssi: -40);

        Tagging.TagObservation? tag = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("3001", tag.Epc);
        Assert.Equal(2, tag.ReadCount);

        IReadOnlyList<Tagging.TagObservation> tags = h.Manager.GetTags(h.Profile.Id);
        Assert.Single(tags);
    }

    [Fact]
    public async Task TagReports_same_epc_aggregate_read_count()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        await h.Manager.StartInventoryAsync(h.Profile.Id, Spec);
        byte[] epc = [0xE2, 0x00, 0x30];

        session.EmitTag(epc, seenCount: 1, timestampMicros: ulong.MaxValue);
        session.EmitTag(epc, seenCount: 3);

        Tagging.TagObservation? aggregated = null;
        for (int i = 0; i < 20; i++)
        {
            IReadOnlyList<Tagging.TagObservation> current = h.Manager.GetTags(h.Profile.Id);
            if (current.Count == 1)
            {
                aggregated = current[0];
                if (aggregated.ReadCount == 4)
                {
                    break;
                }
            }

            await Task.Delay(50);
        }

        Assert.NotNull(aggregated);
        Assert.Equal(4, aggregated.ReadCount);
    }

    [Fact]
    public async Task TagReport_extension_projection_reaches_platform_observation()
    {
        var factory = new FakeSessionFactory();
        factory.Queue.Enqueue(new FakeSession()); // probe
        FakeSession session = new();
        factory.Queue.Enqueue(session);           // registered session
        var manager = new ReaderManager(
            factory,
            new FakeProfileStore(),
            extensions: [new ProjectionExtension()]);
        var profile = new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.4" };

        await manager.AddAsync(profile, enableAfterAdding: false);
        await manager.StartInventoryAsync(profile.Id, new Tagging.InventorySpec());

        var observed = new TaskCompletionSource<Tagging.TagObservation>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        manager.TagObserved += (_, args) => observed.TrySetResult(args.Tag);
        session.EmitTag([0x30, 0x01]);

        Tagging.TagObservation tag = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("A1B2", tag.Tid);
        Assert.Equal("42", tag.ExtensionFields["test.phase"]);

        await manager.DisposeAsync();
    }

    [Fact]
    public async Task StartInventory_after_device_close_is_allowed()
    {
        var h = new Harness();
        FakeSession session = h.Register();
        await h.Manager.StartInventoryAsync(h.Profile.Id, Spec);

        session.RaiseDeviceInitiatedClosed();
        await Task.Delay(50);

        Tagging.StartInventoryResult result = await h.Manager.StartInventoryAsync(h.Profile.Id, Spec);
        Assert.True(result.Succeeded); // InventoryRunning 已在断连时复位，允许重新盘存
    }

    [Fact]
    public async Task Device_close_completes_active_inventory_run_before_faulting_reader()
    {
        var factory = new FakeSessionFactory();
        var profileStore = new FakeProfileStore();
        var runStore = new FakeInventoryRunStore();
        factory.Queue.Enqueue(new FakeSession()); // probe
        var session = new FakeSession();
        factory.Queue.Enqueue(session);           // registered session
        await using var manager = new ReaderManager(factory, profileStore, runStore: runStore);
        var profile = new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.80" };

        await manager.AddAsync(profile, enableAfterAdding: false);
        Tagging.StartInventoryResult started = await manager.StartInventoryAsync(profile.Id, Spec);
        Assert.True(started.Succeeded);

        session.RaiseDeviceInitiatedClosed();
        for (int i = 0; i < 30 && (await runStore.GetForReaderAsync(profile.Id)).Single().EndedAtUtc is null; i++)
        {
            await Task.Delay(20);
        }

        InventoryRunRecord completed = Assert.Single(runStore.Runs);
        Assert.NotNull(completed.EndedAtUtc);
        Assert.Equal("DeviceClosed", completed.StopReason);
        Assert.Equal(ReaderState.Faulted, manager.GetSnapshot(profile.Id).State);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public async Task Two_readers_can_hold_independent_inventory_leases_in_parallel()
    {
        var factory = new FakeSessionFactory();
        var profiles = new[]
        {
            new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.81", Name = "A" },
            new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.82", Name = "B" },
        };
        var sessions = new[] { new FakeSession(), new FakeSession() };
        factory.Queue.Enqueue(new FakeSession());
        factory.Queue.Enqueue(sessions[0]);
        factory.Queue.Enqueue(new FakeSession());
        factory.Queue.Enqueue(sessions[1]);

        await using var manager = new ReaderManager(factory, new FakeProfileStore());
        await manager.AddAsync(profiles[0], enableAfterAdding: false);
        await manager.AddAsync(profiles[1], enableAfterAdding: false);

        Tagging.StartInventoryResult[] results = await Task.WhenAll(
            manager.StartInventoryAsync(profiles[0].Id, Spec),
            manager.StartInventoryAsync(profiles[1].Id, Spec));

        Assert.All(results, result => Assert.True(result.Succeeded, result.Message));
        Assert.All(sessions, session =>
        {
            Assert.True(session.IsConnected);
            Assert.True(session.InventoryRunning);
        });

        await Task.WhenAll(
            manager.StopInventoryAsync(profiles[0].Id),
            manager.StopInventoryAsync(profiles[1].Id));

        Assert.All(sessions, session => Assert.False(session.IsConnected));
    }

    [Fact]
    public async Task Two_readers_can_run_independent_gpio_short_operations_in_parallel()
    {
        var factory = new FakeSessionFactory();
        var profiles = new[]
        {
            new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.85", Name = "A" },
            new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.86", Name = "B" },
        };
        var sessions = new[] { new FakeSession(), new FakeSession() };
        foreach (FakeSession session in sessions)
        {
            session.SetCapabilities(gpiCount: 2, gpoCount: 2);
            session.SettingsSnapshot = new ReaderSettingsSnapshot(
                new ReaderSettings
                {
                    Configuration = new ReaderConfiguration
                    {
                        Gpis =
                        [
                            new GpiStatus { GpiPortNumber = 1, Configured = true, State = GpiState.Low },
                            new GpiStatus { GpiPortNumber = 2, Configured = true, State = GpiState.High },
                        ],
                        Gpos =
                        [
                            new GpoConfiguration { GpoPortNumber = 1, GpoData = false },
                            new GpoConfiguration { GpoPortNumber = 2, GpoData = true },
                        ],
                    },
                },
                new ManagedRoSpecSnapshot(new InventorySettings(), InventoryRuntimeState.Disabled));
        }

        factory.Queue.Enqueue(new FakeSession());
        factory.Queue.Enqueue(sessions[0]);
        factory.Queue.Enqueue(new FakeSession());
        factory.Queue.Enqueue(sessions[1]);

        await using var manager = new ReaderManager(factory, new FakeProfileStore());
        await manager.AddAsync(profiles[0], enableAfterAdding: false);
        await manager.AddAsync(profiles[1], enableAfterAdding: false);

        GpioStatusSnapshot[] results = await Task.WhenAll(
            manager.GetGpioStatusAsync(profiles[0].Id),
            manager.GetGpioStatusAsync(profiles[1].Id));

        Assert.Equal(2, results.Length);
        Assert.All(results, result =>
        {
            Assert.Equal(2, result.Gpis.Count);
            Assert.Equal(2, result.Gpos.Count);
        });
        Assert.All(sessions, session =>
        {
            Assert.Equal(1, session.ConnectCount);
            Assert.Equal(1, session.DisconnectCount);
            Assert.False(session.IsConnected);
        });
        Assert.All(profiles, profile =>
            Assert.Equal(ReaderState.Disconnected, manager.GetSnapshot(profile.Id).State));
    }

    [Fact]
    public async Task Gpi_stop_on_one_reader_does_not_stop_another_reader()
    {
        var factory = new FakeSessionFactory();
        var profiles = new[]
        {
            new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.83", Name = "A" },
            new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.84", Name = "B" },
        };
        var sessions = new[] { new FakeSession(), new FakeSession() };
        InventorySettings readerAInventory = new()
        {
            StopTrigger = new InventoryStopTrigger
            {
                Type = InventoryStopTriggerType.GpiWithTimeout,
                GpiPortNumber = 1,
                GpiState = true,
                TimeoutMilliseconds = 1000,
            },
        };
        sessions[0].SettingsSnapshot = new ReaderSettingsSnapshot(
            new ReaderSettings { Inventory = readerAInventory },
            new ManagedRoSpecSnapshot(readerAInventory, InventoryRuntimeState.Disabled));
        factory.Queue.Enqueue(new FakeSession());
        factory.Queue.Enqueue(sessions[0]);
        factory.Queue.Enqueue(new FakeSession());
        factory.Queue.Enqueue(sessions[1]);

        await using var manager = new ReaderManager(factory, new FakeProfileStore());
        await manager.AddAsync(profiles[0], enableAfterAdding: false);
        await manager.AddAsync(profiles[1], enableAfterAdding: false);
        await Task.WhenAll(
            manager.StartInventoryAsync(profiles[0].Id, Spec),
            manager.StartInventoryAsync(profiles[1].Id, Spec));

        var lifecycleEvents = new List<Tagging.InventoryLifecycleChangedEventArgs>();
        manager.LifecycleChanged += (_, args) => lifecycleEvents.Add(args);

        sessions[0].RaiseGpiChanged(portNumber: 1, state: true);
        for (int i = 0; i < 40 && !lifecycleEvents.Any(args =>
            args.ReaderId == profiles[0].Id
            && args.State == Tagging.InventoryLifecycleState.Stopped); i++)
        {
            await Task.Delay(10);
        }

        Assert.False(sessions[0].InventoryRunning);
        Assert.False(sessions[0].IsConnected);
        Assert.True(sessions[1].InventoryRunning);
        Assert.True(sessions[1].IsConnected);
        Tagging.InventoryLifecycleChangedEventArgs stopped = Assert.Single(
            lifecycleEvents,
            args => args.ReaderId == profiles[0].Id
                && args.State == Tagging.InventoryLifecycleState.Stopped);
        Assert.Equal(Tagging.InventoryStopReason.Gpi, stopped.StopReason);
        Assert.DoesNotContain(lifecycleEvents, args =>
            args.ReaderId == profiles[1].Id
            && args.State == Tagging.InventoryLifecycleState.Stopped);

        await manager.StopInventoryAsync(profiles[1].Id);
    }

    [Fact]
    public async Task Gpi_stop_received_during_inventory_start_is_not_lost()
    {
        var factory = new FakeSessionFactory();
        var profile = new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.85" };
        factory.Queue.Enqueue(new FakeSession()); // probe
        var session = new FakeSession
        {
            SettingsSnapshot = new ReaderSettingsSnapshot(
                new ReaderSettings
                {
                    Inventory = new InventorySettings
                    {
                        StopTrigger = new InventoryStopTrigger
                        {
                            Type = InventoryStopTriggerType.GpiWithTimeout,
                            GpiPortNumber = 1,
                            GpiState = true,
                            TimeoutMilliseconds = 1000,
                        },
                    },
                },
                new ManagedRoSpecSnapshot(
                    new InventorySettings
                    {
                        StopTrigger = new InventoryStopTrigger
                        {
                            Type = InventoryStopTriggerType.GpiWithTimeout,
                            GpiPortNumber = 1,
                            GpiState = true,
                            TimeoutMilliseconds = 1000,
                        },
                    },
                    InventoryRuntimeState.Disabled)),
        };
        session.BeforeStartInventory = () => session.RaiseGpiChanged(1, state: true);
        factory.Queue.Enqueue(session); // registered session

        await using var manager = new ReaderManager(factory, new FakeProfileStore());
        await manager.AddAsync(profile, enableAfterAdding: false);
        var lifecycleEvents = new List<InventoryLifecycleChangedEventArgs>();
        manager.LifecycleChanged += (_, args) => lifecycleEvents.Add(args);

        StartInventoryResult started = await manager.StartInventoryAsync(profile.Id, Spec);
        Assert.True(started.Succeeded);

        for (int i = 0; i < 50 && session.InventoryRunning; i++)
        {
            await Task.Delay(10);
        }

        Assert.False(session.InventoryRunning);
        Assert.False(session.IsConnected);
        Assert.Equal(1, session.StopInventoryCount);
        Assert.Single(lifecycleEvents, args => args.State == InventoryLifecycleState.Stopped);
        Assert.Equal(InventoryStopReason.Gpi, Assert.Single(
            lifecycleEvents,
            args => args.State == InventoryLifecycleState.Stopped).StopReason);
    }

    [Fact]
    public async Task Queued_gpi_stop_from_failed_session_does_not_overwrite_replacement_fault_state()
    {
        var factory = new FakeSessionFactory();
        var profile = new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.87" };
        factory.Queue.Enqueue(new FakeSession()); // probe
        var session = new FakeSession
        {
            SettingsSnapshot = new ReaderSettingsSnapshot(
                new ReaderSettings
                {
                    Inventory = new InventorySettings
                    {
                        StopTrigger = new InventoryStopTrigger
                        {
                            Type = InventoryStopTriggerType.GpiWithTimeout,
                            GpiPortNumber = 1,
                            GpiState = true,
                            TimeoutMilliseconds = 1000,
                        },
                    },
                },
                new ManagedRoSpecSnapshot(
                    new InventorySettings
                    {
                        StopTrigger = new InventoryStopTrigger
                        {
                            Type = InventoryStopTriggerType.GpiWithTimeout,
                            GpiPortNumber = 1,
                            GpiState = true,
                            TimeoutMilliseconds = 1000,
                        },
                    },
                    InventoryRuntimeState.Disabled)),
            StartInventoryThrows = new IOException("inventory start failed"),
        };
        session.BeforeStartInventory = () => session.RaiseGpiChanged(1, state: true);
        factory.Queue.Enqueue(session); // registered session
        var replacement = new FakeSession();
        factory.Queue.Enqueue(replacement); // clean session after failed start

        await using var manager = new ReaderManager(factory, new FakeProfileStore());
        await manager.AddAsync(profile, enableAfterAdding: false);

        StartInventoryResult started = await manager.StartInventoryAsync(profile.Id, Spec);
        Assert.False(started.Succeeded);
        await Task.Delay(50);

        Assert.Equal(ReaderState.Faulted, manager.GetSnapshot(profile.Id).State);
    }

    [Fact]
    public async Task Manual_and_gpi_stop_race_completes_only_one_inventory_stop()
    {
        var factory = new FakeSessionFactory();
        var profile = new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.86" };
        factory.Queue.Enqueue(new FakeSession()); // probe
        var session = new FakeSession
        {
            SettingsSnapshot = new ReaderSettingsSnapshot(
                new ReaderSettings
                {
                    Inventory = new InventorySettings
                    {
                        StopTrigger = new InventoryStopTrigger
                        {
                            Type = InventoryStopTriggerType.GpiWithTimeout,
                            GpiPortNumber = 1,
                            GpiState = true,
                            TimeoutMilliseconds = 1000,
                        },
                    },
                },
                new ManagedRoSpecSnapshot(
                    new InventorySettings
                    {
                        StopTrigger = new InventoryStopTrigger
                        {
                            Type = InventoryStopTriggerType.GpiWithTimeout,
                            GpiPortNumber = 1,
                            GpiState = true,
                            TimeoutMilliseconds = 1000,
                        },
                    },
                    InventoryRuntimeState.Disabled)),
        };
        factory.Queue.Enqueue(session); // registered session

        await using var manager = new ReaderManager(factory, new FakeProfileStore());
        await manager.AddAsync(profile, enableAfterAdding: false);
        await manager.StartInventoryAsync(profile.Id, Spec);
        var lifecycleEvents = new List<InventoryLifecycleChangedEventArgs>();
        manager.LifecycleChanged += (_, args) => lifecycleEvents.Add(args);

        session.RaiseGpiChanged(1, state: true);
        await manager.StopInventoryAsync(profile.Id);
        for (int i = 0; i < 50 && session.StopInventoryCount == 0; i++)
        {
            await Task.Delay(10);
        }

        Assert.False(session.InventoryRunning);
        Assert.False(session.IsConnected);
        Assert.Equal(1, session.StopInventoryCount);
        Assert.Equal(1, session.DisconnectCount);
        InventoryLifecycleChangedEventArgs stopped = Assert.Single(
            lifecycleEvents,
            args => args.State == InventoryLifecycleState.Stopped);
        Assert.Equal(InventoryStopReason.Manual, stopped.StopReason);
    }

    private sealed class ProjectionExtension : IReaderExtensionModule
    {
        public string Id => "test-projection";

        public bool IsApplicable(ReaderProbeInfo info) => true;

        public void ConfigureBuilder(ReaderBuilderContext context)
        {
        }

        public ReaderTagReportProjection ProjectTagReport(TagReport report) => new()
        {
            TidHex = "A1B2",
            Fields = new Dictionary<string, string>
            {
                ["test.phase"] = "42",
            },
        };
    }

    private sealed class BlockingInventoryTagLog : IInventoryTagLog
    {
        public TaskCompletionSource<bool> AppendEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> ReleaseAppend { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int AppendCount { get; private set; }
        public bool Completed { get; private set; }

        public Task<string?> StartAsync(InventoryRunRecord run, CancellationToken ct = default) =>
            Task.FromResult<string?>("test.jsonl");

        public async Task AppendAsync(InventoryRunRecord run, Tagging.TagObservation tag, CancellationToken ct = default)
        {
            AppendCount++;
            AppendEntered.TrySetResult(true);
            await ReleaseAppend.Task.WaitAsync(ct);
        }

        public Task CompleteAsync(InventoryRunRecord run, CancellationToken ct = default)
        {
            Completed = true;
            return Task.CompletedTask;
        }
    }
}
