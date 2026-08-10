using LlrpReaderPlatform.Contracts.Tagging;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>寻卡结果表格行（对聚合 TagObservation 的轻量投影）。</summary>
public sealed record TagRowViewModel
{
    public TagRowViewModel(TagObservation tag, string? tagListName = null)
        : this(Guid.Empty, string.Empty, tag, tagListName)
    {
    }

    public TagRowViewModel(Guid readerId, string readerName, TagObservation tag, string? tagListName = null)
    {
        ReaderId = readerId;
        ReaderName = readerName;
        Tag = tag;
        TagListName = tagListName ?? string.Empty;
    }

    public Guid ReaderId { get; }
    public string ReaderName { get; }
    public TagObservation Tag { get; }
    public string TagListName { get; }
    public int Index { get; init; }
    public string Epc => Tag.Epc;
    public string Tid => Tag.Tid;
    public string? PcBitsHex => Tag.PcBitsHex;
    public long ReadCount => Tag.ReadCount;
    public string FirstSeen => Tag.FirstSeen.ToString("HH:mm:ss");
    public string LastSeen => Tag.LastSeen.ToString("HH:mm:ss");
    public sbyte? LastRssi => Tag.LastRssi;
    public ushort? LastAntenna => Tag.LastAntenna;
    public ushort? LastChannelIndex => Tag.LastChannelIndex;
}
