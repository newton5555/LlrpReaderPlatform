using System.Collections.Concurrent;
using LlrpReaderPlatform.Contracts.Persistence;

namespace LlrpReaderPlatform.Services.Persistence;

public sealed class InMemoryTagListStore : ITagListStore
{
    private readonly ConcurrentDictionary<Guid, TagListDefinition> store = new();

    public Task<IReadOnlyList<TagListDefinition>> GetAllAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TagListDefinition>>(store.Values.OrderBy(x => x.Name).ToArray());

    public Task<TagListDefinition?> GetAsync(Guid tagListId, CancellationToken ct = default)
    {
        store.TryGetValue(tagListId, out TagListDefinition? value);
        return Task.FromResult(value);
    }

    public Task SaveAsync(TagListDefinition tagList, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tagList);
        store[tagList.Id] = tagList;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid tagListId, CancellationToken ct = default)
    {
        store.TryRemove(tagListId, out _);
        return Task.CompletedTask;
    }
}
