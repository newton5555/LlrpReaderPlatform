using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Readers;
using Microsoft.EntityFrameworkCore;

namespace LlrpReaderPlatform.Infrastructure.Data;

/// <summary>EF Core SQLite Reader Profile 存储。每个操作使用独立 DbContext，支持长期服务进程。</summary>
public sealed class SqliteReaderProfileStore(IDbContextFactory<PlatformDbContext> contextFactory) : IReaderProfileStore
{
    public async Task<IReadOnlyList<ReaderProfile>> GetAllAsync(CancellationToken ct = default)
    {
        await PlatformDbSchema.EnsureMigratedAsync(contextFactory, ct).ConfigureAwait(false);
        await using PlatformDbContext db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        ReaderProfileEntity[] entities = await db.ReaderProfiles
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .ToArrayAsync(ct)
            .ConfigureAwait(false);
        return entities.Select(ToProfile).ToArray();
    }

    public async Task<ReaderProfile?> GetAsync(Guid readerId, CancellationToken ct = default)
    {
        await PlatformDbSchema.EnsureMigratedAsync(contextFactory, ct).ConfigureAwait(false);
        await using PlatformDbContext db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        ReaderProfileEntity? entity = await db.ReaderProfiles.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == readerId, ct)
            .ConfigureAwait(false);
        return entity is null ? null : ToProfile(entity);
    }

    public async Task SaveAsync(ReaderProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();

        await PlatformDbSchema.EnsureMigratedAsync(contextFactory, ct).ConfigureAwait(false);
        await using PlatformDbContext db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        ReaderProfileEntity? entity = await db.ReaderProfiles
            .SingleOrDefaultAsync(x => x.Id == profile.Id, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            entity = new ReaderProfileEntity { Id = profile.Id };
            db.ReaderProfiles.Add(entity);
        }

        entity.Name = profile.Name;
        entity.Host = ReaderEndpoint.NormalizeHost(profile.Host);
        entity.Port = profile.Port;
        entity.LlrpVersion = profile.LlrpVersion;
        entity.IsEnabled = profile.IsEnabled;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid readerId, CancellationToken ct = default)
    {
        await PlatformDbSchema.EnsureMigratedAsync(contextFactory, ct).ConfigureAwait(false);
        await using PlatformDbContext db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.ReaderProfiles.Where(x => x.Id == readerId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    private static ReaderProfile ToProfile(ReaderProfileEntity entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        Host = ReaderEndpoint.NormalizeHost(entity.Host),
        Port = entity.Port,
        LlrpVersion = entity.LlrpVersion,
        IsEnabled = entity.IsEnabled,
    };
}
