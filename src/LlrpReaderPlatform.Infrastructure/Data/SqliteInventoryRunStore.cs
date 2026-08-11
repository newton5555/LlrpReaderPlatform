using LlrpReaderPlatform.Contracts.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlrpReaderPlatform.Infrastructure.Data;

public sealed class SqliteInventoryRunStore(IDbContextFactory<PlatformDbContext> contextFactory) : IInventoryRunStore
{
    public async Task<IReadOnlyList<InventoryRunRecord>> GetForReaderAsync(Guid readerId, CancellationToken ct = default)
    {
        await PlatformDbSchema.EnsureMigratedAsync(contextFactory, ct).ConfigureAwait(false);
        await using PlatformDbContext db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        InventoryRunRecord[] runs = await db.InventoryRuns.AsNoTracking().Where(x => x.ReaderId == readerId)
            .Select(x => new InventoryRunRecord
            {
                Id = x.Id,
                ReaderId = x.ReaderId,
                StartedAtUtc = x.StartedAtUtc,
                EndedAtUtc = x.EndedAtUtc,
                StopReason = x.StopReason,
                TotalReadCount = x.TotalReadCount,
                UniqueTagCount = x.UniqueTagCount,
                LogFilePath = x.LogFilePath,
            }).ToArrayAsync(ct).ConfigureAwait(false);
        return runs.OrderByDescending(x => x.StartedAtUtc).ToArray();
    }

    public async Task SaveAsync(InventoryRunRecord run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await PlatformDbSchema.EnsureMigratedAsync(contextFactory, ct).ConfigureAwait(false);
        await using PlatformDbContext db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        InventoryRunEntity? entity = await db.InventoryRuns.SingleOrDefaultAsync(x => x.Id == run.Id, ct).ConfigureAwait(false);
        if (entity is null)
        {
            entity = new InventoryRunEntity { Id = run.Id };
            db.InventoryRuns.Add(entity);
        }

        entity.ReaderId = run.ReaderId;
        entity.StartedAtUtc = run.StartedAtUtc;
        entity.EndedAtUtc = run.EndedAtUtc;
        entity.StopReason = run.StopReason;
        entity.TotalReadCount = run.TotalReadCount;
        entity.UniqueTagCount = run.UniqueTagCount;
        entity.LogFilePath = run.LogFilePath;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
