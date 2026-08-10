using LlrpReaderPlatform.Contracts.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlrpReaderPlatform.Infrastructure.Data;

public sealed class SqliteReaderSettingsPresetStore(IDbContextFactory<PlatformDbContext> contextFactory)
    : IReaderSettingsPresetStore
{
    public async Task<ReaderSettingsPreset?> GetAsync(Guid readerId, CancellationToken ct = default)
    {
        await using PlatformDbContext db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        ReaderSettingsPresetEntity? entity = await db.ReaderSettingsPresets.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ReaderId == readerId, ct)
            .ConfigureAwait(false);
        return entity is null ? null : ToPreset(entity);
    }

    public async Task SaveAsync(ReaderSettingsPreset preset, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preset);
        await using PlatformDbContext db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        ReaderSettingsPresetEntity? entity = await db.ReaderSettingsPresets
            .SingleOrDefaultAsync(x => x.ReaderId == preset.ReaderId, ct)
            .ConfigureAwait(false);
        if (entity is null)
        {
            entity = new ReaderSettingsPresetEntity { ReaderId = preset.ReaderId };
            db.ReaderSettingsPresets.Add(entity);
        }

        entity.SchemaVersion = preset.SchemaVersion;
        entity.SettingsJson = preset.SettingsJson;
        entity.UpdatedAtUtc = preset.UpdatedAtUtc;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid readerId, CancellationToken ct = default)
    {
        await using PlatformDbContext db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        await db.ReaderSettingsPresets.Where(x => x.ReaderId == readerId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    private static ReaderSettingsPreset ToPreset(ReaderSettingsPresetEntity entity) => new()
    {
        ReaderId = entity.ReaderId,
        SchemaVersion = entity.SchemaVersion,
        SettingsJson = entity.SettingsJson,
        UpdatedAtUtc = entity.UpdatedAtUtc,
    };
}
