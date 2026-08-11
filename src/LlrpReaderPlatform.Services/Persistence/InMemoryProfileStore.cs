using System.Collections.Concurrent;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Readers;

namespace LlrpReaderPlatform.Services.Persistence;

/// <summary>
/// 内存版 ReaderProfile 持久化实现。作为无 Infrastructure 时的默认兜底；
/// 生产环境由 Infrastructure 的 <c>IReaderProfileStore</c>（SQLite/EF Core）覆盖注册。
/// </summary>
public sealed class InMemoryProfileStore : IReaderProfileStore
{
    private readonly ConcurrentDictionary<Guid, ReaderProfile> store = new();

    public Task<IReadOnlyList<ReaderProfile>> GetAllAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<ReaderProfile>>(store.Values.ToArray());
    }

    public Task<ReaderProfile?> GetAsync(Guid readerId, CancellationToken ct = default)
    {
        store.TryGetValue(readerId, out ReaderProfile? profile);
        return Task.FromResult(profile);
    }

    public Task SaveAsync(ReaderProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        store[profile.Id] = profile with { Host = ReaderEndpoint.NormalizeHost(profile.Host) };
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid readerId, CancellationToken ct = default)
    {
        store.TryRemove(readerId, out _);
        return Task.CompletedTask;
    }
}
