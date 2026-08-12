using System.Collections.Concurrent;
using LlrpReaderPlatform.Contracts.Persistence;

namespace LlrpReaderPlatform.Services.Persistence;

public sealed class InMemoryAppSettingsStore : IAppSettingsStore
{
    private readonly ConcurrentDictionary<string, string> store = new(StringComparer.Ordinal);

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        store.TryGetValue(key, out string? value);
        return Task.FromResult(value);
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        store[key] = value ?? string.Empty;
        return Task.CompletedTask;
    }
}
