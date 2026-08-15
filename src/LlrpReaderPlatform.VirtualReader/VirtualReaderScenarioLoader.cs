using System.Text.Json;
using System.Text.Json.Serialization;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Tagging;

namespace LlrpReaderPlatform.VirtualReader;

/// <summary>
/// 读取虚拟 Reader 场景和现有平台盘存文件。
/// JSONL 的 envelope 与平台 Infrastructure 的输出保持兼容。
/// </summary>
public sealed class VirtualReaderScenarioLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    static VirtualReaderScenarioLoader()
    {
        SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    public async Task<VirtualInventoryDataset> LoadAsync(
        string scenarioPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenarioPath);
        string fullScenarioPath = Path.GetFullPath(scenarioPath);
        await using FileStream stream = File.OpenRead(fullScenarioPath);
        VirtualReaderScenario scenario = await JsonSerializer.DeserializeAsync<VirtualReaderScenario>(
            stream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Virtual Reader scenario is empty: {scenarioPath}");

        if (scenario.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported Virtual Reader scenario schema: {scenario.SchemaVersion}.");
        }

        string baseDirectory = Path.GetDirectoryName(fullScenarioPath) ?? AppContext.BaseDirectory;
        IReadOnlyList<SnapshotDocument> snapshots = await LoadSnapshotsAsync(
            ResolveSource(baseDirectory, scenario.Inventory.SnapshotPath, ".json"),
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<LogDocument> logs = await LoadLogsAsync(
            ResolveSource(baseDirectory, scenario.Inventory.TagLogPath, ".jsonl"),
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<VirtualReplayEvent> events = logs.Count > 0
            ? BuildReplayEvents(logs, scenario.Replay)
            : BuildSnapshotReplayEvents(snapshots, scenario.Replay);
        IReadOnlyList<TagObservation> snapshotTags = snapshots
            .SelectMany(static document => document.Snapshot.Tags)
            .ToArray();
        IReadOnlyList<InventoryRunRecord> sourceRuns = snapshots
            .Select(static document => document.Snapshot.Run)
            .ToArray();

        Dictionary<string, VirtualTagMemorySeed> memory = scenario.TagMemory
            .Where(static seed => !string.IsNullOrWhiteSpace(seed.Epc))
            .GroupBy(static seed => seed.Epc, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.OrdinalIgnoreCase);

        return new VirtualInventoryDataset
        {
            Scenario = scenario,
            Events = events,
            SnapshotTags = snapshotTags,
            SourceRuns = sourceRuns,
            MemoryByEpc = memory,
        };
    }

    private static async Task<IReadOnlyList<SnapshotDocument>> LoadSnapshotsAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var documents = new List<SnapshotDocument>();
        foreach (string path in paths)
        {
            await using FileStream stream = File.OpenRead(path);
            InventoryRunSnapshot snapshot = await JsonSerializer.DeserializeAsync<InventoryRunSnapshot>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Inventory snapshot is empty: {path}");
            documents.Add(new SnapshotDocument(path, snapshot));
        }

        return documents;
    }

    private static async Task<IReadOnlyList<LogDocument>> LoadLogsAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var documents = new List<LogDocument>();
        foreach (string path in paths)
        {
            using var stream = new StreamReader(File.OpenRead(path));
            int lineNumber = 0;
            while (await stream.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                VirtualTagLogEnvelope envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<VirtualTagLogEnvelope>(line, SerializerOptions)
                        ?? throw new InvalidDataException("JSONL line is empty.");
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException($"Invalid inventory JSONL at {path}:{lineNumber}.", exception);
                }

                if (envelope.Tag is null || string.IsNullOrWhiteSpace(envelope.Tag.Epc))
                {
                    throw new InvalidDataException($"Inventory JSONL has no tag EPC at {path}:{lineNumber}.");
                }

                documents.Add(new LogDocument(path, lineNumber, envelope));
            }
        }

        return documents;
    }

    private static IReadOnlyList<VirtualReplayEvent> BuildReplayEvents(
        IReadOnlyList<LogDocument> documents,
        VirtualReplayOptions options)
    {
        if (documents.Count == 0)
        {
            return [];
        }

        int fallbackMilliseconds = Math.Max(0, options.FallbackIntervalMilliseconds);
        var events = new List<VirtualReplayEvent>(documents.Count);
        DateTimeOffset? firstTimestamp = null;
        TimeSpan previousOffset = TimeSpan.Zero;

        for (int index = 0; index < documents.Count; index++)
        {
            VirtualTagLogEnvelope envelope = documents[index].Envelope;
            TagObservation tag = envelope.Tag!;
            TimeSpan offset = ResolveOffset(
                tag.FirstSeen,
                ref firstTimestamp,
                previousOffset,
                fallbackMilliseconds,
                index);
            previousOffset = offset;
            events.Add(new VirtualReplayEvent
            {
                Sequence = index,
                SourceRunId = envelope.Id == Guid.Empty ? null : envelope.Id,
                SourceReaderId = envelope.ReaderId == Guid.Empty ? null : envelope.ReaderId,
                Offset = offset,
                Tag = tag,
            });
        }

        return events;
    }

    private static IReadOnlyList<VirtualReplayEvent> BuildSnapshotReplayEvents(
        IReadOnlyList<SnapshotDocument> documents,
        VirtualReplayOptions options)
    {
        var events = new List<VirtualReplayEvent>();
        int sequence = 0;
        foreach (SnapshotDocument document in documents)
        {
            foreach (TagObservation tag in document.Snapshot.Tags)
            {
                events.Add(new VirtualReplayEvent
                {
                    Sequence = sequence,
                    SourceRunId = document.Snapshot.Run.Id,
                    SourceReaderId = document.Snapshot.Run.ReaderId,
                    Offset = TimeSpan.FromMilliseconds(
                        (long)sequence * Math.Max(0, options.FallbackIntervalMilliseconds)),
                    Tag = tag,
                });
                sequence++;
            }
        }

        return events;
    }

    private static TimeSpan ResolveOffset(
        DateTimeOffset timestamp,
        ref DateTimeOffset? firstTimestamp,
        TimeSpan previousOffset,
        int fallbackMilliseconds,
        int sequence)
    {
        bool usable = timestamp != default && timestamp > DateTimeOffset.MinValue;
        if (usable && firstTimestamp is null)
        {
            firstTimestamp = timestamp;
        }

        if (usable && firstTimestamp is DateTimeOffset first)
        {
            TimeSpan offset = timestamp - first;
            if (offset >= previousOffset)
            {
                return offset;
            }
        }

        return previousOffset + TimeSpan.FromMilliseconds(fallbackMilliseconds);
    }

    private static IReadOnlyList<string> ResolveSource(
        string baseDirectory,
        string? source,
        string expectedExtension)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return [];
        }

        string path = Path.GetFullPath(source, baseDirectory);
        if (File.Exists(path))
        {
            return [path];
        }

        if (!Directory.Exists(path))
        {
            throw new FileNotFoundException($"Virtual Reader data source was not found: {path}", path);
        }

        string extension = Path.GetExtension(path).ToLowerInvariant();
        string pattern = extension switch
        {
            ".jsonl" => "*.jsonl",
            ".json" => "*.json",
            _ => $"*{expectedExtension}",
        };
        return Directory.EnumerateFiles(path, pattern, SearchOption.AllDirectories)
            .Where(file => string.Equals(
                Path.GetExtension(file),
                expectedExtension,
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(static file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private sealed record VirtualTagLogEnvelope
    {
        public Guid Id { get; init; }
        public Guid ReaderId { get; init; }
        public TagObservation? Tag { get; init; }
    }

    private sealed record SnapshotDocument(string Path, InventoryRunSnapshot Snapshot);

    private sealed record LogDocument(string Path, int LineNumber, VirtualTagLogEnvelope Envelope);
}
