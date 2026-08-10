using System.Text.Json;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Contracts.Tagging;
using Xunit;

namespace LlrpReaderPlatform.Contracts.Tests;

public sealed class ContractsSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void Reader_profile_round_trips_without_sdk_or_ui_types()
    {
        var original = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Name = "R420",
            Host = "192.0.2.10",
            Port = 5084,
            LlrpVersion = LlrpProtocolVersionOption.Force101,
            IsEnabled = false,
        };

        string json = JsonSerializer.Serialize(original, JsonOptions);
        ReaderProfile? restored = JsonSerializer.Deserialize<ReaderProfile>(json, JsonOptions);

        Assert.NotNull(restored);
        Assert.Equal(original, restored);
    }

    [Fact]
    public void Inventory_and_tag_access_contracts_round_trip_semantically()
    {
        var original = new InventorySpec
        {
            Antennas = [1, 4],
            DurationSeconds = 15,
            Report = new InventoryReportSpec
            {
                IncludeAntennaId = true,
                IncludeChannelIndex = false,
                IncludePeakRssi = true,
                IncludePcBits = true,
            },
        };

        string json = JsonSerializer.Serialize(original, JsonOptions);
        InventorySpec? restored = JsonSerializer.Deserialize<InventorySpec>(json, JsonOptions);

        Assert.NotNull(restored);
        Assert.Equal(original.Antennas, restored.Antennas);
        Assert.Equal(original.DurationSeconds, restored.DurationSeconds);
        Assert.Equal(original.Report, restored.Report);

        var request = new TagWriteRequest
        {
            Epc = "300833B2DDD9014000000000",
            SelectionBank = TagMemoryBank.Epc,
            MemoryBank = TagMemoryBank.User,
            OffsetWords = 2,
            DataHex = "ABCD1234",
            AntennaId = 4,
            AccessPasswordHex = "00000000",
        };

        string requestJson = JsonSerializer.Serialize(request, JsonOptions);
        TagWriteRequest? restoredRequest = JsonSerializer.Deserialize<TagWriteRequest>(requestJson, JsonOptions);

        Assert.NotNull(restoredRequest);
        Assert.Equal(request, restoredRequest);
    }

    [Fact]
    public void Reader_feature_catalog_round_trips_as_ui_neutral_contract()
    {
        var original = new ReaderFeatureCatalog
        {
            SupportedFeatures =
            [
                ReaderFeatures.StandardInventory,
                ReaderFeatures.ImpinjFastId,
            ],
        };

        string json = JsonSerializer.Serialize(original, JsonOptions);
        ReaderFeatureCatalog? restored = JsonSerializer.Deserialize<ReaderFeatureCatalog>(json, JsonOptions);

        Assert.NotNull(restored);
        Assert.True(restored.Supports(ReaderFeatures.StandardInventory));
        Assert.True(restored.Supports(ReaderFeatures.ImpinjFastId));
        Assert.False(restored.Supports(ReaderFeatures.ImpinjDoppler));
    }

    [Fact]
    public void Tag_observation_round_trips_extension_fields_without_sdk_types()
    {
        var original = new TagObservation
        {
            Epc = "3001",
            Tid = "E200",
            ReadCount = 2,
            ExtensionFields = new Dictionary<string, string>
            {
                ["impinj.serializedTid"] = "E200",
                ["vendor.phase"] = "17",
            },
        };

        string json = JsonSerializer.Serialize(original, JsonOptions);
        TagObservation? restored = JsonSerializer.Deserialize<TagObservation>(json, JsonOptions);

        Assert.NotNull(restored);
        Assert.Equal("E200", restored.Tid);
        Assert.Equal("17", restored.ExtensionFields["vendor.phase"]);
    }
}
