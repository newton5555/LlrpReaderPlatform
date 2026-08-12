using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderPlatform.Contracts.Errors;
using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>
/// 能力驱动设置页 ViewModel：绑定 SettingsEditorModel 生成语义化设置行，
/// 只在 Save 时提交 SettingsDraft（不直接接触 SettingsCompiler 或 SDK）。
/// </summary>
public partial class ReaderSettingsViewModel : ObservableObject, IPageOperationOwner, IDisposable
{
    private readonly IReaderSettingsService settings;
    private readonly IReaderManager? readerManager;
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly CancellationToken lifetimeToken;
    private ReaderFeatureCatalog? readerFeatureCatalog;
    private ushort? readerGpiCount;
    private ushort? readerGpoCount;
    private bool capabilitiesCurrent;
    private CancellationTokenSource? activeLoadCts;
    private CancellationTokenSource? activeSaveCts;
    private long readerContextVersion;
    private ReaderCapabilityContextStamp? readerContextStamp;
    private bool disposed;

    [ObservableProperty]
    private Guid? readerId;

    [ObservableProperty]
    private string readerHost = "-";

    [ObservableProperty]
    private string readerModel = "-";

    [ObservableProperty]
    private string readerProtocol = "-";

    [ObservableProperty]
    private string readerProtocolPolicy = "-";

    [ObservableProperty]
    private string readerConnection = "-";

    [ObservableProperty]
    private string readerExtensions = "-";

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
        DiagnosticsViewModel? diagnostics = null,
        IReaderManager? readerManager = null)
    {
        this.settings = settings;
        Diagnostics = diagnostics;
        this.readerManager = readerManager;
        lifetimeToken = lifetimeCts.Token;
    }

    /// <summary>设置页自己的设备信息投影，避免 View 反向依赖 MainViewModel。</summary>
    public void SetReaderContext(ReaderItemViewModel? reader)
    {
        Guid? nextReaderId = reader?.ReaderId;
        ReaderCapabilityContextStamp nextContext = ReaderCapabilityContextStamp.From(reader);
        bool readerChanged = ReaderId != nextReaderId;
        bool contextChanged = readerContextStamp is not { } currentContext
            || currentContext != nextContext;

        if (readerChanged)
        {
            Interlocked.Increment(ref readerContextVersion);
            CancelActiveOperation(activeLoadCts);
            ClearRows();
            CapabilityRevision = 0;
            SettingsOrigin = nextReaderId is null
                ? "No reader settings loaded"
                : "Waiting for Reader settings";
            Status = nextReaderId is null
                ? "请先从左侧选择 Reader。"
                : "正在切换 Reader 设置...";
        }
        else if (contextChanged)
        {
            // 同一 Reader 重新激活、能力版本变化或进入故障态时，
            // 不能让旧 Query/Default 结果覆盖当前能力上下文。保留现有行，
            // 使离线只读回显仍可见，直到下一次显式刷新。
            Interlocked.Increment(ref readerContextVersion);
            CancelActiveOperation(activeLoadCts);
        }

        readerContextStamp = nextContext;
        ReaderId = nextReaderId;
        ReaderHost = reader?.Host ?? "-";
        ReaderModel = string.IsNullOrWhiteSpace(reader?.Model) ? "-" : reader.Model!;
        ReaderProtocol = reader?.Snapshot.NegotiatedProtocolVersion switch
        {
            LlrpProtocolVersion.Version101 => "LLRP 1.0.1",
            LlrpProtocolVersion.Version11 => "LLRP 1.1",
            _ => reader is null ? "-" : "未协商",
        };
        ReaderProtocolPolicy = reader?.Snapshot.Profile.LlrpVersion switch
        {
            LlrpProtocolVersionOption.Auto => "Auto (1.1 → 1.0.1)",
            LlrpProtocolVersionOption.Force101 => "Force LLRP 1.0.1",
            LlrpProtocolVersionOption.Force11 => "Force LLRP 1.1",
            _ => reader is null ? "-" : "未知策略",
        };
        ReaderConnection = reader?.ConnectionSummary ?? "-";
        ReaderExtensions = reader is null
            ? "-"
            : reader.Snapshot.ActiveExtensionIds.Count == 0
                ? "Standard LLRP"
                : string.Join(", ", reader.Snapshot.ActiveExtensionIds);
        ReaderRegion = "Reader default";
        readerFeatureCatalog = reader?.Snapshot.FeatureCatalog;
        readerGpiCount = reader?.Snapshot.GpiCount;
        readerGpoCount = reader?.Snapshot.GpoCount;
        capabilitiesCurrent = nextContext.CapabilitiesCurrent;
        OnPropertyChanged(nameof(IsReaderAvailable));
        OnPropertyChanged(nameof(CanSave));
        Diagnostics?.SelectReader(
            reader?.ReaderId,
            readerFeatureCatalog,
            readerGpiCount,
            readerGpoCount,
            capabilitiesCurrent);
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

    /// <summary>
    /// 是否已经有真实或缓存的设置布局可供旧 Tab1/Tab2 投影。
    /// 能力未就绪时只保留服务层的占位行，不能让固定布局绑定到一组空控件。
    /// </summary>
    public bool IsSettingsLayoutAvailable => Rows.Any(row => row.Key != "capability-pending");

    public bool IsGpiSettingsVisible => GpiSettings.Count > 0;
    public bool IsManualSettingsVisible => ManualRows.Count > 0;
    public bool IsPowerSettingsVisible => PowerRows.Count > 0 || AntennaRows.Count > 0;
    public bool IsFilterSettingsVisible => FilterRows.Count > 0;
    public bool IsStateAwareSettingsVisible => StateAwareRows.Count > 0;
    public bool IsReportSettingsVisible => ReportRows.Count > 0;
    public bool IsOtherSettingsVisible => OtherRows.Count > 0;

    public bool CanSave => !IsBusy
        && ReaderId is not null
        && IsReaderAvailable
        && Rows.Any(static row => !row.IsReadOnly);

    /// <summary>
    /// 设置页的语义编辑门禁。Reader 连接故障、正在断开或能力快照过期时，
    /// 仍可查看当前行或本地缓存，但不能继续把草稿当作设备当前配置下发。
    /// </summary>
    public bool IsReaderAvailable => ReaderId is not null
        && (readerManager is null || capabilitiesCurrent);

    partial void OnReaderIdChanged(Guid? value)
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(IsReaderAvailable));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanSave));

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
    public SettingsEntryRowViewModel? TxPowerRow => FindRow(SettingsKeys.TxPowerIndex);
    public SettingsEntryRowViewModel? RxSensitivityRow => FindRow(SettingsKeys.RxSensitivityIndex);
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
    public bool IsSearchModeVisible => SearchModeRow is not null;
    public bool IsFastIdVisible => FastIdRow is not null;
    public bool IsPhaseAngleVisible => PhaseAngleRow is not null;
    public bool IsDopplerVisible => DopplerRow is not null;
    public bool IsFrequencySettingsVisible => FrequencyModeRow is not null;
    public bool IsLowDutySettingsVisible => LowDutyEnabledRow is not null;
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
        if (disposed)
        {
            return;
        }

        if (id is not { } readerId)
        {
            Status = "请先从左侧选择 Reader。";
            return;
        }

        await LoadCoreAsync(readerId);
    }

    /// <summary>
    /// 供主窗口在切换到设置页时读取结果；命令接口本身只暴露 Task，
    /// 这里保留服务层返回的“是否来自可编辑 Reader 设置”语义，避免主窗口
    /// 在只读缓存或能力占位页上误报设备同步成功。
    /// </summary>
    public Task<bool> LoadForNavigationAsync(Guid id, CancellationToken ct = default) =>
        LoadCoreAsync(id, externalToken: ct);

    private async Task<bool> LoadCoreAsync(
        Guid id,
        bool manageBusy = true,
        CancellationToken externalToken = default)
    {
        // A second explicit refresh replaces the previous Query. The active
        // load CTS is the ownership marker; SaveAsync uses manageBusy:false
        // and keeps the outer busy state while it performs its re-read.
        if (manageBusy && IsBusy && activeLoadCts is null)
        {
            return false;
        }

        if (manageBusy)
        {
            IsBusy = true;
        }

        using CancellationTokenSource loadCts = externalToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, externalToken)
            : CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        CancellationTokenSource? previousLoadCts = Interlocked.Exchange(ref activeLoadCts, loadCts);
        previousLoadCts?.Cancel();
        long contextVersion = Volatile.Read(ref readerContextVersion);
        ReaderId = id;
        Diagnostics?.SelectReader(
            id,
            readerFeatureCatalog,
            readerGpiCount,
            readerGpoCount,
            capabilitiesCurrent);
        try
        {
            SettingsEditorModel model = await settings.QueryAsync(id, loadCts.Token);
            if (disposed
                || loadCts.IsCancellationRequested
                || !ReferenceEquals(activeLoadCts, loadCts)
                || !IsCurrentReaderContext(id, contextVersion))
            {
                return false;
            }

            ReaderRuntimeSnapshot? latestSnapshot = RefreshReaderCapabilityContext(id);
            if (!IsSettingsModelCurrent(model, latestSnapshot))
            {
                return false;
            }

            if (!IsCurrentReaderContext(id, contextVersion))
            {
                return false;
            }

            CapabilityRevision = model.Snapshot.CapabilityRevision;
            ReplaceRows(model.Layout.Entries);

            Status = model.Layout.HasEditableSettings
                ? "设置已加载（可编辑）"
                : "当前设置为只读；需要连接 Reader 或该 Reader 未提供可编辑能力。";
            SettingsOrigin = model.Layout.HasEditableSettings ? "Loaded from Reader" : "Cached / read-only";
            // QueryAsync may deliberately return a read-only semantic SQLite
            // preset when the Reader cannot be reached. Keep that distinction
            // for SaveAsync: a device Apply followed by a cached fallback is
            // not a successful Reader re-read.
            return model.Layout.HasEditableSettings;
        }
        catch (OperationCanceledException) when (loadCts.IsCancellationRequested)
        {
            // Reader 切换或页面销毁时，旧查询不能再覆盖当前页面。
            return false;
        }
        catch (Exception ex)
        {
            if (!disposed)
            {
                ClearRows();
                CapabilityRevision = 0;
                Status = PlatformErrorDisplay.Failure("读取 Reader 设置", ex);
            }

            return false;
        }
        finally
        {
            if (manageBusy)
            {
                if (ReferenceEquals(activeLoadCts, loadCts))
                {
                    IsBusy = false;
                }
            }

            Interlocked.CompareExchange(ref activeLoadCts, null, loadCts);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (disposed)
        {
            return;
        }

        if (IsBusy || ReaderId is not { } id)
        {
            if (ReaderId is null)
            {
                Status = "请先从左侧选择 Reader。";
            }

            return;
        }

        if (!IsReaderAvailable)
        {
            Status = "Reader 当前未连接或能力已过期，请先从左侧重新激活。";
            return;
        }

        var draft = new SettingsDraft
        {
            ReaderId = id,
            CapabilityRevision = CapabilityRevision,
        };
        long contextVersion = Volatile.Read(ref readerContextVersion);
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
        CancellationTokenSource saveCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        CancellationTokenSource? previousSaveCts = Interlocked.Exchange(ref activeSaveCts, saveCts);
        CancelAndDispose(previousSaveCts);
        try
        {
            SettingsApplyResult result = await settings.ApplyAsync(id, draft, saveCts.Token);
            if (saveCts.IsCancellationRequested || !IsCurrentReaderContext(id, contextVersion))
            {
                return;
            }

            if (!result.Succeeded)
            {
                Status = PlatformErrorDisplay.Failure("保存", result.ErrorCode, result.Error);
                return;
            }

            string applyStatus = "保存成功，但设备回读失败。";
            if (IsCurrentReaderContext(id, contextVersion)
                && await LoadCoreAsync(id, manageBusy: false))
            {
                if (disposed
                    || saveCts.IsCancellationRequested
                    || !IsCurrentReaderContext(id, contextVersion))
                {
                    return;
                }

                applyStatus = "保存成功，已回读 Reader 当前设置。";
                SettingsOrigin = "Saved to Reader + local preset";
            }

            if (disposed
                || saveCts.IsCancellationRequested
                || !IsCurrentReaderContext(id, contextVersion))
            {
                return;
            }

            Status = applyStatus;
        }
        catch (OperationCanceledException) when (saveCts.IsCancellationRequested)
        {
            // 页面离开或窗口退出时取消设置下发，不再向旧页面写状态。
        }
        catch (Exception ex)
        {
            if (!disposed)
            {
                Status = PlatformErrorDisplay.Failure("保存", ex);
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref activeSaveCts, null, saveCts);
            saveCts.Dispose();
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
        if (disposed)
        {
            return;
        }

        if (IsBusy || ReaderId is not { } readerId)
        {
            if (ReaderId is null)
            {
                Status = "请先从左侧选择 Reader。";
            }

            return;
        }

        IsBusy = true;
        long contextVersion = Volatile.Read(ref readerContextVersion);
        using CancellationTokenSource defaultsCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        CancellationTokenSource? previousLoadCts = Interlocked.Exchange(ref activeLoadCts, defaultsCts);
        previousLoadCts?.Cancel();
        try
        {
            SettingsEditorModel model = await settings.GetDefaultsAsync(readerId, defaultsCts.Token);
            if (disposed
                || defaultsCts.IsCancellationRequested
                || !ReferenceEquals(activeLoadCts, defaultsCts)
                || !IsCurrentReaderContext(readerId, contextVersion))
            {
                return;
            }

            ReaderRuntimeSnapshot? latestSnapshot = RefreshReaderCapabilityContext(readerId);
            if (!IsSettingsModelCurrent(model, latestSnapshot)
                || !IsCurrentReaderContext(readerId, contextVersion))
            {
                return;
            }

            CapabilityRevision = model.Snapshot.CapabilityRevision;
            ReaderId = readerId;
            Diagnostics?.SelectReader(
                readerId,
                readerFeatureCatalog,
                readerGpiCount,
                readerGpoCount,
                capabilitiesCurrent);
            ReplaceRows(model.Layout.Entries);

            Status = "已加载 SDK 默认设置（尚未下发到 Reader）。";
            SettingsOrigin = "SDK defaults (not applied)";
        }
        catch (OperationCanceledException) when (defaultsCts.IsCancellationRequested)
        {
            // 窗口退出时静默结束未完成的默认设置读取。
        }
        catch (Exception ex)
        {
            if (!disposed)
            {
                Status = PlatformErrorDisplay.Failure("读取默认设置", ex);
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref activeLoadCts, null, defaultsCts);
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
        OnPropertyChanged(nameof(IsSettingsLayoutAvailable));
        OnPropertyChanged(nameof(StopTimeoutRow));
        OnPropertyChanged(nameof(CanSave));
    }

    private ReaderRuntimeSnapshot? RefreshReaderCapabilityContext(Guid id)
    {
        if (readerManager is null)
        {
            return null;
        }

        ReaderRuntimeSnapshot snapshot;
        try
        {
            snapshot = readerManager.GetSnapshot(id);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }

        readerContextStamp = ReaderCapabilityContextStamp.From(snapshot);
        readerFeatureCatalog = snapshot.FeatureCatalog;
        readerGpiCount = snapshot.GpiCount;
        readerGpoCount = snapshot.GpoCount;
        capabilitiesCurrent = readerContextStamp.Value.CapabilitiesCurrent;
        OnPropertyChanged(nameof(IsReaderAvailable));
        OnPropertyChanged(nameof(CanSave));
        Diagnostics?.SelectReader(
            id,
            readerFeatureCatalog,
            readerGpiCount,
            readerGpoCount,
            capabilitiesCurrent);
        return snapshot;
    }

    private static bool IsSettingsModelCurrent(
        SettingsEditorModel model,
        ReaderRuntimeSnapshot? latestSnapshot) =>
        latestSnapshot is null
        || latestSnapshot.IsStale
        || model.Snapshot.CapabilityRevision == latestSnapshot.CapabilityRevision;

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
        OnPropertyChanged(nameof(IsSettingsLayoutAvailable));
        OnPropertyChanged(nameof(IsGpiSettingsVisible));
        NotifySettingsSectionVisibility();
        OnPropertyChanged(nameof(CanSave));
    }

    private bool IsCurrentReaderContext(Guid id, long version) =>
        !disposed
        && ReaderId == id
        && Volatile.Read(ref readerContextVersion) == version;

    public void CancelPendingOperations()
    {
        CancelActiveOperation(Volatile.Read(ref activeLoadCts));
        CancelActiveOperation(Volatile.Read(ref activeSaveCts));
        Diagnostics?.CancelPendingOperations();
    }

    private static void CancelActiveOperation(CancellationTokenSource? operationCts)
    {
        try
        {
            operationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 页面切换与异步操作完成的释放可能并发发生。
        }
    }

    private static void CancelAndDispose(CancellationTokenSource? operationCts)
    {
        if (operationCts is null)
        {
            return;
        }

        CancelActiveOperation(operationCts);
        operationCts.Dispose();
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
            SettingsEntryRowViewModel? tx = FindRow(SettingsKeys.AntennaTxPowerIndex(antennaId));
            SettingsEntryRowViewModel? rx = FindRow(SettingsKeys.AntennaRxSensitivityIndex(antennaId));
            // ChannelIndex belongs to the complete LLRP RFTransmitter tuple, but it is
            // not an antenna power setting. Keep it out of the old WPF antenna matrix;
            // fixed-frequency UI will own the value when that standard path is added.
            AntennaSettings.Add(new LegacyAntennaSettingsRowViewModel(
                antennaId,
                tx,
                rx,
                channel: null));
        }

        ushort gpiRowCount = ResolveGpiRowCount();
        for (int port = 1; port <= gpiRowCount; port++)
        {
            GpiSettings.Add(new LegacyGpiSettingsRowViewModel(
                checked((ushort)port),
                FindRow(SettingsKeys.StartGpiEnabled),
                FindRow(SettingsKeys.StartGpiPort),
                FindRow(SettingsKeys.StartGpiLevel),
                FindRow(SettingsKeys.StopGpiEnabled),
                FindRow(SettingsKeys.StopGpiPort),
                FindRow(SettingsKeys.StopGpiLevel),
                FindRow(SettingsKeys.StopGpiTimeoutMs),
                FindRow(ImpinjGpiDebounceKey(checked((ushort)port)))));
        }

        Filter1 = BuildFilterRow(1);
        Filter2 = BuildFilterRow(2);
    }

    private ushort ResolveGpiRowCount()
    {
        if (readerGpiCount is 0)
        {
            return 0;
        }

        if (readerGpiCount is > 0)
        {
            return readerGpiCount.Value;
        }

        // 未收到标准能力基线时保留旧 WPF 的四行回退；明确声明不支持 GPI
        // 的设备不显示伪造端口。
        if (readerFeatureCatalog?.HasStandardCapabilityBaseline == true
            && !readerFeatureCatalog.Supports(ReaderFeatures.StandardGpi))
        {
            return 0;
        }

        return 4;
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
            nameof(IsGpiSettingsVisible),
            nameof(IsManualSettingsVisible), nameof(IsPowerSettingsVisible),
            nameof(IsFilterSettingsVisible), nameof(IsStateAwareSettingsVisible),
            nameof(IsReportSettingsVisible), nameof(IsOtherSettingsVisible),
            nameof(IsImpinjExtensionsAvailable), nameof(IsSearchModeVisible), nameof(IsFastIdVisible),
            nameof(IsPhaseAngleVisible), nameof(IsDopplerVisible), nameof(IsFrequencySettingsVisible),
            nameof(IsLowDutySettingsVisible), nameof(IsRfModeEditable), nameof(IsSearchModeEditable),
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

    private void NotifySettingsSectionVisibility()
    {
        OnPropertyChanged(nameof(IsManualSettingsVisible));
        OnPropertyChanged(nameof(IsPowerSettingsVisible));
        OnPropertyChanged(nameof(IsFilterSettingsVisible));
        OnPropertyChanged(nameof(IsStateAwareSettingsVisible));
        OnPropertyChanged(nameof(IsReportSettingsVisible));
        OnPropertyChanged(nameof(IsOtherSettingsVisible));
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

        if (key is "tx-power-index" or "rx-sensitivity-index" or "antenna-ids" or "individual-antenna-settings")
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
        // Capability-table ComboBoxes display a human-readable label (for example,
        // "33 (33 dBm)") while their binding source remains the table index.
        // WPF can write that display text back through the editable ComboBox.Text binding;
        // recover the option value before attempting numeric parsing.
        SettingsOption? displayedOption = entry.Options.FirstOrDefault(option =>
            option.Display is not null
            && string.Equals(option.Display.Trim(), text.Trim(), StringComparison.OrdinalIgnoreCase));
        if (displayedOption?.Value is not null)
        {
            return displayedOption.Value;
        }

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

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CancelPendingOperations();
        lifetimeCts.Cancel();
        lifetimeCts.Dispose();
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
