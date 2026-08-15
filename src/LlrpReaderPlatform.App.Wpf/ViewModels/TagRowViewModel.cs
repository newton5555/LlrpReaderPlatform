using CommunityToolkit.Mvvm.ComponentModel;
using LlrpReaderPlatform.Contracts.Tagging;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>寻卡结果表格行（对聚合 TagObservation 的轻量、可原位更新投影）。</summary>
public sealed class TagRowViewModel : ObservableObject
{
    private int index;
    private string readerName = string.Empty;
    private string tagListName = string.Empty;
    private string tagColorHex = string.Empty;
    private TagObservation tag;

    public TagRowViewModel(TagObservation tag, string? tagListName = null, string? tagColorHex = null)
        : this(Guid.Empty, string.Empty, tag, tagListName, tagColorHex)
    {
    }

    public TagRowViewModel(
        Guid readerId,
        string readerName,
        TagObservation tag,
        string? tagListName = null,
        string? tagColorHex = null)
    {
        ArgumentNullException.ThrowIfNull(tag);
        ReaderId = readerId;
        Epc = tag.Epc;
        this.tag = tag;
        this.readerName = readerName;
        this.tagListName = tagListName ?? string.Empty;
        this.tagColorHex = tagColorHex ?? string.Empty;
    }

    public Guid ReaderId { get; }
    public string Epc { get; }
    public TagObservation Tag => tag;

    public int Index
    {
        get => index;
        set => SetProperty(ref index, value);
    }

    public string ReaderName => readerName;
    public string TagListName => tagListName;
    public string TagColorHex => tagColorHex;
    public string Tid => tag.Tid;
    public bool HasTid => !string.IsNullOrWhiteSpace(tag.Tid);
    public string? PcBitsHex => tag.PcBitsHex;
    public long ReadCount => tag.ReadCount;
    public string FirstSeen => tag.FirstSeen.ToString("HH:mm:ss");
    public string LastSeen => tag.LastSeen.ToString("HH:mm:ss");
    /// <summary>旧 WPF 的 Peak RSSI 列；平台报告模型内部仍保留 LastRssi 命名。</summary>
    public sbyte? PeakRssi => tag.LastRssi;
    public sbyte? LastRssi => tag.LastRssi;
    public ushort? LastAntenna => tag.LastAntenna;
    public ushort? LastChannelIndex => tag.LastChannelIndex;

    /// <summary>报告扩展字段（来自 TagObservation.ExtensionFields；无则空）。读取语义展示键，
    /// 回退到厂商投影键，保证 Impinj/Zebra 均能显示相位列（ADR-0013）。</summary>
    public string? Phase => Field("phase", "zebra.phase", "impinj.rfPhaseAngle");
    public string? Gps => Field("gps", "zebra.gps");
    public string? Xpc => Field("xpc", "zebra.xpc");

    private string? Field(params string[] keys)
    {
        foreach (string key in keys)
        {
            if (tag.ExtensionFields.TryGetValue(key, out string? value) && !string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }

    public void Update(
        string nextReaderName,
        TagObservation nextTag,
        string nextTagListName,
        string nextTagColorHex = "")
    {
        ArgumentNullException.ThrowIfNull(nextTag);
        if (!string.Equals(Epc, nextTag.Epc, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("A tag row cannot change its EPC identity.", nameof(nextTag));
        }

        TagObservation previous = tag;
        string previousReaderName = readerName;
        string previousTagListName = tagListName;
        string previousTagColorHex = tagColorHex;
        tag = nextTag;
        readerName = nextReaderName;
        tagListName = nextTagListName;
        tagColorHex = nextTagColorHex;

        if (!ReferenceEquals(previous, nextTag))
        {
            OnPropertyChanged(nameof(Tag));
        }
        if (!string.Equals(previousReaderName, nextReaderName, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(ReaderName));
        }
        if (!string.Equals(previousTagListName, nextTagListName, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(TagListName));
        }
        if (!string.Equals(previousTagColorHex, nextTagColorHex, StringComparison.OrdinalIgnoreCase))
        {
            OnPropertyChanged(nameof(TagColorHex));
        }
        if (!string.Equals(previous.Tid, nextTag.Tid, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(Tid));
            OnPropertyChanged(nameof(HasTid));
        }
        if (!string.Equals(previous.PcBitsHex, nextTag.PcBitsHex, StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(PcBitsHex));
        }
        if (previous.ReadCount != nextTag.ReadCount)
        {
            OnPropertyChanged(nameof(ReadCount));
        }
        if (previous.FirstSeen != nextTag.FirstSeen)
        {
            OnPropertyChanged(nameof(FirstSeen));
        }
        if (previous.LastSeen != nextTag.LastSeen)
        {
            OnPropertyChanged(nameof(LastSeen));
        }
        if (previous.LastRssi != nextTag.LastRssi)
        {
            OnPropertyChanged(nameof(PeakRssi));
            OnPropertyChanged(nameof(LastRssi));
        }
        if (previous.LastAntenna != nextTag.LastAntenna)
        {
            OnPropertyChanged(nameof(LastAntenna));
        }
        if (previous.LastChannelIndex != nextTag.LastChannelIndex)
        {
            OnPropertyChanged(nameof(LastChannelIndex));
        }
    }
}
