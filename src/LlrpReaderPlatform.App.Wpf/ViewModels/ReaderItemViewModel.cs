using CommunityToolkit.Mvvm.ComponentModel;
using LlrpReaderPlatform.Contracts.Readers;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>设备列表项（对齐旧 ReaderItemViewModel）：对 ReaderRuntimeSnapshot 的 UI 投影，IsEnabled 可写并回调变更。</summary>
public sealed partial class ReaderItemViewModel : ObservableObject
{
    private readonly Action<bool>? onEnabledChanged;

    public ReaderItemViewModel(ReaderRuntimeSnapshot snapshot, Action<bool>? onEnabledChanged = null)
    {
        Snapshot = snapshot;
        this.onEnabledChanged = onEnabledChanged;
        isEnabled = snapshot.IsEnabled;
    }

    public ReaderRuntimeSnapshot Snapshot { get; }

    public Guid ReaderId => Snapshot.ReaderId;
    public string Name => Snapshot.Profile.Name;
    public string Host => Snapshot.Profile.Host;
    public int Port => Snapshot.Profile.Port;
    public string Endpoint => ReaderEndpointFormatter.Format(Host, Port);
    public string State => Snapshot.State.ToString();
    public string StatusText => Snapshot.State switch
    {
        ReaderState.Inventorying => "寻卡中",
        ReaderState.Stopping => "停止中",
        ReaderState.Connecting => "连接中",
        ReaderState.Faulted => "连接故障",
        ReaderState.Disconnecting => "断开中",
        ReaderState.Connected => "已连接",
        ReaderState.Disconnected when !Snapshot.IsStale && Snapshot.CapabilityRevision > 0 => "已同步能力（短连接空闲）",
        _ => "未连接",
    };
    public string ConnectionSummary => Snapshot.State switch
    {
        ReaderState.Inventorying => "LLRP 长连接已建立",
        ReaderState.Connected => "LLRP 短连接已建立",
        ReaderState.Connecting => "正在建立 LLRP 连接",
        ReaderState.Disconnecting => "正在释放 LLRP 连接",
        ReaderState.Stopping => "正在停止并释放 LLRP 连接",
        ReaderState.Disconnected when !Snapshot.IsStale && Snapshot.CapabilityRevision > 0
            => "能力已同步，短连接已释放，可执行下一次操作",
        ReaderState.Faulted => "LLRP 连接故障，需要重新激活",
        _ => "尚未建立 LLRP 连接",
    };
    public string Details => string.Join(" · ", new[]
    {
        ProtocolPolicy,
        Protocol,
        ConnectionSummary,
        Model,
        Firmware,
        Snapshot.Error,
    }.Where(static value => !string.IsNullOrWhiteSpace(value)));
    /// <summary>
    /// 持久化的连接策略。实际协商版本可能在 Reader 离线或应用刚重启时为空，
    /// 但策略仍然必须可见，尤其是强制 LLRP 1.0.1 的标准设备。
    /// </summary>
    public string ProtocolPolicy => Snapshot.Profile.LlrpVersion switch
    {
        LlrpProtocolVersionOption.Auto => "Policy Auto",
        LlrpProtocolVersionOption.Force101 => "Policy Force 1.0.1",
        LlrpProtocolVersionOption.Force11 => "Policy Force 1.1",
        _ => "Policy Unknown",
    };
    public string? Protocol => Snapshot.NegotiatedProtocolVersion switch
    {
        LlrpProtocolVersion.Version101 => "LLRP 1.0.1",
        LlrpProtocolVersion.Version11 => "LLRP 1.1",
        _ => null,
    };
    public string? Model => Snapshot.Model;
    public string? Firmware => Snapshot.Firmware;
    public string? Error => Snapshot.Error;

    [ObservableProperty]
    private bool isEnabled;

    partial void OnIsEnabledChanged(bool value) => onEnabledChanged?.Invoke(value);
}
