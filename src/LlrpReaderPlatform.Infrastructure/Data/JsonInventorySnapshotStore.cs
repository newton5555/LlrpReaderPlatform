using System.Text.Json;
using System.Text.Json.Serialization;
using LlrpReaderPlatform.Contracts.Persistence;

namespace LlrpReaderPlatform.Infrastructure.Data;

/// <summary>
/// 将盘存停止后的最终聚合结果写入独立 JSON 文件。
/// 不订阅 TagObserved，也不在盘存过程中写文件。
/// </summary>
public sealed class JsonInventorySnapshotStore : IInventorySnapshotStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string rootDirectory;
    private readonly IInventoryLoggingPolicy loggingPolicy;

    public JsonInventorySnapshotStore(string? rootDirectory = null)
        : this(
            new LlrpReaderPlatform.Services.Persistence.DefaultInventoryLoggingPolicy(),
            rootDirectory)
    {
    }

    public JsonInventorySnapshotStore(
        IInventoryLoggingPolicy loggingPolicy,
        string? rootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(loggingPolicy);
        this.loggingPolicy = loggingPolicy;
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        this.rootDirectory = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(
                string.IsNullOrWhiteSpace(localAppData) ? AppContext.BaseDirectory : localAppData,
                "LlrpReaderPlatform",
                "inventory-snapshots")
            : rootDirectory;
    }

    public async Task<string?> SaveAsync(InventoryRunSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ct.ThrowIfCancellationRequested();
        if (await loggingPolicy.GetModeAsync(ct).ConfigureAwait(false) == InventoryLoggingMode.Off)
        {
            return null;
        }

        string directory = Path.Combine(rootDirectory, snapshot.Run.ReaderId.ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{snapshot.Run.Id:N}.json");
        string temporaryPath = path + ".tmp";
        string json = JsonSerializer.Serialize(snapshot, SerializerOptions);

        await File.WriteAllTextAsync(temporaryPath, json, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        File.Move(temporaryPath, path, overwrite: true);
        return path;
    }
}
