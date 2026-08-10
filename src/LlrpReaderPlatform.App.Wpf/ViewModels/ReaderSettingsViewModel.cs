using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Services.Settings;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>
/// 能力驱动设置页 ViewModel：绑定 SettingsEditorModel 生成语义化设置行，
/// 只在 Save 时提交 SettingsDraft（不直接接触 SettingsCompiler 或 SDK）。
/// </summary>
public partial class ReaderSettingsViewModel : ObservableObject
{
    private readonly IReaderSettingsService settings;
    private ReaderFeatureCatalog? readerFeatureCatalog;
    private ushort? readerGpoCount;

    [ObservableProperty]
    private Guid? readerId;

    [ObservableProperty]
    private string readerHost = "-";

    [ObservableProperty]
    private string readerModel = "-";

    [ObservableProperty]
    private string readerProtocol = "-";

    [ObservableProperty]
    private string readerRegion = "Reader default";

    [ObservableProperty]
    private string? status;

    [ObservableProperty]
    private long capabilityRevision;

    [ObservableProperty]
    private string preset = "Default";

    [ObservableProperty]
    private string settingsOrigin = "No reader settings loaded";

    [ObservableProperty]
    private bool isBusy;

    public ReaderSettingsViewModel(
        IReaderSettingsService settings,
        DiagnosticsViewModel? diagnostics = null)
    {
        this.settings = settings;
        Diagnostics = diagnostics;
    }

    /// <summary>设置页自己的设备信息投影，避免 View 反向依赖 MainViewModel。</summary>
    public void SetReaderContext(ReaderItemViewModel? reader)
    {
        ReaderId = reader?.ReaderId;
        ReaderHost = reader?.Host ?? "-";
        ReaderModel = string.IsNullOrWhiteSpace(reader?.Model) ? "-" : reader.Model!;
        ReaderProtocol = reader?.Snapshot.NegotiatedProtocolVersion switch
        {
            LlrpProtocolVersion.Version101 => "LLRP 1.0.1",
            LlrpProtocolVersion.Version11 => "LLRP 1.1",
            _ => reader is null ? "-" : "未协商",
        };
        ReaderRegion = "Reader default";
        readerFeatureCatalog = reader?.Snapshot.FeatureCatalog;
        readerGpoCount = reader?.Snapshot.GpoCount;
        Diagnostics?.SelectReader(reader?.ReaderId, readerFeatureCatalog, readerGpoCount);
    }

    public ObservableCollection<SettingsEntryRowViewModel> Rows { get; } = [];
    public ObservableCollection<SettingsEntryRowViewModel> ManualRows { get; } = [];
    public ObservableCollection<SettingsEntryRowViewModel> PowerRows { get; } = [];
    public ObservableCollection<SettingsEntryRowViewModel> GpiRows { get; } = [];
    public ObservableCollection<SettingsEntryRowViewModel> FilterRows { get; } = [];
    public ObservableCollection<SettingsEntryRowViewModel> StateAwareRows { get; } = [];
    public ObservableCollection<SettingsEntryRowViewModel> FrequencyRows { get; } = [];
    public ObservableCollection<SettingsEntryRowViewModel> LowDutyRows { get; } = [];
    public ObservableCollection<SettingsEntryRowViewModel> ReportRows { get; } = [];
    public ObservableCollection<SettingsEntryRowViewModel> OtherRows { get; } = [];
    public ObservableCollection<SettingsEntryRowViewModel> AntennaRows { get; } = [];
    public ObservableCollection<SettingsEntryRowViewModel> Filter1Rows { get; } = [];
    public ObservableCollection<SettingsEntryRowViewModel> Filter2Rows { get; } = [];
    public ObservableCollection<LegacyAntennaSettingsRowViewModel> AntennaSettings { get; } = [];
    public ObservableCollection<LegacyGpiSettingsRowViewModel> GpiSettings { get; } = [];
    public DiagnosticsViewModel? Diagnostics { get; }
    public SettingsEntryRowViewModel? StopTimeoutRow => Rows.FirstOrDefault(row => row.Key == SettingsKeys.StopGpiTimeoutMs);
    public event EventHandler? CancelRequested;

    // 旧 Reader Studio 设置页的字段适配器。它们只暴露平台 SettingsEntry，
    // 让 WPF 保持旧布局，同时不把旧项目的 ReaderSettings 类型带入新架构。
    public SettingsEntryRowViewModel? AntennasRow => FindRow(SettingsKeys.AntennaIds);
    public SettingsEntryRowViewModel? RfModeRow => FindRow(SettingsKeys.RfMode);
    public SettingsEntryRowViewModel? SearchModeRow => FindRow("impinj.search-mode");
    public SettingsEntryRowViewModel? FastIdRow => FindRow("impinj.fast-id");
    public SettingsEntryRowViewModel? PopulationRow => FindRow(SettingsKeys.TagPopulation);
    public SettingsEntryRowViewModel? ReportEveryRow => FindRow(SettingsKeys.ReportEvery);
    public SettingsEntryRowViewModel? SessionRow => FindRow(SettingsKeys.Session);
    public SettingsEntryRowViewModel? TariRow => FindRow(SettingsKeys.Tari);
    public SettingsEntryRowViewModel? PhaseAngleRow => FindRow("impinj.phase-angle");
    public SettingsEntryRowViewModel? DopplerRow => FindRow("impinj.doppler");
    public SettingsEntryRowViewModel? TxPowerRow => FindRow(SettingsKeys.TxPowerDbm);
    public SettingsEntryRowViewModel? RxSensitivityRow => FindRow(SettingsKeys.RxSensitivityDb);
    public SettingsEntryRowViewModel? StateAwareFiltersRow => FindRow(SettingsKeys.StateAwareFiltersEnabled);
    public SettingsEntryRowViewModel? StateAwareTargetRow => FindRow(SettingsKeys.StateAwareTarget);
    public SettingsEntryRowViewModel? StateAwareSelectedFlagRow => FindRow(SettingsKeys.StateAwareSelectedFlag);
    public SettingsEntryRowViewModel? FrequencyModeRow => FindRow("impinj.fixed-frequency-mode");
    public SettingsEntryRowViewModel? FrequencyChannelsRow => FindRow("impinj.fixed-frequency-channels");
    public SettingsEntryRowViewModel? LowDutyEnabledRow => FindRow("impinj.low-duty-cycle");
    public SettingsEntryRowViewModel? EmptyFieldTimeoutRow => FindRow("impinj.empty-field-timeout-ms");
    public SettingsEntryRowViewModel? FieldPingIntervalRow => FindRow("impinj.field-ping-interval-ms");
    public LegacyFilterSettingsRowViewModel? Filter1 { get; private set; }
    public LegacyFilterSettingsRowViewModel? Filter2 { get; private set; }

    public bool IsImpinjExtensionsAvailable => SearchModeRow is not null;
    public bool IsRfModeEditable => RfModeRow is { IsReadOnly: false };
    public bool IsSearchModeEditable => SearchModeRow is { IsReadOnly: false };
    public bool IsFastIdEditable => FastIdRow is { IsReadOnly: false };
    public bool IsPopulationEditable => PopulationRow is { IsReadOnly: false };
    public bool IsReportEveryEditable => ReportEveryRow is { IsReadOnly: false };
    public bool IsSessionEditable => SessionRow is { IsReadOnly: false };
    public bool IsTariEditable => TariRow is { IsReadOnly: false };
    public bool IsPhaseAngleEditable => PhaseAngleRow is { IsReadOnly: false };
    public bool IsDopplerEditable => DopplerRow is { IsReadOnly: false };
    public bool IsTxPowerEditable => TxPowerRow is { IsReadOnly: false } && IsGlobalAntennaSettingsEnabled;
    public bool IsRxSensitivityEditable => RxSensitivityRow is { IsReadOnly: false } && IsGlobalAntennaSettingsEnabled;
    public bool IsFrequencyModeEditable => FrequencyModeRow is { IsReadOnly: false };
    public bool IsLowDutyEditable => LowDutyEnabledRow is { IsReadOnly: false };
    public bool IsEmptyFieldTimeoutEditable => EmptyFieldTimeoutRow is { IsReadOnly: false };
    public bool IsFieldPingIntervalEditable => FieldPingIntervalRow is { IsReadOnly: false };
    public bool IsStateAwareFiltersSupported => StateAwareFiltersRow is not null && !StateAwareFiltersRow.IsReadOnly;
    public bool IsStateAwareFiltersEnabled => StateAwareFiltersRow?.BooleanValue == true;
    public bool ShowStateAwareFilterOptions => IsStateAwareFiltersSupported && IsStateAwareFiltersEnabled;
    public bool ShowNonStateAwareFilterOptions => !ShowStateAwareFilterOptions;
    public bool IsFrequencyChannelsVisible => FrequencyModeRow?.ValueText == "2";
    public bool IsFrequencyChannelsEditable => FrequencyChannelsRow is { IsReadOnly: false };
    public bool IsGlobalAntennaSettingsEnabled =>
        FindRow(SettingsKeys.IndividualAntennaSettings)?.BooleanValue != true;

    public bool IsIndividualAntennaSettingsExpanded
    {
        get => FindRow(SettingsKeys.IndividualAntennaSettings)?.BooleanValue == true;
        set
        {
            SettingsEntryRowViewModel? row = FindRow(SettingsKeys.IndividualAntennaSettings);
            if (row is null || row.IsReadOnly || row.BooleanValue == value)
            {
                return;
            }

            row.BooleanValue = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsGlobalAntennaSettingsEnabled));
            OnPropertyChanged(nameof(IsTxPowerEditable));
            OnPropertyChanged(nameof(IsRxSensitivityEditable));
        }
    }

    [RelayCommand]
    private async Task LoadAsync(Guid? id)
    {
        if (id is not { } readerId)
        {
            Status = "请先从左侧选择 Reader。";
            return;
        }

        await LoadCoreAsync(readerId);
    }

    private async Task<bool> LoadCoreAsync(Guid id, bool manageBusy = true)
    {
        if (manageBusy && IsBusy)
        {
            return false;
        }

        if (manageBusy)
        {
            IsBusy = true;
        }

        ReaderId = id;
        Diagnostics?.SelectReader(id, readerFeatureCatalog, readerGpoCount);
        try
        {
            SettingsEditorModel model = await settings.QueryAsync(id, CancellationToken.None);
            CapabilityRevision = model.Snapshot.CapabilityRevision;
            ReplaceRows(model.Layout.Entries);

            Status = model.Layout.HasEditableSettings
                ? "设置已加载（可编辑）"
                : "需要连接 Reader 以获取能力后才能配置。";
            SettingsOrigin = model.Layout.HasEditableSettings ? "Loaded from Reader" : "Cached / read-only";
            return true;
        }
        catch (Exception ex)
        {
            ClearRows();
            CapabilityRevision = 0;
            Status = $"读取 Reader 设置失败: {ex.Message}";
            return false;
        }
        finally
        {
            if (manageBusy)
            {
                IsBusy = false;
            }
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy || ReaderId is not { } id)
        {
            if (ReaderId is null)
            {
                Status = "请先从左侧选择 Reader。";
            }

            return;
        }

        var draft = new SettingsDraft
        {
            ReaderId = id,
            CapabilityRevision = CapabilityRevision,
        };
        foreach (SettingsEntryRowViewModel row in Rows)
        {
            if (row.IsReadOnly)
            {
                continue;
            }

            try
            {
                draft.Values[row.Key] = ConvertValue(row.ValueText, row.Entry);
            }
            catch
            {
                Status = $"设置项 {row.Title} 的值无效。";
                return;
            }
        }

        IsBusy = true;
        try
        {
            SettingsApplyResult result = await settings.ApplyAsync(id, draft, CancellationToken.None);
            if (!result.Succeeded)
            {
                Status = $"保存失败: {result.Error}";
                return;
            }

            string applyStatus = "保存成功，但设备回读失败。";
            if (await LoadCoreAsync(id, manageBusy: false))
            {
                applyStatus = "保存成功，已回读 Reader 当前设置。";
                SettingsOrigin = "Saved to Reader + local preset";
            }

            Status = applyStatus;
        }
        catch (Exception ex)
        {
            Status = $"保存失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsBusy)
        {
            return;
        }

        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task LoadDefaultsAsync()
    {
        if (IsBusy || ReaderId is not { } readerId)
        {
            if (ReaderId is null)
            {
                Status = "请先从左侧选择 Reader。";
            }

            return;
        }

        IsBusy = true;
        try
        {
            SettingsEditorModel model = await settings.GetDefaultsAsync(readerId, CancellationToken.None);
            CapabilityRevision = model.Snapshot.CapabilityRevision;
            ReaderId = readerId;
            Diagnostics?.SelectReader(readerId, readerFeatureCatalog, readerGpoCount);
            ReplaceRows(model.Layout.Entries);

            Status = "已加载 SDK 默认设置（尚未下发到 Reader）。";
            SettingsOrigin = "SDK defaults (not applied)";
        }
        catch (Exception ex)
        {
            Status = $"读取默认设置失败: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void FillAllAntennas()
    {
        SettingsEntryRowViewModel? row = AntennasRow;
        if (row is null || row.IsReadOnly)
        {
            return;
        }

        row.ValueText = string.Join(", ", AntennaSettings.Select(static antenna => antenna.AntennaId));
    }

    [RelayCommand]
    private void ClearAntennas()
    {
        SettingsEntryRowViewModel? row = AntennasRow;
        if (row is not null && !row.IsReadOnly)
        {
            row.ValueText = string.Empty;
        }
    }

    private void ReplaceRows(IReadOnlyList<SettingsEntry> entries)
    {
        ClearRows();
        foreach (SettingsEntry entry in entries)
        {
            var row = new SettingsEntryRowViewModel(entry);
            Rows.Add(row);
            GetGroup(entry.Key).Add(row);
        }

        RebuildLegacyLayoutRows();
        NotifyLegacyLayoutProperties();
        OnPropertyChanged(nameof(StopTimeoutRow));
    }

    private void ClearRows()
    {
        Rows.Clear();
        ManualRows.Clear();
        PowerRows.Clear();
        GpiRows.Clear();
        FilterRows.Clear();
        StateAwareRows.Clear();
        FrequencyRows.Clear();
        LowDutyRows.Clear();
        ReportRows.Clear();
        OtherRows.Clear();
        AntennaRows.Clear();
        Filter1Rows.Clear();
        Filter2Rows.Clear();
        AntennaSettings.Clear();
        GpiSettings.Clear();
        Filter1 = null;
        Filter2 = null;
    }

    private void RebuildLegacyLayoutRows()
    {
        foreach (SettingsEntryRowViewModel row in Rows)
        {
            if (row.Key.StartsWith("filter-1-", StringComparison.Ordinal))
            {
                Filter1Rows.Add(row);
            }
            else if (row.Key.StartsWith("filter-2-", StringComparison.Ordinal))
            {
                Filter2Rows.Add(row);
            }
        }

        foreach (ushort antennaId in Rows
            .Select(static row => row.Key)
            .Select(TryGetAntennaId)
            .Where(static id => id is not null)
            .Select(static id => id!.Value)
            .Distinct()
            .OrderBy(static id => id))
        {
            SettingsEntryRowViewModel? tx = FindRow(SettingsKeys.AntennaTxPowerDbm(antennaId));
            SettingsEntryRowViewModel? rx = FindRow(SettingsKeys.AntennaRxSensitivityDb(antennaId));
            SettingsEntryRowViewModel? channel = FindRow(SettingsKeys.AntennaChannelIndex(antennaId));
            AntennaSettings.Add(new LegacyAntennaSettingsRowViewModel(
                antennaId,
                tx,
                rx,
                channel));
        }

        for (ushort port = 1; port <= 4; port++)
        {
            GpiSettings.Add(new LegacyGpiSettingsRowViewModel(
                port,
                FindRow(SettingsKeys.StartGpiEnabled),
                FindRow(SettingsKeys.StartGpiPort),
                FindRow(SettingsKeys.StartGpiLevel),
                FindRow(SettingsKeys.StopGpiEnabled),
                FindRow(SettingsKeys.StopGpiPort),
                FindRow(SettingsKeys.StopGpiLevel),
                FindRow(SettingsKeys.StopGpiTimeoutMs),
                FindRow(ImpinjGpiDebounceKey(port))));
        }

        Filter1 = BuildFilterRow(1);
        Filter2 = BuildFilterRow(2);
    }

    private LegacyFilterSettingsRowViewModel BuildFilterRow(int index) => new(
        index,
        FindRow(SettingsKeys.FilterEnabled(index)),
        FindRow(SettingsKeys.FilterMask(index)),
        FindRow(SettingsKeys.FilterBitLength(index)),
        FindRow(SettingsKeys.FilterOffset(index)),
        FindRow(SettingsKeys.FilterMemoryBank(index)),
        FindRow(SettingsKeys.FilterStateTarget(index)),
        FindRow(SettingsKeys.FilterStateAction(index)),
        FindRow(SettingsKeys.FilterMatchAction(index)),
        FindRow(SettingsKeys.FilterNonMatchAction(index)));

    private void NotifyLegacyLayoutProperties()
    {
        string[] properties =
        [
            nameof(AntennasRow), nameof(RfModeRow), nameof(SearchModeRow), nameof(FastIdRow),
            nameof(PopulationRow), nameof(ReportEveryRow), nameof(SessionRow), nameof(TariRow),
            nameof(PhaseAngleRow), nameof(DopplerRow),
            nameof(TxPowerRow), nameof(RxSensitivityRow), nameof(StateAwareFiltersRow),
            nameof(StateAwareTargetRow), nameof(StateAwareSelectedFlagRow), nameof(FrequencyModeRow),
            nameof(FrequencyChannelsRow), nameof(LowDutyEnabledRow), nameof(EmptyFieldTimeoutRow),
            nameof(FieldPingIntervalRow), nameof(Filter1), nameof(Filter2),
            nameof(IsImpinjExtensionsAvailable), nameof(IsRfModeEditable), nameof(IsSearchModeEditable),
            nameof(IsFastIdEditable), nameof(IsPopulationEditable), nameof(IsReportEveryEditable),
            nameof(IsSessionEditable), nameof(IsTariEditable),
            nameof(IsPhaseAngleEditable), nameof(IsDopplerEditable), nameof(IsFrequencyModeEditable),
            nameof(IsTxPowerEditable), nameof(IsRxSensitivityEditable),
            nameof(IsLowDutyEditable), nameof(IsEmptyFieldTimeoutEditable), nameof(IsFieldPingIntervalEditable),
            nameof(IsStateAwareFiltersSupported),
            nameof(IsStateAwareFiltersEnabled), nameof(ShowStateAwareFilterOptions),
            nameof(ShowNonStateAwareFilterOptions), nameof(IsFrequencyChannelsVisible), nameof(IsFrequencyChannelsEditable),
            nameof(IsGlobalAntennaSettingsEnabled), nameof(IsIndividualAntennaSettingsExpanded),
        ];
        foreach (string property in properties)
        {
            OnPropertyChanged(property);
        }

        foreach (SettingsEntryRowViewModel row in Rows)
        {
            row.PropertyChanged += OnLegacyRowPropertyChanged;
        }
    }

    private void OnLegacyRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SettingsEntryRowViewModel.BooleanValue)
            or nameof(SettingsEntryRowViewModel.ValueText)
            or nameof(SettingsEntryRowViewModel.SelectedChoiceIndex))
        {
            OnPropertyChanged(nameof(IsGlobalAntennaSettingsEnabled));
            OnPropertyChanged(nameof(IsTxPowerEditable));
            OnPropertyChanged(nameof(IsRxSensitivityEditable));
            OnPropertyChanged(nameof(IsIndividualAntennaSettingsExpanded));
            OnPropertyChanged(nameof(IsStateAwareFiltersEnabled));
            OnPropertyChanged(nameof(ShowStateAwareFilterOptions));
            OnPropertyChanged(nameof(ShowNonStateAwareFilterOptions));
            OnPropertyChanged(nameof(IsFrequencyChannelsVisible));
            OnPropertyChanged(nameof(IsFrequencyChannelsEditable));
        }
    }

    private SettingsEntryRowViewModel? FindRow(string key) => Rows.FirstOrDefault(row => row.Key == key);

    private static ushort? TryGetAntennaId(string key)
    {
        string[] parts = key.Split('-');
        return parts.Length >= 3
            && parts[0] == "antenna"
            && ushort.TryParse(parts[1], out ushort antennaId)
            ? antennaId
            : null;
    }

    private static string ImpinjGpiDebounceKey(ushort port) => $"impinj.gpi-debounce-ms.{port}";

    private ObservableCollection<SettingsEntryRowViewModel> GetGroup(string key)
    {
        if (key.StartsWith("filter-", StringComparison.Ordinal))
        {
            return FilterRows;
        }

        if (key.StartsWith("state-aware-", StringComparison.Ordinal))
        {
            return StateAwareRows;
        }

        if (key.StartsWith("start-gpi-", StringComparison.Ordinal)
            || key.StartsWith("stop-gpi-", StringComparison.Ordinal)
            || key.StartsWith("impinj.gpi-debounce-", StringComparison.Ordinal))
        {
            return GpiRows;
        }

        if (key is "impinj.fixed-frequency-mode" or "impinj.fixed-frequency-channels")
        {
            return FrequencyRows;
        }

        if (key is "impinj.low-duty-cycle" or "impinj.empty-field-timeout-ms" or "impinj.field-ping-interval-ms")
        {
            return LowDutyRows;
        }

        if (key.StartsWith("report-", StringComparison.Ordinal))
        {
            return ReportRows;
        }

        if (key is "tx-power-dbm" or "rx-sensitivity-db" or "antenna-ids" or "individual-antenna-settings")
        {
            return PowerRows;
        }

        if (key.StartsWith("antenna-", StringComparison.Ordinal))
        {
            return AntennaRows;
        }

        if (key is "antenna" or "session" or "tag-population" or "report-every" or "rf-mode" or "tari"
            or "impinj.search-mode" or "impinj.fast-id" or "impinj.phase-angle" or "impinj.doppler")
        {
            return ManualRows;
        }

        return OtherRows;
    }

    private static object ConvertValue(string text, SettingsEntry entry)
    {
        if (entry.ValueType == typeof(bool))
        {
            return bool.Parse(text);
        }

        if (entry.ValueType == typeof(ushort))
        {
            return ushort.Parse(text, CultureInfo.InvariantCulture);
        }

        if (entry.ValueType == typeof(int))
        {
            return int.Parse(text, CultureInfo.InvariantCulture);
        }

        if (entry.ValueType == typeof(decimal))
        {
            return decimal.Parse(text, CultureInfo.InvariantCulture);
        }

        return text;
    }
}

/// <summary>旧 WPF 的每天线 RF 编辑行，内部仍直接复用能力驱动设置行。</summary>
public sealed class LegacyAntennaSettingsRowViewModel
{
    public LegacyAntennaSettingsRowViewModel(
        ushort antennaId,
        SettingsEntryRowViewModel? txPower,
        SettingsEntryRowViewModel? rxSensitivity,
        SettingsEntryRowViewModel? channel)
    {
        AntennaId = antennaId;
        Name = $"Antenna {antennaId}";
        TxPower = txPower;
        RxSensitivity = rxSensitivity;
        Channel = channel;
    }

    public ushort AntennaId { get; }
    public string Name { get; }
    public SettingsEntryRowViewModel? TxPower { get; }
    public SettingsEntryRowViewModel? RxSensitivity { get; }
    public SettingsEntryRowViewModel? Channel { get; }
    public bool HasChannel => Channel is not null;
}

/// <summary>旧 WPF 双列 Gen2 Filter 编辑矩阵的适配器。</summary>
public sealed class LegacyFilterSettingsRowViewModel
{
    public LegacyFilterSettingsRowViewModel(
        int index,
        SettingsEntryRowViewModel? enabled,
        SettingsEntryRowViewModel? mask,
        SettingsEntryRowViewModel? bitLength,
        SettingsEntryRowViewModel? offset,
        SettingsEntryRowViewModel? memoryBank,
        SettingsEntryRowViewModel? stateTarget,
        SettingsEntryRowViewModel? stateAction,
        SettingsEntryRowViewModel? matchAction,
        SettingsEntryRowViewModel? nonMatchAction)
    {
        Index = index;
        Enabled = enabled;
        Mask = mask;
        BitLength = bitLength;
        Offset = offset;
        MemoryBank = memoryBank;
        StateTarget = stateTarget;
        StateAction = stateAction;
        MatchAction = matchAction;
        NonMatchAction = nonMatchAction;
    }

    public int Index { get; }
    public SettingsEntryRowViewModel? Enabled { get; }
    public SettingsEntryRowViewModel? Mask { get; }
    public SettingsEntryRowViewModel? BitLength { get; }
    public SettingsEntryRowViewModel? Offset { get; }
    public SettingsEntryRowViewModel? MemoryBank { get; }
    public SettingsEntryRowViewModel? StateTarget { get; }
    public SettingsEntryRowViewModel? StateAction { get; }
    public SettingsEntryRowViewModel? MatchAction { get; }
    public SettingsEntryRowViewModel? NonMatchAction { get; }
}

/// <summary>
/// 旧 WPF 的四行 GPI 矩阵适配器。平台设置语义是一个 Start/Stop GPI 触发器，
/// 这里把它投影为旧 UI 的“每个端口最多选一行”，保存时仍回写同一组平台设置行。
/// </summary>
public sealed partial class LegacyGpiSettingsRowViewModel : ObservableObject
{
    private readonly SettingsEntryRowViewModel? startEnabled;
    private readonly SettingsEntryRowViewModel? startPort;
    private readonly SettingsEntryRowViewModel? startLevel;
    private readonly SettingsEntryRowViewModel? stopEnabled;
    private readonly SettingsEntryRowViewModel? stopPort;
    private readonly SettingsEntryRowViewModel? stopLevel;
    private readonly SettingsEntryRowViewModel? stopTimeout;
    private readonly SettingsEntryRowViewModel? debounce;

    public LegacyGpiSettingsRowViewModel(
        ushort port,
        SettingsEntryRowViewModel? startEnabled,
        SettingsEntryRowViewModel? startPort,
        SettingsEntryRowViewModel? startLevel,
        SettingsEntryRowViewModel? stopEnabled,
        SettingsEntryRowViewModel? stopPort,
        SettingsEntryRowViewModel? stopLevel,
        SettingsEntryRowViewModel? stopTimeout,
        SettingsEntryRowViewModel? debounce)
    {
        Port = port;
        this.startEnabled = startEnabled;
        this.startPort = startPort;
        this.startLevel = startLevel;
        this.stopEnabled = stopEnabled;
        this.stopPort = stopPort;
        this.stopLevel = stopLevel;
        this.stopTimeout = stopTimeout;
        this.debounce = debounce;

        foreach (SettingsEntryRowViewModel row in Rows())
        {
            row.PropertyChanged += OnSourcePropertyChanged;
        }
    }

    public ushort Port { get; }
    public bool IsStartEditable => IsEditable(startEnabled) && IsEditable(startPort);
    public bool IsStartLevelEditable => IsEditable(startLevel);
    public bool IsStopEditable => IsEditable(stopEnabled) && IsEditable(stopPort);
    public bool IsStopLevelEditable => IsEditable(stopLevel);
    public bool IsDebounceEnabled => debounce is not null && !debounce.IsReadOnly;

    public bool StartEnabled
    {
        get => IsPortSelected(startEnabled, startPort);
        set
        {
            if (value)
            {
                SetBoolean(startEnabled, true);
                SetText(startPort, Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (StartEnabled)
            {
                SetBoolean(startEnabled, false);
            }

            NotifyTriggerProperties();
        }
    }

    public string StartLevel
    {
        get => ReadBoolean(startLevel) ? "High" : "Low";
        set
        {
            SetBoolean(startLevel, string.Equals(value, "High", StringComparison.OrdinalIgnoreCase));
            OnPropertyChanged();
        }
    }

    public bool StopEnabled
    {
        get => IsPortSelected(stopEnabled, stopPort);
        set
        {
            if (value)
            {
                SetBoolean(stopEnabled, true);
                SetText(stopPort, Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (StopEnabled)
            {
                SetBoolean(stopEnabled, false);
            }

            NotifyTriggerProperties();
        }
    }

    public string StopLevel
    {
        get => ReadBoolean(stopLevel) ? "High" : "Low";
        set
        {
            SetBoolean(stopLevel, string.Equals(value, "High", StringComparison.OrdinalIgnoreCase));
            OnPropertyChanged();
        }
    }

    public string DebounceMs
    {
        get => debounce?.ValueText ?? string.Empty;
        set
        {
            SetText(debounce, value);
            OnPropertyChanged();
        }
    }

    private IEnumerable<SettingsEntryRowViewModel> Rows() =>
        new[] { startEnabled, startPort, startLevel, stopEnabled, stopPort, stopLevel, stopTimeout, debounce }
            .OfType<SettingsEntryRowViewModel>()
            .Distinct();

    private void OnSourcePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SettingsEntryRowViewModel.BooleanValue)
            or nameof(SettingsEntryRowViewModel.ValueText))
        {
            NotifyTriggerProperties();
            OnPropertyChanged(nameof(DebounceMs));
        }
    }

    private void NotifyTriggerProperties()
    {
        OnPropertyChanged(nameof(StartEnabled));
        OnPropertyChanged(nameof(StartLevel));
        OnPropertyChanged(nameof(StopEnabled));
        OnPropertyChanged(nameof(StopLevel));
    }

    private bool IsPortSelected(SettingsEntryRowViewModel? enabled, SettingsEntryRowViewModel? selectedPort) =>
        ReadBoolean(enabled) && int.TryParse(selectedPort?.ValueText, out int value) && value == Port;

    private static bool ReadBoolean(SettingsEntryRowViewModel? row) => row?.BooleanValue == true;

    private static bool IsEditable(SettingsEntryRowViewModel? row) => row is { IsReadOnly: false };

    private static void SetBoolean(SettingsEntryRowViewModel? row, bool value)
    {
        if (row is not null && !row.IsReadOnly)
        {
            row.BooleanValue = value;
        }
    }

    private static void SetText(SettingsEntryRowViewModel? row, string value)
    {
        if (row is not null && !row.IsReadOnly)
        {
            row.ValueText = value;
        }
    }
}
