using System.Collections.Concurrent;
using LlrpReaderPlatform.Contracts.Persistence;

namespace LlrpReaderPlatform.Services.Persistence;

public sealed class InMemorySettingsPresetStore : IReaderSettingsPresetStore
{
    private readonly ConcurrentDictionary<Guid, ReaderSettingsPreset> store = new();

    public Task<ReaderSettingsPreset?> GetAsync(Guid readerId, CancellationToken ct = default)
    {
        store.TryGetValue(readerId, out ReaderSettingsPreset? preset);
        return Task.FromResult(preset);
    }

    public Task SaveAsync(ReaderSettingsPreset preset, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preset);
        store[preset.ReaderId] = preset;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid readerId, CancellationToken ct = default)
    {
        store.TryRemove(readerId, out _);
        return Task.CompletedTask;
    }
}
