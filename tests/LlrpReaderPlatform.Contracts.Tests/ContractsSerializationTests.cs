using System.Text.Json;
using LlrpReaderPlatform.Contracts.Discovery;
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
        original.Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (original with { LlrpVersion = (LlrpProtocolVersionOption)99 }).Validate());
        Assert.Equal("fe80::10", ReaderEndpoint.NormalizeHost(" [FE80::10] "));
        Assert.Equal("[fe80::10]:5084", ReaderEndpoint.Format("[FE80::10]", 5084));
        Assert.Throws<ArgumentException>(() =>
            (original with { Host = "[]" }).Validate());
    }

    [Fact]
    public void Discovered_readers_normalize_hosts_ports_and_duplicate_endpoints()
    {
        IReadOnlyList<DiscoveredReader> normalized = DiscoveredReaderNormalization.Normalize(
        [
            new DiscoveredReader(" reader-v6 ", "reader-v6.local", "[FE80::10]", 5084, new Dictionary<string, string>()),
            new DiscoveredReader("alias", "alias.local", "fe80::10", 5084, new Dictionary<string, string>()),
            new DiscoveredReader(string.Empty, string.Empty, "10.0.0.2", 0, new Dictionary<string, string>()),
            new DiscoveredReader("invalid", string.Empty, string.Empty, 5084, new Dictionary<string, string>()),
        ]);

        Assert.Equal(2, normalized.Count);
        Assert.Equal("reader-v6", normalized[0].DisplayName);
        Assert.Equal("fe80::10", normalized[0].IpAddress);
        Assert.Equal("10.0.0.2", normalized[1].Host);
        Assert.Equal(5084, normalized[1].Port);
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
        // 记录包含新增的 ExtensionReportFields 集合字段，改用 JSON 往返等价比较，避免
        // 引用型集合导致 record 值相等失败；语义字段内容通过断言单独锁定。
        Assert.Equal(JsonSerializer.Serialize(original.Report, JsonOptions), JsonSerializer.Serialize(restored.Report, JsonOptions));

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
