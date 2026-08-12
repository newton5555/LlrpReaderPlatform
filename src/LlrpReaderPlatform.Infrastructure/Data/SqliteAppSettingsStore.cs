using LlrpReaderPlatform.Contracts.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlrpReaderPlatform.Infrastructure.Data;

public sealed class SqliteAppSettingsStore(IDbContextFactory<PlatformDbContext> contextFactory) : IAppSettingsStore
{
    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await PlatformDbSchema.EnsureMigratedAsync(contextFactory, ct).ConfigureAwait(false);
        await using PlatformDbContext db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.AppSettings.AsNoTracking()
            .Where(x => x.Key == key)
            .Select(x => x.Value)
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await PlatformDbSchema.EnsureMigratedAsync(contextFactory, ct).ConfigureAwait(false);
        await using PlatformDbContext db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        AppSettingEntity? entity = await db.AppSettings.SingleOrDefaultAsync(x => x.Key == key, ct).ConfigureAwait(false);
        if (entity is null)
        {
            entity = new AppSettingEntity { Key = key };
            db.AppSettings.Add(entity);
        }

        entity.Value = value ?? string.Empty;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
