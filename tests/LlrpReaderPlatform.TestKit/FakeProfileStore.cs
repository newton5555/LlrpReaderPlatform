using System.Collections.Concurrent;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Readers;

namespace LlrpReaderPlatform.TestKit;

/// <summary>
/// 可控的内存版 ProfileStore：测试可注入 Save/Delete 抛异常以验证补偿回滚路径。
/// </summary>
public sealed class FakeProfileStore : IReaderProfileStore
{
    private readonly ConcurrentDictionary<Guid, ReaderProfile> store = new();

    /// <summary>SaveAsync 若不为 null 则抛出（模拟持久化失败）。</summary>
    public Exception? SaveThrows { get; set; }

    /// <summary>DeleteAsync 若不为 null 则抛出（模拟删除失败）。</summary>
    public Exception? DeleteThrows { get; set; }

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
        if (SaveThrows is not null)
        {
            throw SaveThrows;
        }

        store[profile.Id] = profile;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid readerId, CancellationToken ct = default)
    {
        if (DeleteThrows is not null)
        {
            throw DeleteThrows;
        }

        store.TryRemove(readerId, out _);
        return Task.CompletedTask;
    }
}
