using System.Text.Json;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Tagging;
using Xunit;

namespace LlrpReaderPlatform.VirtualReader.Tests;

public sealed class VirtualReaderScenarioLoaderTests
{
    [Fact]
    public async Task LoadAsync_imports_jsonl_and_snapshot_using_relative_paths()
    {
        using var directory = new TemporaryDirectory();
        string logDirectory = Path.Combine(directory.Path, "tag-logs", "reader");
        string snapshotDirectory = Path.Combine(directory.Path, "inventory-snapshots", "reader");
        Directory.CreateDirectory(logDirectory);
        Directory.CreateDirectory(snapshotDirectory);

        Guid readerId = Guid.NewGuid();
        Guid runId = Guid.NewGuid();
        TagObservation first = Tag("3001", DateTimeOffset.UnixEpoch.AddMilliseconds(10), -40);
        TagObservation second = Tag("3002", DateTimeOffset.UnixEpoch.AddMilliseconds(35), -42);
        string logPath = Path.Combine(logDirectory, $"{runId:N}.jsonl");
        await File.WriteAllLinesAsync(logPath, [
            JsonSerializer.Serialize(new { id = runId, readerId, tag = first }, JsonOptions),
            JsonSerializer.Serialize(new { id = runId, readerId, tag = second }, JsonOptions),
        ]);

        var snapshot = new InventoryRunSnapshot
        {
            Run = new InventoryRunRecord
            {
                Id = runId,
                ReaderId = readerId,
                StartedAtUtc = DateTimeOffset.UtcNow,
                TotalReadCount = 2,
                UniqueTagCount = 2,
            },
            Tags = [first, second],
        };
        string snapshotPath = Path.Combine(snapshotDirectory, $"{runId:N}.json");
        await File.WriteAllTextAsync(snapshotPath, JsonSerializer.Serialize(snapshot, JsonOptions));

        string scenarioPath = Path.Combine(directory.Path, "scenario.json");
        var scenario = new VirtualReaderScenario
        {
            Name = "captured",
            ReaderId = readerId,
            Inventory = new VirtualInventorySource
            {
                TagLogPath = "tag-logs",
                SnapshotPath = "inventory-snapshots",
            },
            TagMemory = [new VirtualTagMemorySeed { Epc = "3001", UserHex = "A55A" }],
        };
        await File.WriteAllTextAsync(scenarioPath, JsonSerializer.Serialize(scenario, JsonOptions));

        VirtualInventoryDataset dataset = await new VirtualReaderScenarioLoader().LoadAsync(scenarioPath);

        Assert.Equal(readerId, dataset.Scenario.ReaderId);
        Assert.Equal(2, dataset.Events.Count);
        Assert.Equal("3001", dataset.Events[0].Tag.Epc);
        Assert.Equal("3002", dataset.Events[1].Tag.Epc);
        Assert.Equal(TimeSpan.FromMilliseconds(25), dataset.Events[1].Offset);
        Assert.Equal(2, dataset.SnapshotTags.Count);
        Assert.Equal(runId, dataset.SourceRuns.Single().Id);
        Assert.Equal("A55A", dataset.MemoryByEpc["3001"].UserHex);
    }

    [Fact]
    public async Task LoadAsync_uses_snapshot_as_replay_fallback_when_jsonl_is_missing()
    {
        using var directory = new TemporaryDirectory();
        string snapshotPath = Path.Combine(directory.Path, "snapshot.json");
        Guid readerId = Guid.NewGuid();
        Guid runId = Guid.NewGuid();
        var snapshot = new InventoryRunSnapshot
        {
            Run = new InventoryRunRecord
            {
                Id = runId,
                ReaderId = readerId,
                StartedAtUtc = DateTimeOffset.UtcNow,
            },
            Tags = [Tag("3001", DateTimeOffset.UnixEpoch, -50)],
        };
        await File.WriteAllTextAsync(snapshotPath, JsonSerializer.Serialize(snapshot, JsonOptions));

        string scenarioPath = Path.Combine(directory.Path, "scenario.json");
        await File.WriteAllTextAsync(
            scenarioPath,
            JsonSerializer.Serialize(new VirtualReaderScenario
            {
                ReaderId = readerId,
                Inventory = new VirtualInventorySource { SnapshotPath = "snapshot.json" },
                Replay = new VirtualReplayOptions { FallbackIntervalMilliseconds = 25 },
            }, JsonOptions));

        VirtualInventoryDataset dataset = await new VirtualReaderScenarioLoader().LoadAsync(scenarioPath);

        var replayEvent = Assert.Single(dataset.Events);
        Assert.Equal(runId, replayEvent.SourceRunId);
        Assert.Equal("3001", replayEvent.Tag.Epc);
    }

    [Fact]
    public async Task LoadAsync_rejects_malformed_jsonl_with_file_and_line_context()
    {
        using var directory = new TemporaryDirectory();
        string logPath = Path.Combine(directory.Path, "tags.jsonl");
        await File.WriteAllLinesAsync(logPath, ["{not-json}"]);
        string scenarioPath = Path.Combine(directory.Path, "scenario.json");
        await File.WriteAllTextAsync(
            scenarioPath,
            JsonSerializer.Serialize(new VirtualReaderScenario
            {
                Inventory = new VirtualInventorySource { TagLogPath = "tags.jsonl" },
            }, JsonOptions));

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new VirtualReaderScenarioLoader().LoadAsync(scenarioPath));

        Assert.Contains("tags.jsonl:1", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_accepts_string_enum_values_from_handwritten_scenario()
    {
        using var directory = new TemporaryDirectory();
        string scenarioPath = Path.Combine(directory.Path, "scenario.json");
        await File.WriteAllTextAsync(scenarioPath, """
        {
          "schemaVersion": 1,
          "protocolVersion": "Force101",
          "replay": { "mode": "Step" }
        }
        """);

        VirtualInventoryDataset dataset = await new VirtualReaderScenarioLoader().LoadAsync(scenarioPath);

        Assert.Equal(LlrpReaderPlatform.Contracts.Readers.LlrpProtocolVersionOption.Force101, dataset.Scenario.ProtocolVersion);
        Assert.Equal(VirtualReplayMode.Step, dataset.Scenario.Replay.Mode);
    }

    private static TagObservation Tag(string epc, DateTimeOffset timestamp, sbyte rssi) => new()
    {
        Epc = epc,
        ReadCount = 1,
        FirstSeen = timestamp,
        LastSeen = timestamp,
        LastRssi = rssi,
    };

    private static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"virtual-reader-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
