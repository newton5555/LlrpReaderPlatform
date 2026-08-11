using LlrpReaderPlatform.App.Wpf.ViewModels;
using LlrpReaderPlatform.Contracts.Errors;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Services.Lifecycle;
using LlrpReaderPlatform.Services.Settings;
using LlrpReaderPlatform.TestKit;
using Xunit;

namespace LlrpReaderPlatform.App.Wpf.Tests;

public sealed class LegacySettingsLayoutViewModelTests
{
    [Fact]
    public async Task Settings_commands_report_when_no_reader_is_selected()
    {
        Guid readerId = Guid.NewGuid();
        var service = new StubSettingsService(readerId, BuildModel(readerId));
        var vm = new ReaderSettingsViewModel(service);

        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal("请先从左侧选择 Reader。", vm.Status);

        await vm.LoadDefaultsCommand.ExecuteAsync(null);
        Assert.Equal("请先从左侧选择 Reader。", vm.Status);

        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("请先从左侧选择 Reader。", vm.Status);
        Assert.False(vm.CanSave);
        Assert.Equal(0, service.QueryCount);
        Assert.Null(service.LastDraft);
    }

    [Fact]
    public async Task Gpi_matrix_maps_old_rows_to_platform_settings_and_save_draft()
    {
        Guid readerId = Guid.NewGuid();
        var service = new StubSettingsService(readerId, BuildModel(readerId));
        var vm = new ReaderSettingsViewModel(service);

        await vm.LoadCommand.ExecuteAsync(readerId);

        Assert.True(vm.CanSave);
        Assert.False(vm.IsSearchModeVisible);
        Assert.False(vm.IsFastIdVisible);
        Assert.False(vm.IsPhaseAngleVisible);
        Assert.False(vm.IsDopplerVisible);
        Assert.False(vm.IsFrequencySettingsVisible);
        Assert.False(vm.IsLowDutySettingsVisible);
        Assert.False(vm.IsManualSettingsVisible);
        Assert.True(vm.IsPowerSettingsVisible);
        Assert.True(vm.IsFilterSettingsVisible);
        Assert.False(vm.IsStateAwareSettingsVisible);
        Assert.False(vm.IsReportSettingsVisible);
        Assert.False(vm.IsOtherSettingsVisible);

        Assert.Equal(4, vm.GpiSettings.Count);
        Assert.True(vm.IsGpiSettingsVisible);
        Assert.Equal("20", vm.GpiSettings[0].DebounceMs);
        Assert.False(vm.GpiSettings[1].StartEnabled);

        vm.GpiSettings[1].StartEnabled = true;
        vm.GpiSettings[1].StartLevel = "High";
        vm.GpiSettings[0].DebounceMs = "250";

        Assert.True(vm.GpiSettings[1].StartEnabled);
        Assert.False(vm.GpiSettings[0].StartEnabled);
        Assert.Equal("2", Find(vm, SettingsKeys.StartGpiPort).ValueText);
        Assert.True(Find(vm, SettingsKeys.StartGpiLevel).BooleanValue);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(service.LastDraft);
        Assert.Equal(2, service.LastDraft!.Values[SettingsKeys.StartGpiPort]);
        Assert.Equal(true, service.LastDraft.Values[SettingsKeys.StartGpiLevel]);
        Assert.Equal(250, service.LastDraft.Values[ImpinjDebounceKey(1)]);
        Assert.Equal(2, service.QueryCount);
        Assert.Contains("回读", vm.Status);
    }

    [Fact]
    public async Task Save_converts_numeric_choice_display_back_to_its_semantic_value()
    {
        Guid readerId = Guid.NewGuid();
        var service = new StubSettingsService(readerId, BuildModelWithNumericTxPowerOption(readerId));
        var vm = new ReaderSettingsViewModel(service);

        await vm.LoadCommand.ExecuteAsync(readerId);

        SettingsEntryRowViewModel row = Find(vm, SettingsKeys.AntennaTxPowerDbm(1));
        row.ValueText = "Index 33: 33 dBm";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(service.LastDraft);
        Assert.Equal(33m, service.LastDraft!.Values[SettingsKeys.AntennaTxPowerDbm(1)]);
        Assert.DoesNotContain("值无效", vm.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_become_read_only_when_the_selected_reader_snapshot_is_stale()
    {
        Guid readerId = Guid.NewGuid();
        var sessionFactory = new FakeSessionFactory();
        await using var manager = new ReaderManager(sessionFactory, new FakeProfileStore());
        ReaderProfile profile = new()
        {
            Id = readerId,
            Name = "Stale settings",
            Host = "192.0.2.91",
        };
        sessionFactory.Queue.Enqueue(new FakeSession()); // Probe
        sessionFactory.Queue.Enqueue(new FakeSession()); // registered session
        await manager.AddAsync(profile, enableAfterAdding: false);
        Assert.True((await manager.ActivateAsync(readerId)).Succeeded);

        var service = new StubSettingsService(
            readerId,
            BuildModel(readerId, manager.GetSnapshot(readerId).CapabilityRevision));
        var vm = new ReaderSettingsViewModel(service, readerManager: manager);
        vm.SetReaderContext(new ReaderItemViewModel(manager.GetSnapshot(readerId)));
        await vm.LoadCommand.ExecuteAsync(readerId);

        Assert.True(vm.IsReaderAvailable);
        Assert.True(vm.CanSave);

        ReaderRuntimeSnapshot stale = manager.GetSnapshot(readerId) with
        {
            State = ReaderState.Faulted,
            IsStale = true,
            Error = "socket reset",
        };
        vm.SetReaderContext(new ReaderItemViewModel(stale));

        Assert.False(vm.IsReaderAvailable);
        Assert.False(vm.CanSave);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("重新激活", vm.Status);
        Assert.Null(service.LastDraft);
    }

    [Fact]
    public async Task Capability_refresh_reenables_save_after_reader_context_was_stale()
    {
        Guid readerId = Guid.NewGuid();
        var sessionFactory = new FakeSessionFactory();
        await using var manager = new ReaderManager(sessionFactory, new FakeProfileStore());
        ReaderProfile profile = new()
        {
            Id = readerId,
            Name = "Reconnect settings",
            Host = "192.0.2.92",
        };
        sessionFactory.Queue.Enqueue(new FakeSession());
        sessionFactory.Queue.Enqueue(new FakeSession());
        await manager.AddAsync(profile, enableAfterAdding: false);
        Assert.True((await manager.ActivateAsync(readerId)).Succeeded);

        long capabilityRevision = manager.GetSnapshot(readerId).CapabilityRevision;
        var service = new StubSettingsService(readerId, BuildModel(readerId, capabilityRevision));
        var vm = new ReaderSettingsViewModel(service, readerManager: manager);
        vm.SetReaderContext(new ReaderItemViewModel(manager.GetSnapshot(readerId) with
        {
            State = ReaderState.Faulted,
            IsStale = true,
        }));

        await vm.LoadCommand.ExecuteAsync(readerId);

        Assert.True(vm.IsReaderAvailable);
        Assert.True(vm.CanSave);
    }

    [Fact]
    public async Task Settings_result_is_discarded_when_same_reader_capability_changes()
    {
        Guid readerId = Guid.NewGuid();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new StubSettingsService(readerId, BuildModel(readerId))
        {
            BeforeQueryAsync = async _ =>
            {
                started.TrySetResult(true);
                await release.Task;
            },
        };
        var vm = new ReaderSettingsViewModel(service);
        ReaderProfile profile = new()
        {
            Id = readerId,
            Name = "Changing settings",
            Host = "192.0.2.93",
        };
        vm.SetReaderContext(new ReaderItemViewModel(new ReaderRuntimeSnapshot
        {
            ReaderId = readerId,
            Profile = profile,
            State = ReaderState.Connected,
            CapabilityRevision = 1,
            IsStale = false,
        }));

        Task load = vm.LoadCommand.ExecuteAsync(readerId);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        vm.SetReaderContext(new ReaderItemViewModel(new ReaderRuntimeSnapshot
        {
            ReaderId = readerId,
            Profile = profile,
            State = ReaderState.Connected,
            CapabilityRevision = 2,
            IsStale = false,
        }));
        release.TrySetResult(true);
        await load.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task Cancel_requests_navigation_without_querying_or_applying()
    {
        Guid readerId = Guid.NewGuid();
        var service = new StubSettingsService(readerId, BuildModel(readerId));
        var vm = new ReaderSettingsViewModel(service);
        bool cancelRequested = false;
        vm.CancelRequested += (_, _) => cancelRequested = true;

        await vm.LoadCommand.ExecuteAsync(readerId);
        vm.GpiSettings[1].StartEnabled = true;
        Assert.Equal("2", Find(vm, SettingsKeys.StartGpiPort).ValueText);

        vm.CancelCommand.Execute(null);

        Assert.True(cancelRequested);
        Assert.Equal(1, service.QueryCount);
        Assert.Null(service.LastDraft);
        Assert.True(vm.GpiSettings[1].StartEnabled);
    }

    [Fact]
    public async Task Settings_apply_error_code_is_projected_to_status()
    {
        Guid readerId = Guid.NewGuid();
        var service = new StubSettingsService(readerId, BuildModel(readerId))
        {
            ApplyResult = new SettingsApplyResult(false, "inventory is running")
            {
                ErrorCode = LlrpReaderPlatform.Contracts.Errors.PlatformErrorCode.ReaderBusy,
            },
        };
        var vm = new ReaderSettingsViewModel(service);

        await vm.LoadCommand.ExecuteAsync(readerId);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains("Reader 忙碌", vm.Status);
        Assert.Contains("inventory is running", vm.Status);
    }

    [Fact]
    public async Task Settings_query_preserves_reader_busy_error_code()
    {
        Guid readerId = Guid.NewGuid();
        var service = new StubSettingsService(readerId, BuildModel(readerId))
        {
            QueryException = new PlatformOperationException(
                PlatformErrorCode.ReaderBusy,
                "inventory is running"),
        };
        var vm = new ReaderSettingsViewModel(service);

        await vm.LoadCommand.ExecuteAsync(readerId);

        Assert.Contains("Reader 忙碌", vm.Status);
        Assert.Contains("inventory is running", vm.Status);
    }

    [Fact]
    public async Task Old_tab_one_adapters_project_filters_and_antenna_actions()
    {
        Guid readerId = Guid.NewGuid();
        var service = new StubSettingsService(readerId, BuildFullModel(readerId));
        var vm = new ReaderSettingsViewModel(service);

        await vm.LoadCommand.ExecuteAsync(readerId);

        Assert.True(vm.IsSettingsLayoutAvailable);
        Assert.True(vm.IsManualSettingsVisible);
        Assert.True(vm.IsPowerSettingsVisible);
        Assert.True(vm.IsFilterSettingsVisible);
        Assert.True(vm.IsStateAwareSettingsVisible);
        Assert.NotNull(vm.Filter1);
        Assert.NotNull(vm.Filter2);
        Assert.Equal("3008", vm.Filter1!.Mask!.ValueText);
        Assert.Equal(1, vm.Filter1.MemoryBank!.SelectedChoiceIndex);
        Assert.Equal(2, vm.AntennaSettings.Count);
        Assert.Equal("2", vm.ReportEveryRow!.ValueText);
        Assert.Equal("250", vm.TariRow!.ValueText);
        Assert.False(vm.AntennaSettings[0].HasChannel);
        Assert.Null(vm.AntennaSettings[0].Channel);
        Assert.True(vm.IsRfModeEditable);
        Assert.True(vm.IsSearchModeVisible);
        Assert.True(vm.IsFastIdVisible);
        Assert.True(vm.IsPhaseAngleVisible);
        Assert.True(vm.IsDopplerVisible);
        Assert.False(vm.IsFrequencySettingsVisible);
        Assert.False(vm.IsLowDutySettingsVisible);
        Assert.True(vm.IsPopulationEditable);
        Assert.True(vm.IsReportEveryEditable);
        Assert.True(vm.IsSessionEditable);
        Assert.True(vm.IsTariEditable);
        Assert.True(vm.IsTxPowerEditable);
        Assert.True(vm.IsRxSensitivityEditable);

        vm.FillAllAntennasCommand.Execute(null);
        Assert.Equal("1, 2", vm.AntennasRow!.ValueText);
        vm.ClearAntennasCommand.Execute(null);
        Assert.Empty(vm.AntennasRow.ValueText);

        vm.IsIndividualAntennaSettingsExpanded = true;
        Assert.False(vm.IsTxPowerEditable);
        Assert.False(vm.IsRxSensitivityEditable);

        vm.StateAwareFiltersRow!.BooleanValue = true;
        Assert.True(vm.ShowStateAwareFilterOptions);
        Assert.False(vm.ShowNonStateAwareFilterOptions);
    }

    [Fact]
    public async Task Explicit_zero_gpi_capability_hides_the_tab_one_gpi_matrix()
    {
        Guid readerId = Guid.NewGuid();
        var service = new StubSettingsService(readerId, BuildModel(readerId));
        var vm = new ReaderSettingsViewModel(service);
        vm.SetReaderContext(new ReaderItemViewModel(new ReaderRuntimeSnapshot
        {
            ReaderId = readerId,
            Profile = new ReaderProfile
            {
                Id = readerId,
                Name = "No GPI Reader",
                Host = "192.0.2.97",
            },
            State = ReaderState.Connected,
            IsEnabled = true,
            IsStale = false,
            GpiCount = 0,
        }));

        await vm.LoadCommand.ExecuteAsync(readerId);

        Assert.Empty(vm.GpiSettings);
        Assert.False(vm.IsGpiSettingsVisible);
    }

    [Fact]
    public async Task Save_keeps_busy_through_device_reread_and_rejects_cancel()
    {
        Guid readerId = Guid.NewGuid();
        var service = new StubSettingsService(readerId, BuildModel(readerId));
        var vm = new ReaderSettingsViewModel(service);
        await vm.LoadCommand.ExecuteAsync(readerId);

        var rereadEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReread = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.BeforeQueryAsync = async queryNumber =>
        {
            if (queryNumber == 2)
            {
                rereadEntered.TrySetResult(true);
                await releaseReread.Task;
            }
        };

        bool cancelRequested = false;
        vm.CancelRequested += (_, _) => cancelRequested = true;
        Task save = vm.SaveCommand.ExecuteAsync(null);

        await rereadEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(vm.IsBusy);
        vm.CancelCommand.Execute(null);
        Assert.False(cancelRequested);

        releaseReread.TrySetResult(true);
        await save;
        Assert.False(vm.IsBusy);
        Assert.Contains("回读", vm.Status);
    }

    [Fact]
    public async Task Save_does_not_report_reader_reread_when_query_returns_readonly_cache()
    {
        Guid readerId = Guid.NewGuid();
        SettingsEditorModel liveModel = BuildModel(readerId);
        SettingsEditorModel cachedModel = new(
            new EffectiveSettingsLayout
            {
                ReaderId = readerId,
                CapabilityRevision = liveModel.Layout.CapabilityRevision,
                Entries = liveModel.Layout.Entries
                    .Select(static entry => entry with { ReadOnlyReason = "设备当前不可达；以下为本地缓存，只读显示。" })
                    .ToArray(),
            },
            liveModel.Snapshot);
        var service = new SequentialSettingsService(readerId, liveModel, cachedModel);
        var vm = new ReaderSettingsViewModel(service);

        await vm.LoadCommand.ExecuteAsync(readerId);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(2, service.QueryCount);
        Assert.Equal("Cached / read-only", vm.SettingsOrigin);
        Assert.Equal("保存成功，但设备回读失败。", vm.Status);
        Assert.DoesNotContain("已回读 Reader 当前设置", vm.Status);
    }

    private static SettingsEntryRowViewModel Find(ReaderSettingsViewModel vm, string key) =>
        Assert.Single(vm.Rows, row => row.Key == key);

    private static string ImpinjDebounceKey(ushort port) => $"impinj.gpi-debounce-ms.{port}";

    private static SettingsEditorModel BuildModel(Guid readerId, long capabilityRevision = 3)
    {
        return BuildModelCore(readerId, capabilityRevision, includeTxPowerOption: false);
    }

    private static SettingsEditorModel BuildModelWithNumericTxPowerOption(Guid readerId)
    {
        return BuildModelCore(readerId, capabilityRevision: 3, includeTxPowerOption: true);
    }

    private static SettingsEditorModel BuildModelCore(
        Guid readerId,
        long capabilityRevision,
        bool includeTxPowerOption)
    {
        SettingsEntry[] entries =
        [
            Boolean(SettingsKeys.StartGpiEnabled, false),
            Integer(SettingsKeys.StartGpiPort, 1),
            Boolean(SettingsKeys.StartGpiLevel, false),
            Boolean(SettingsKeys.StopGpiEnabled, false),
            Integer(SettingsKeys.StopGpiPort, 1),
            Boolean(SettingsKeys.StopGpiLevel, false),
            Integer(SettingsKeys.StopGpiTimeoutMs, 1000),
            Integer(ImpinjDebounceKey(1), 20),
            Boolean(SettingsKeys.FilterEnabled(1), false),
            Boolean(SettingsKeys.FilterEnabled(2), false),
            Decimal(
                SettingsKeys.AntennaTxPowerDbm(1),
                33m,
                includeTxPowerOption ? new SettingsOption(33m, "Index 33: 33 dBm") : null),
            Integer(SettingsKeys.AntennaRxSensitivityDb(1), 0),
        ];

        return new SettingsEditorModel(
            new EffectiveSettingsLayout
            {
                ReaderId = readerId,
                CapabilityRevision = capabilityRevision,
                Entries = entries,
            },
            new SettingsSnapshot
            {
                ReaderId = readerId,
                CapabilityRevision = capabilityRevision,
                Values = entries.ToDictionary(entry => entry.Key, entry => entry.CurrentValue),
            });
    }

    private static SettingsEditorModel BuildFullModel(Guid readerId)
    {
        SettingsEntry[] entries =
        [
            Text(SettingsKeys.AntennaIds, "1"),
            Boolean(SettingsKeys.IndividualAntennaSettings, false),
            Decimal(SettingsKeys.TxPowerDbm, 20m),
            Integer(SettingsKeys.RxSensitivityDb, 0),
            Choice(SettingsKeys.RfMode, 0, new SettingsOption(0, "0: FM0")),
            Choice(SettingsKeys.Session, 1, new SettingsOption(0, "S0"), new SettingsOption(1, "S1")),
            Integer(SettingsKeys.TagPopulation, 32),
            Integer(SettingsKeys.ReportEvery, 2),
            Integer(SettingsKeys.Tari, 250),
            Boolean("impinj.fast-id", false),
            Choice("impinj.search-mode", -1, new SettingsOption(-1, "Reader selected")),
            Boolean("impinj.phase-angle", false),
            Boolean("impinj.doppler", false),
            Decimal(SettingsKeys.AntennaTxPowerDbm(1), 20m),
            Integer(SettingsKeys.AntennaRxSensitivityDb(1), 0),
            Integer(SettingsKeys.AntennaChannelIndex(1), 1),
            Decimal(SettingsKeys.AntennaTxPowerDbm(2), 20m),
            Integer(SettingsKeys.AntennaRxSensitivityDb(2), 0),
            Integer(SettingsKeys.AntennaChannelIndex(2), 2),
            Boolean(SettingsKeys.StateAwareFiltersEnabled, false),
            ..FilterEntries(1),
            ..FilterEntries(2),
        ];

        return new SettingsEditorModel(
            new EffectiveSettingsLayout { ReaderId = readerId, CapabilityRevision = 4, Entries = entries },
            new SettingsSnapshot
            {
                ReaderId = readerId,
                CapabilityRevision = 4,
                Values = entries.ToDictionary(entry => entry.Key, entry => entry.CurrentValue),
            });
    }

    private static IEnumerable<SettingsEntry> FilterEntries(int index)
    {
        yield return Boolean(SettingsKeys.FilterEnabled(index), index == 1);
        yield return Text(SettingsKeys.FilterMask(index), index == 1 ? "3008" : string.Empty);
        yield return Integer(SettingsKeys.FilterBitLength(index), 16);
        yield return Integer(SettingsKeys.FilterOffset(index), 32);
        yield return Choice(SettingsKeys.FilterMemoryBank(index), 1, new SettingsOption(0, "Reserved"), new SettingsOption(1, "EPC"));
        yield return Choice(SettingsKeys.FilterStateTarget(index), 1, new SettingsOption(0, "Selected flag"), new SettingsOption(1, "Session 0"));
        yield return Choice(SettingsKeys.FilterStateAction(index), 0, new SettingsOption(0, "Assert A / Deassert B"));
        yield return Choice(SettingsKeys.FilterMatchAction(index), 1, new SettingsOption(0, "Do nothing"), new SettingsOption(1, "Select"));
        yield return Choice(SettingsKeys.FilterNonMatchAction(index), 2, new SettingsOption(0, "Do nothing"), new SettingsOption(1, "Select"), new SettingsOption(2, "Unselect"));
    }

    private static SettingsEntry Boolean(string key, bool value) => new()
    {
        Key = key,
        Title = key,
        EditorKind = EditorKind.Boolean,
        ValueType = typeof(bool),
        CurrentValue = value,
    };

    private static SettingsEntry Integer(string key, int value) => new()
    {
        Key = key,
        Title = key,
        EditorKind = EditorKind.Integer,
        ValueType = typeof(int),
        CurrentValue = value,
    };

    private static SettingsEntry Text(string key, string value) => new()
    {
        Key = key,
        Title = key,
        EditorKind = EditorKind.Text,
        ValueType = typeof(string),
        CurrentValue = value,
    };

    private static SettingsEntry Choice(string key, int value, params SettingsOption[] options) => new()
    {
        Key = key,
        Title = key,
        EditorKind = EditorKind.Choice,
        ValueType = typeof(int),
        Options = options,
        CurrentValue = value,
    };

    private static SettingsEntry Decimal(string key, decimal value, SettingsOption? option = null) => new()
    {
        Key = key,
        Title = key,
        EditorKind = EditorKind.Decimal,
        ValueType = typeof(decimal),
        CurrentValue = value,
        Options = option is null ? [] : [option],
    };

    private sealed class StubSettingsService(Guid readerId, SettingsEditorModel model) : IReaderSettingsService
    {
        public int QueryCount { get; private set; }
        public SettingsDraft? LastDraft { get; private set; }
        public Func<int, Task>? BeforeQueryAsync { get; set; }
        public Exception? QueryException { get; set; }
        public SettingsApplyResult ApplyResult { get; set; } = new(true);

        public async Task<SettingsEditorModel> QueryAsync(Guid id, CancellationToken ct = default)
        {
            Assert.Equal(readerId, id);
            QueryCount++;
            if (BeforeQueryAsync is not null)
            {
                await BeforeQueryAsync(QueryCount);
            }

            if (QueryException is not null)
            {
                throw QueryException;
            }

            return model;
        }

        public Task<SettingsEditorModel> GetDefaultsAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(model);

        public SettingsValidationResult Validate(SettingsDraft draft) => new(true);

        public Task<SettingsApplyResult> ApplyAsync(Guid id, SettingsDraft draft, CancellationToken ct = default)
        {
            LastDraft = draft;
            return Task.FromResult(ApplyResult);
        }
    }

    private sealed class SequentialSettingsService(
        Guid readerId,
        SettingsEditorModel liveModel,
        SettingsEditorModel cachedModel) : IReaderSettingsService
    {
        public int QueryCount { get; private set; }

        public Task<SettingsEditorModel> QueryAsync(Guid id, CancellationToken ct = default)
        {
            Assert.Equal(readerId, id);
            QueryCount++;
            return Task.FromResult(QueryCount == 1 ? liveModel : cachedModel);
        }

        public Task<SettingsEditorModel> GetDefaultsAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(liveModel);

        public SettingsValidationResult Validate(SettingsDraft draft) => new(true);

        public Task<SettingsApplyResult> ApplyAsync(
            Guid id,
            SettingsDraft draft,
            CancellationToken ct = default) =>
            Task.FromResult(new SettingsApplyResult(true));
    }
}
