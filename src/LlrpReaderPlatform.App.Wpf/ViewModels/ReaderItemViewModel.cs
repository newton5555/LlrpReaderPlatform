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
    public string Endpoint => $"{Host}:{Port}";
    public string State => Snapshot.State.ToString();
    public string StatusText => Snapshot.State switch
    {
        ReaderState.Inventorying => "寻卡中",
        ReaderState.Connecting => "连接中",
        ReaderState.Faulted => "连接故障",
        ReaderState.Disconnecting => "断开中",
        ReaderState.Connected => "已连接",
        ReaderState.Disconnected when !Snapshot.IsStale && Snapshot.CapabilityRevision > 0 => "已同步能力",
        _ => "未连接",
    };
    public string Details => string.Join(" · ", new[]
    {
        Protocol,
        Model,
        Firmware,
        Snapshot.Error,
    }.Where(static value => !string.IsNullOrWhiteSpace(value)));
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
