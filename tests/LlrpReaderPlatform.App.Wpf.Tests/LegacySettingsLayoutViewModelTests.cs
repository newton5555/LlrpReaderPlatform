using LlrpReaderPlatform.App.Wpf.ViewModels;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Services.Settings;
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

        Assert.Equal(4, vm.GpiSettings.Count);
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
    public async Task Old_tab_one_adapters_project_filters_and_antenna_actions()
    {
        Guid readerId = Guid.NewGuid();
        var service = new StubSettingsService(readerId, BuildFullModel(readerId));
        var vm = new ReaderSettingsViewModel(service);

        await vm.LoadCommand.ExecuteAsync(readerId);

        Assert.NotNull(vm.Filter1);
        Assert.NotNull(vm.Filter2);
        Assert.Equal("3008", vm.Filter1!.Mask!.ValueText);
        Assert.Equal(1, vm.Filter1.MemoryBank!.SelectedChoiceIndex);
        Assert.Equal(2, vm.AntennaSettings.Count);
        Assert.Equal("2", vm.ReportEveryRow!.ValueText);
        Assert.Equal("250", vm.TariRow!.ValueText);
        Assert.True(vm.AntennaSettings[0].HasChannel);
        Assert.Equal("1", vm.AntennaSettings[0].Channel!.ValueText);
        Assert.True(vm.IsRfModeEditable);
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

    private static SettingsEntryRowViewModel Find(ReaderSettingsViewModel vm, string key) =>
        Assert.Single(vm.Rows, row => row.Key == key);

    private static string ImpinjDebounceKey(ushort port) => $"impinj.gpi-debounce-ms.{port}";

    private static SettingsEditorModel BuildModel(Guid readerId)
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
            Decimal(SettingsKeys.AntennaTxPowerDbm(1), 20m),
            Integer(SettingsKeys.AntennaRxSensitivityDb(1), 0),
        ];

        return new SettingsEditorModel(
            new EffectiveSettingsLayout
            {
                ReaderId = readerId,
                CapabilityRevision = 3,
                Entries = entries,
            },
            new SettingsSnapshot
            {
                ReaderId = readerId,
                CapabilityRevision = 3,
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

    private static SettingsEntry Decimal(string key, decimal value) => new()
    {
        Key = key,
        Title = key,
        EditorKind = EditorKind.Decimal,
        ValueType = typeof(decimal),
        CurrentValue = value,
    };

    private sealed class StubSettingsService(Guid readerId, SettingsEditorModel model) : IReaderSettingsService
    {
        public int QueryCount { get; private set; }
        public SettingsDraft? LastDraft { get; private set; }
        public Func<int, Task>? BeforeQueryAsync { get; set; }

        public async Task<SettingsEditorModel> QueryAsync(Guid id, CancellationToken ct = default)
        {
            Assert.Equal(readerId, id);
            QueryCount++;
            if (BeforeQueryAsync is not null)
            {
                await BeforeQueryAsync(QueryCount);
            }

            return model;
        }

        public Task<SettingsEditorModel> GetDefaultsAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(model);

        public SettingsValidationResult Validate(SettingsDraft draft) => new(true);

        public Task<SettingsApplyResult> ApplyAsync(Guid id, SettingsDraft draft, CancellationToken ct = default)
        {
            LastDraft = draft;
            return Task.FromResult(new SettingsApplyResult(true));
        }
    }
}
