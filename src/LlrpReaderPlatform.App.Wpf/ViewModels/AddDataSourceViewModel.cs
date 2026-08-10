using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Discovery;
using LlrpReaderPlatform.Contracts.Readers;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>
/// 添加数据源页（对齐旧 AddDataSourceViewModel）：Host/Name/Port 输入、mDNS 发现与选用、
/// 提交（Probe→持久化→注册→激活并同步能力）。提交成功后触发 <see cref="DataSourceAdded"/>
/// 由 Shell 处理导航。
/// </summary>
public partial class AddDataSourceViewModel : ObservableObject
{
    private readonly IReaderManager readerManager;
    private readonly IReaderDiscoveryService discovery;

    [ObservableProperty]
    private string host = "192.0.2.1";

    [ObservableProperty]
    private ushort port = 5084;

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

    public AddDataSourceViewModel(IReaderManager readerManager, IReaderDiscoveryService discovery)
    {
        this.readerManager = readerManager;
        this.discovery = discovery;
    }

    public IReadOnlyList<LlrpProtocolVersionOption> LlrpVersions { get; } = Enum.GetValues<LlrpProtocolVersionOption>();

    /// <summary>提交成功时触发（携带新 ReaderId）。</summary>
    public event EventHandler<Guid>? DataSourceAdded;

    /// <summary>用户取消添加时触发。</summary>
    public event EventHandler? CancelRequested;

    public ObservableCollection<DiscoveredReaderViewModel> Discovered { get; } = [];

    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (IsSubmitting)
        {
            return;
        }

        IsSubmitting = true;
        var profile = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(ReaderName) ? Host : ReaderName,
            Host = Host,
            Port = Port,
            LlrpVersion = LlrpVersion,
            IsEnabled = true,
        };

        try
        {
            ReaderAddResult result = await readerManager.AddAsync(profile, enableAfterAdding: true, CancellationToken.None);
            HasProbeResult = true;
            ProbeSummary = FormatProbeSummary(result);
            ExtensionSummary = result.MatchedExtensionIds.Count == 0
                ? "扩展匹配：标准 LLRP 路径"
                : $"扩展匹配：{string.Join(", ", result.MatchedExtensionIds)}";
            if (result.Succeeded)
            {
                Status = $"已添加 {profile.Host}:{profile.Port}";
                IsDiscoveryPanelOpen = false;
                DataSourceAdded?.Invoke(this, profile.Id);
            }
            else
            {
                Status = $"添加失败: {result.Error}";
            }
        }
        catch (Exception ex)
        {
            Status = $"添加失败: {ex.Message}";
        }
        finally
        {
            IsSubmitting = false;
        }
    }

    [RelayCommand]
    private async Task DiscoverAsync()
    {
        if (IsDiscovering)
        {
            return;
        }

        IsDiscovering = true;
        IsDiscoveryPanelOpen = true;
        Status = "正在扫描 _llrp._tcp...";
        try
        {
            IReadOnlyList<DiscoveredReader> found = await discovery.DiscoverAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
            Discovered.Clear();
            foreach (DiscoveredReader r in found)
            {
                Discovered.Add(new DiscoveredReaderViewModel(r));
            }

            Status = found.Count == 0 ? "未发现 LLRP 设备" : $"发现 {found.Count} 个设备，可选用后提交";
        }
        catch (Exception ex)
        {
            Discovered.Clear();
            Status = $"发现失败: {ex.Message}";
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    [RelayCommand]
    private void UseDiscovered(DiscoveredReaderViewModel item)
    {
        Host = item.IpAddress;
        ReaderName = item.DisplayName;
        Port = (ushort)Math.Clamp(item.Port, 1, 65535);
        Status = $"已选用 {item.DisplayName}，可提交";
    }

    [RelayCommand]
    private void CloseDiscoveryPanel() => IsDiscoveryPanelOpen = false;

    [RelayCommand]
    private void Cancel() => CancelRequested?.Invoke(this, EventArgs.Empty);

    private static string FormatProbeSummary(ReaderAddResult result)
    {
        string protocol = result.NegotiatedProtocolVersion switch
        {
            LlrpProtocolVersion.Version101 => "LLRP 1.0.1",
            LlrpProtocolVersion.Version11 => "LLRP 1.1",
            _ => "LLRP 版本未识别",
        };
        string model = string.IsNullOrWhiteSpace(result.Model) ? "型号未知" : result.Model;
        string firmware = string.IsNullOrWhiteSpace(result.Firmware) ? "固件未知" : result.Firmware;
        return $"Probe：{protocol} · 型号 {model} · 固件 {firmware}";
    }
}
