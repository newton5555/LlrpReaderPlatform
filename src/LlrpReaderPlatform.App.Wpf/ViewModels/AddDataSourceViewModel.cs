using System.Globalization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Discovery;
using LlrpReaderPlatform.Contracts.Errors;
using LlrpReaderPlatform.Contracts.Readers;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

public sealed record LlrpVersionOptionItem(LlrpProtocolVersionOption Value, string Display);

/// <summary>
/// 添加数据源页（对齐旧 AddDataSourceViewModel）：Host/Name/Port 输入、mDNS 发现与选用、
/// 提交（Probe→持久化→注册→激活并同步能力）。提交成功后触发 <see cref="DataSourceAdded"/>
/// 由 Shell 处理导航。
/// </summary>
public partial class AddDataSourceViewModel : ObservableObject, IPageOperationOwner, IDisposable
{
    private readonly IReaderManager readerManager;
    private readonly IReaderDiscoveryService discovery;
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly CancellationToken lifetimeToken;
    private CancellationTokenSource? activeOperationCts;
    private bool disposed;

    [ObservableProperty]
    private string host = "192.0.2.1";

    [ObservableProperty]
    private string port = "5084";

    [ObservableProperty]
    private string readerName = "Reader";

    [ObservableProperty]
    private LlrpProtocolVersionOption llrpVersion = LlrpProtocolVersionOption.Auto;

    [ObservableProperty]
    private string? status;

    [ObservableProperty]
    private string? probeSummary;

    [ObservableProperty]
    private string? extensionSummary;

    [ObservableProperty]
    private bool hasProbeResult;

    [ObservableProperty]
    private bool isDiscovering;

    [ObservableProperty]
    private bool isDiscoveryPanelOpen;

    [ObservableProperty]
    private bool isSubmitting;

    [ObservableProperty]
    private bool isProbing;

    /// <summary>Probe、发现或提交期间锁定端点编辑，避免页面修改在途操作的输入。</summary>
    public bool IsInputEnabled => !disposed && !IsProbing && !IsSubmitting && !IsDiscovering;

    public AddDataSourceViewModel(IReaderManager readerManager, IReaderDiscoveryService discovery)
    {
        this.readerManager = readerManager;
        this.discovery = discovery;
        lifetimeToken = lifetimeCts.Token;
    }

    public IReadOnlyList<LlrpVersionOptionItem> LlrpVersions { get; } =
    [
        new(LlrpProtocolVersionOption.Auto, "Auto (1.1 → 1.0.1)"),
        new(LlrpProtocolVersionOption.Force101, "LLRP 1.0.1"),
        new(LlrpProtocolVersionOption.Force11, "LLRP 1.1"),
    ];

    /// <summary>提交成功时触发（携带新 ReaderId）。</summary>
    public event EventHandler<Guid>? DataSourceAdded;

    /// <summary>用户取消添加时触发。</summary>
    public event EventHandler? CancelRequested;

    public ObservableCollection<DiscoveredReaderViewModel> Discovered { get; } = [];

    partial void OnHostChanged(string value) => InvalidateProbeResult();

    partial void OnPortChanged(string value) => InvalidateProbeResult();

    partial void OnLlrpVersionChanged(LlrpProtocolVersionOption value) => InvalidateProbeResult();

    partial void OnIsProbingChanged(bool value) => OnPropertyChanged(nameof(IsInputEnabled));

    partial void OnIsSubmittingChanged(bool value) => OnPropertyChanged(nameof(IsInputEnabled));

    partial void OnIsDiscoveringChanged(bool value) => OnPropertyChanged(nameof(IsInputEnabled));

    [RelayCommand]
    private async Task ProbeAsync()
    {
        if (IsProbing || IsSubmitting || IsDiscovering)
        {
            return;
        }

        if (!TryBuildProfile(isEnabled: false, out ReaderProfile profile))
        {
            return;
        }

        IsProbing = true;
        Status = $"正在 Probe {ReaderEndpointFormatter.Format(profile.Host, profile.Port)}...";
        using CancellationTokenSource operationCts = BeginOperation();
        try
        {
            ReaderProbeResult result = await readerManager.ProbeAsync(
                profile,
                operationCts.Token);
            if (disposed || operationCts.IsCancellationRequested)
            {
                return;
            }

            HasProbeResult = true;
            ProbeSummary = FormatProbeSummary(result);
            ExtensionSummary = result.Succeeded
                ? FormatExtensionSummary(result.MatchedExtensionIds)
                : "扩展匹配：未执行";
            Status = result.Succeeded
                ? "Probe 成功；设备尚未添加到平台。"
                : PlatformErrorDisplay.Failure("Probe", result.ErrorCode, result.Error);
        }
        catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
        {
            if (!disposed && !lifetimeCts.IsCancellationRequested)
            {
                Status = "Probe 已取消。";
            }
        }
        catch (Exception ex)
        {
            if (!disposed)
            {
                HasProbeResult = false;
                ProbeSummary = null;
                ExtensionSummary = null;
                Status = PlatformErrorDisplay.Failure("Probe", ex);
            }
        }
        finally
        {
            IsProbing = false;
            EndOperation(operationCts);
        }
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (IsProbing || IsSubmitting || IsDiscovering)
        {
            return;
        }

        if (!TryBuildProfile(isEnabled: true, out ReaderProfile profile))
        {
            return;
        }

        IsSubmitting = true;
        using CancellationTokenSource operationCts = BeginOperation();

        try
        {
            ReaderAddResult result = await readerManager.AddAsync(
                profile,
                enableAfterAdding: true,
                operationCts.Token);
            if (disposed || operationCts.IsCancellationRequested)
            {
                return;
            }

            HasProbeResult = true;
            ProbeSummary = FormatProbeSummary(result);
            ExtensionSummary = FormatExtensionSummary(result.MatchedExtensionIds);
            if (result.Succeeded)
            {
                Status = $"已添加 {ReaderEndpointFormatter.Format(profile.Host, profile.Port)}";
                IsDiscoveryPanelOpen = false;
                if (!disposed)
                {
                    DataSourceAdded?.Invoke(this, profile.Id);
                }
            }
            else
            {
                Status = PlatformErrorDisplay.Failure("添加", result.ErrorCode, result.Error);
            }
        }
        catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
        {
            if (!disposed && !lifetimeCts.IsCancellationRequested)
            {
                Status = "添加已取消。";
            }
        }
        catch (Exception ex)
        {
            if (!disposed)
            {
                Status = PlatformErrorDisplay.Failure("添加", ex);
            }
        }
        finally
        {
            IsSubmitting = false;
            EndOperation(operationCts);
        }
    }

    [RelayCommand]
    private async Task DiscoverAsync()
    {
        if (IsProbing || IsSubmitting || IsDiscovering)
        {
            return;
        }

        IsDiscovering = true;
        IsDiscoveryPanelOpen = true;
        Status = "正在扫描 _llrp._tcp...";
        using CancellationTokenSource operationCts = BeginOperation();
        try
        {
            IReadOnlyList<DiscoveredReader> found = await discovery.DiscoverAsync(
                TimeSpan.FromSeconds(3),
                operationCts.Token);
            if (disposed || operationCts.IsCancellationRequested)
            {
                return;
            }

            IReadOnlyList<DiscoveredReader> normalized = DiscoveredReaderNormalization.Normalize(found);
            Discovered.Clear();
            foreach (DiscoveredReader r in normalized)
            {
                Discovered.Add(new DiscoveredReaderViewModel(r));
            }

            Status = normalized.Count == 0 ? "未发现 LLRP 设备" : $"发现 {normalized.Count} 个设备，可选用后提交";
        }
        catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
        {
            if (!disposed && !lifetimeCts.IsCancellationRequested)
            {
                Status = "发现已取消。";
            }
        }
        catch (Exception ex)
        {
            if (!disposed)
            {
                Discovered.Clear();
                Status = PlatformErrorDisplay.Failure("发现", ex);
            }
        }
        finally
        {
            IsDiscovering = false;
            EndOperation(operationCts);
        }
    }

    [RelayCommand]
    private void UseDiscovered(DiscoveredReaderViewModel item)
    {
        if (disposed || !IsInputEnabled)
        {
            return;
        }

        Host = ReaderEndpointFormatter.NormalizeHost(item.IpAddress);
        ReaderName = item.DisplayName;
        Port = Math.Clamp(item.Port, 1, 65535).ToString(CultureInfo.InvariantCulture);
        Status = $"已选用 {item.DisplayName}，可提交";
    }

    [RelayCommand]
    private void CloseDiscoveryPanel() => IsDiscoveryPanelOpen = false;

    [RelayCommand]
    private void Cancel()
    {
        if (disposed)
        {
            return;
        }

        activeOperationCts?.Cancel();
        IsDiscoveryPanelOpen = false;
        Discovered.Clear();
        HasProbeResult = false;
        ProbeSummary = null;
        ExtensionSummary = null;
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatProbeSummary(ReaderAddResult result)
    {
        return FormatProbeSummary(
            result.Model,
            result.Firmware,
            result.NegotiatedProtocolVersion);
    }

    private static string FormatProbeSummary(ReaderProbeResult result)
    {
        return FormatProbeSummary(
            result.Model,
            result.Firmware,
            result.NegotiatedProtocolVersion);
    }

    private static string FormatProbeSummary(
        string? model,
        string? firmware,
        LlrpProtocolVersion? negotiatedProtocolVersion)
    {
        string protocol = negotiatedProtocolVersion switch
        {
            LlrpProtocolVersion.Version101 => "LLRP 1.0.1",
            LlrpProtocolVersion.Version11 => "LLRP 1.1",
            _ => "LLRP 版本未识别",
        };
        string modelDisplay = string.IsNullOrWhiteSpace(model) ? "型号未知" : model;
        string firmwareDisplay = string.IsNullOrWhiteSpace(firmware) ? "固件未知" : firmware;
        return $"Probe：{protocol} · 型号 {modelDisplay} · 固件 {firmwareDisplay}";
    }

    private static string FormatExtensionSummary(IReadOnlyList<string> extensionIds) =>
        extensionIds.Count == 0
            ? "扩展匹配：标准 LLRP 路径"
            : $"扩展匹配：{string.Join(", ", extensionIds)}";

    private bool TryBuildProfile(bool isEnabled, out ReaderProfile profile)
    {
        string normalizedHost = ReaderEndpointFormatter.NormalizeHost(Host);
        if (normalizedHost.Length == 0)
        {
            profile = null!;
            Status = "Host 不能为空。";
            return false;
        }

        if (!int.TryParse(
                Port.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int portValue)
            || portValue is < 1 or > 65535)
        {
            profile = null!;
            Status = "LLRP Port 必须是 1 到 65535 的整数。";
            return false;
        }

        profile = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(ReaderName) ? normalizedHost : ReaderName.Trim(),
            Host = normalizedHost,
            Port = portValue,
            LlrpVersion = LlrpVersion,
            IsEnabled = isEnabled,
        };
        return true;
    }

    private void InvalidateProbeResult()
    {
        if (IsProbing)
        {
            return;
        }

        HasProbeResult = false;
        ProbeSummary = null;
        ExtensionSummary = null;
    }

    public void CancelPendingOperations()
    {
        CancellationTokenSource? operationCts = Volatile.Read(ref activeOperationCts);
        try
        {
            operationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 页面切换与 Probe/发现完成的释放可能并发发生。
        }
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

    private CancellationTokenSource BeginOperation()
    {
        var operationCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        CancellationTokenSource? previous = Interlocked.Exchange(ref activeOperationCts, operationCts);
        previous?.Cancel();
        return operationCts;
    }

    private void EndOperation(CancellationTokenSource operationCts)
    {
        Interlocked.CompareExchange(ref activeOperationCts, null, operationCts);
    }
}
