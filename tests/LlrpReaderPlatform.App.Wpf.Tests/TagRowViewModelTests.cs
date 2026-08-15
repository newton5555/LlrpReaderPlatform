using LlrpReaderPlatform.App.Wpf.ViewModels;
using LlrpReaderPlatform.Contracts.Tagging;
using Xunit;

namespace LlrpReaderPlatform.App.Wpf.Tests;

public sealed class TagRowViewModelTests
{
    [Fact]
    public void Projects_all_columns_from_observation()
    {
        var first = new DateTimeOffset(2026, 1, 1, 10, 30, 0, TimeSpan.Zero);
        var last = new DateTimeOffset(2026, 1, 1, 10, 30, 5, TimeSpan.Zero);
        var tag = new TagObservation
        {
            Epc = "3001",
            Tid = "0AB0",
            PcBitsHex = "3000",
            ReadCount = 7,
            FirstSeen = first,
            LastSeen = last,
            LastRssi = -50,
            LastAntenna = 1,
            LastChannelIndex = 2,
        };

        var row = new TagRowViewModel(tag);

        Assert.Equal("3001", row.Epc);
        Assert.Equal("0AB0", row.Tid);
        Assert.Equal("3000", row.PcBitsHex);
        Assert.Equal(7, row.ReadCount);
        Assert.Equal("10:30:00", row.FirstSeen);
        Assert.Equal("10:30:05", row.LastSeen);
        Assert.Equal((sbyte)-50, row.LastRssi);
        Assert.Equal((sbyte)-50, row.PeakRssi);
        Assert.Equal((ushort)1, row.LastAntenna);
        Assert.Equal((ushort)2, row.LastChannelIndex);
    }

    [Fact]
    public void Update_changes_values_without_replacing_the_row_identity()
    {
        var first = new TagObservation
        {
            Epc = "3001",
            ReadCount = 1,
            FirstSeen = DateTimeOffset.UtcNow,
            LastSeen = DateTimeOffset.UtcNow,
        };
        var row = new TagRowViewModel(first);
        var changed = new List<string?>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        row.Update("Reader A", first with
        {
            ReadCount = 100,
            LastSeen = first.LastSeen.AddSeconds(1),
            LastRssi = -40,
        }, "Known tag");

        Assert.Equal("3001", row.Epc);
        Assert.Equal(100, row.ReadCount);
        Assert.Equal("Reader A", row.ReaderName);
        Assert.Equal("Known tag", row.TagListName);
        Assert.Contains(nameof(TagRowViewModel.ReadCount), changed);
        Assert.Contains(nameof(TagRowViewModel.LastSeen), changed);
        Assert.Contains(nameof(TagRowViewModel.PeakRssi), changed);
    }

    [Fact]
    public void Update_raises_phase_change_when_extension_field_changes()
    {
        var first = new TagObservation
        {
            Epc = "3001",
            ReadCount = 1,
            FirstSeen = DateTimeOffset.UtcNow,
            LastSeen = DateTimeOffset.UtcNow,
            ExtensionFields = new Dictionary<string, string>
            {
                [ReportFieldSemantics.Phase] = "3376",
            },
        };
        var row = new TagRowViewModel(first);
        var changed = new List<string?>();
        row.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        row.Update("Reader A", first with
        {
            ReadCount = 2,
            ExtensionFields = new Dictionary<string, string>
            {
                [ReportFieldSemantics.Phase] = "2416",
            },
        }, "Known tag");

        Assert.Equal("2416", row.Phase);
        Assert.Contains(nameof(TagRowViewModel.Phase), changed);
    }
}
