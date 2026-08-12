using System.Globalization;
using LlrpReaderPlatform.App.Wpf.Converters;
using LlrpReaderPlatform.Contracts.Tagging;
using Xunit;

namespace LlrpReaderPlatform.App.Wpf.Tests;

public sealed class TagMemoryBankDisplayConverterTests
{
    [Theory]
    [InlineData(TagMemoryBank.Epc, "EPC")]
    [InlineData(TagMemoryBank.Tid, "TID")]
    [InlineData(TagMemoryBank.User, "User")]
    [InlineData(TagMemoryBank.Reserved, "Reserved")]
    public void Uses_legacy_tag_memory_labels(TagMemoryBank bank, string expected)
    {
        var converter = new TagMemoryBankDisplayConverter();

        object result = converter.Convert(
            bank,
            typeof(string),
            parameter: null,
            CultureInfo.InvariantCulture);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Display_labels_are_one_way()
    {
        var converter = new TagMemoryBankDisplayConverter();

        Assert.Throws<NotSupportedException>(() => converter.ConvertBack(
            "EPC",
            typeof(TagMemoryBank),
            parameter: null,
            CultureInfo.InvariantCulture));
    }
}
