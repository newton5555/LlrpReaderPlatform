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
        Assert.Equal((ushort)1, row.LastAntenna);
        Assert.Equal((ushort)2, row.LastChannelIndex);
    }
}
