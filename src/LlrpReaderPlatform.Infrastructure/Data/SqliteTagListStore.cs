using LlrpReaderPlatform.Contracts.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LlrpReaderPlatform.Infrastructure.Data;

public sealed class SqliteTagListStore(IDbContextFactory<PlatformDbContext> contextFactory) : ITagListStore
{
    public async Task<IReadOnlyList<TagListDefinition>> GetAllAsync(CancellationToken ct = default)
    {
        await using PlatformDbContext db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        List<TagListEntity> entities = await db.TagLists.AsNoTracking().Include(x => x.Entries)
            .OrderBy(x => x.Name).ToListAsync(ct).ConfigureAwait(false);
        return entities.Select(ToDefinition).ToArray();
    }

    public async Task<TagListDefinition?> GetAsync(Guid tagListId, CancellationToken ct = default)
    {
        await using PlatformDbContext db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        TagListEntity? entity = await db.TagLists.Include(x => x.Entries)
            .AsNoTracking().SingleOrDefaultAsync(x => x.Id == tagListId, ct).ConfigureAwait(false);
        return entity is null ? null : ToDefinition(entity);
    }

    public async Task SaveAsync(TagListDefinition tagList, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tagList);
        await using PlatformDbContext db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        TagListEntity? entity = await db.TagLists.Include(x => x.Entries)
            .SingleOrDefaultAsync(x => x.Id == tagList.Id, ct).ConfigureAwait(false);
        if (entity is null)
        {
            entity = new TagListEntity { Id = tagList.Id };
            db.TagLists.Add(entity);
        }
        else
        {
            db.TagListEntries.RemoveRange(entity.Entries);
        }

        entity.Name = tagList.Name;
        entity.IsEnabled = tagList.IsEnabled;
        entity.ColorHex = tagList.ColorHex;
        entity.Entries = tagList.Entries.Select(x => new TagListEntryEntity
        {
            Id = x.Id,
            TagListId = tagList.Id,
            EpcHex = x.EpcHex,
            DisplayName = x.DisplayName,
            ColorHex = x.ColorHex,
        }).ToList();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid tagListId, CancellationToken ct = default)
    {
        await using PlatformDbContext db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        await db.TagLists.Where(x => x.Id == tagListId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }

    private static TagListDefinition ToDefinition(TagListEntity entity) => new()
    {
        Id = entity.Id,
        Name = entity.Name,
        IsEnabled = entity.IsEnabled,
        ColorHex = entity.ColorHex,
        Entries = entity.Entries.Select(x => new TagListEntry
        {
            Id = x.Id,
            TagListId = x.TagListId,
            EpcHex = x.EpcHex,
            DisplayName = x.DisplayName,
            ColorHex = x.ColorHex,
        }).ToArray(),
    };

}
