using LlrpReaderPlatform.Services.Sdk;
using LlrpSdk;
using Tagging = LlrpReaderPlatform.Contracts.Tagging;
using Xunit;

namespace LlrpReaderPlatform.Services.Tests.Sdk;

public sealed class SdkTagAccessMapperTests
{
    [Fact]
    public void BuildEpcSelection_sets_bit_pointer_and_data()
    {
        LlrpSdk.TagSelection selection = SdkTagAccessMapper.BuildEpcSelection("3001");

        Assert.Equal((ushort)1, (ushort)selection.MemoryBank); // Epc
        Assert.Equal((ushort)32, selection.BitPointer);
        Assert.Equal((ushort)16, selection.BitLength);
        Assert.True(selection.Match);
        Assert.Equal(new byte[] { 0x30, 0x01 }, selection.Data.ToArray());
        Assert.Equal(new byte[] { 0xFF, 0xFF }, selection.Mask.ToArray());
    }

    [Fact]
    public void BuildSelection_for_tid_starts_at_bit_zero()
    {
        LlrpSdk.TagSelection selection = SdkTagAccessMapper.BuildSelection("E2003412", Tagging.TagMemoryBank.Tid);

        Assert.Equal((ushort)2, (ushort)selection.MemoryBank); // Tid
        Assert.Equal((ushort)0, selection.BitPointer);
        Assert.Equal((ushort)32, selection.BitLength);
        Assert.Equal(new byte[] { 0xE2, 0x00, 0x34, 0x12 }, selection.Data.ToArray());
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, selection.Mask.ToArray());
    }

    [Fact]
    public void BuildSelection_rejects_target_that_overflows_llrp_bit_length()
    {
        string target = new string('A', (ushort.MaxValue / 8 + 1) * 2);

        Assert.Throws<FormatException>(() => SdkTagAccessMapper.BuildSelection(target, Tagging.TagMemoryBank.Tid));
    }

    [Fact]
    public void MapOperationResult_success_maps_read_data()
    {
        var op = new LlrpSdk.TagAccessOperationResult(
            OpSpecID: 1, Success: true, ReadData: new ushort[] { 0x0AB0 }, WordsWritten: null, Error: null);

        var result = SdkTagAccessMapper.MapOperationResult(op);

        Assert.True(result.Succeeded);
        Assert.Equal("0AB0", result.DataHex);
    }

    [Fact]
    public void MapOperationResult_failure_maps_error()
    {
        var op = new LlrpSdk.TagAccessOperationResult(
            OpSpecID: 1, Success: false, ReadData: [], WordsWritten: null, Error: "boom");

        var result = SdkTagAccessMapper.MapOperationResult(op);

        Assert.False(result.Succeeded);
        Assert.Equal("boom", result.Error);
    }

    [Fact]
    public void MapOperationResult_null_returns_error()
    {
        var result = SdkTagAccessMapper.MapOperationResult(null);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void ToWordBytes_is_big_endian()
    {
        byte[] bytes = SdkTagAccessMapper.ToWordBytes([0x0AB0, 0x0001]);

        Assert.Equal(new byte[] { 0x0A, 0xB0, 0x00, 0x01 }, bytes);
    }

    [Fact]
    public void ParseWords_maps_big_endian_hex_to_u16_words()
    {
        Assert.Equal(new ushort[] { 0x0AB0, 0x0001 }, SdkTagAccessMapper.ParseWords("0AB0 0001"));
    }

    [Fact]
    public void ParseWords_rejects_partial_word()
    {
        Assert.Throws<FormatException>(() => SdkTagAccessMapper.ParseWords("0AB"));
    }

    [Fact]
    public void ValidateReadRequest_rejects_zero_word_count()
    {
        var request = new Tagging.TagReadRequest { Epc = "3001", WordCount = 0 };

        Assert.Throws<FormatException>(() => SdkTagAccessMapper.ValidateReadRequest(request));
    }

    [Fact]
    public void ValidateWriteRequest_accepts_separated_hex_and_rejects_empty_target()
    {
        var valid = new Tagging.TagWriteRequest { Epc = "30-01", DataHex = "0AB0 0001" };
        SdkTagAccessMapper.ValidateWriteRequest(valid);

        var invalid = new Tagging.TagWriteRequest { Epc = "", DataHex = "0AB0" };
        Assert.Throws<FormatException>(() => SdkTagAccessMapper.ValidateWriteRequest(invalid));
    }
}
