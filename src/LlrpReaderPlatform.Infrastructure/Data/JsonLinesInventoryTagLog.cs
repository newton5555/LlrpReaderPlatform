using System.Text.Json;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Tagging;

namespace LlrpReaderPlatform.Infrastructure.Data;

/// <summary>按 Inventory Run 写入 JSONL 的可选标签日志实现，便于后续导入/回放。</summary>
public sealed class JsonLinesInventoryTagLog : IInventoryTagLog
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly Dictionary<Guid, string> paths = [];
    private readonly IAppSettingsStore appSettings;
    private readonly IInventoryLoggingPolicy loggingPolicy;
    private readonly string? fallbackRoot;

    public JsonLinesInventoryTagLog(
        IAppSettingsStore appSettings,
        IInventoryLoggingPolicy? loggingPolicy = null,
        string? fallbackRoot = null)
    {
        ArgumentNullException.ThrowIfNull(appSettings);
        this.appSettings = appSettings;
        this.loggingPolicy = loggingPolicy
            ?? new AppSettingsInventoryLoggingPolicy(appSettings);
        this.fallbackRoot = fallbackRoot;
    }

    public JsonLinesInventoryTagLog(IAppSettingsStore appSettings, string fallbackRoot)
        : this(appSettings, loggingPolicy: null, fallbackRoot)
    {
    }

    public async Task<string?> StartAsync(InventoryRunRecord run, CancellationToken ct = default)
    {
        if (await loggingPolicy.GetModeAsync(ct).ConfigureAwait(false)
            != InventoryLoggingMode.RawReports)
        {
            return null;
        }

        string? configuredRoot = await appSettings
            .GetAsync(InventoryLoggingSettings.RawDirectoryKey, ct)
            .ConfigureAwait(false);
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string defaultRoot = string.IsNullOrWhiteSpace(fallbackRoot)
            ? Path.Combine(
                string.IsNullOrWhiteSpace(localAppData) ? AppContext.BaseDirectory : localAppData,
                "LlrpReaderPlatform",
                "tag-logs")
            : fallbackRoot;
        string root = string.IsNullOrWhiteSpace(configuredRoot) ? defaultRoot : configuredRoot.Trim();
        string directory = Path.Combine(root, run.ReaderId.ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{run.Id:N}.jsonl");
        await File.AppendAllTextAsync(path, string.Empty, ct).ConfigureAwait(false);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            paths[run.Id] = path;
        }
        finally
        {
            gate.Release();
        }

        return path;
    }

    public async Task AppendAsync(InventoryRunRecord run, TagObservation tag, CancellationToken ct = default)
    {
        string? path = await GetPathAsync(run.Id, ct).ConfigureAwait(false);
        if (path is null)
        {
            return;
        }

        string line = JsonSerializer.Serialize(new
        {
            run.Id,
            run.ReaderId,
            tag,
        }) + Environment.NewLine;
        await writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(path, line, ct).ConfigureAwait(false);
        }
        finally
        {
            writeGate.Release();
        }
    }

    public async Task CompleteAsync(InventoryRunRecord run, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            paths.Remove(run.Id);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string?> GetPathAsync(Guid runId, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return paths.TryGetValue(runId, out string? path) ? path : null;
        }
        finally
        {
            gate.Release();
        }
    }
}
