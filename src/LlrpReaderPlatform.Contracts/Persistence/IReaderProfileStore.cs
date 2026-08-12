using LlrpReaderPlatform.Contracts.Readers;

namespace LlrpReaderPlatform.Contracts.Persistence;

/// <summary>
/// Reader Profile 的持久化接口。Services 只依赖此接口，不接触具体 SQLite/EF Core 实现；
/// 由 Infrastructure 提供的实现注入。
/// </summary>
public interface IReaderProfileStore
{
    Task<IReadOnlyList<ReaderProfile>> GetAllAsync(CancellationToken ct = default);
    Task<ReaderProfile?> GetAsync(Guid readerId, CancellationToken ct = default);
    Task SaveAsync(ReaderProfile profile, CancellationToken ct = default);
    Task DeleteAsync(Guid readerId, CancellationToken ct = default);
}
