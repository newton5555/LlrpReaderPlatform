using System.Collections.Concurrent;
using LlrpReaderPlatform.Contracts.Persistence;

namespace LlrpReaderPlatform.Services.Persistence;

public sealed class InMemoryInventoryRunStore : IInventoryRunStore
{
    private readonly ConcurrentDictionary<Guid, InventoryRunRecord> store = new();

    public Task<IReadOnlyList<InventoryRunRecord>> GetForReaderAsync(Guid readerId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<InventoryRunRecord>>(store.Values
            .Where(x => x.ReaderId == readerId)
            .OrderByDescending(x => x.StartedAtUtc)
            .ToArray());

    public Task SaveAsync(InventoryRunRecord run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        store[run.Id] = run;
        return Task.CompletedTask;
    }
}
