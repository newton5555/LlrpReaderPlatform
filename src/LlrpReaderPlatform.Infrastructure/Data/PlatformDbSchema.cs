using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;

namespace LlrpReaderPlatform.Infrastructure.Data;

/// <summary>
/// 串行化同一个 DbContextFactory 的 SQLite schema 初始化。
/// 多个 Store 可能在应用启动或页面首次加载时同时访问数据库；迁移本身是数据库级操作，
/// 不能让每个 Store 无保护地并发执行。迁移完成后，各 Store 仍使用独立 DbContext。
/// </summary>
internal static class PlatformDbSchema
{
    private static readonly ConditionalWeakTable<
        IDbContextFactory<PlatformDbContext>,
        SemaphoreSlim> MigrationGates = new();

    public static async Task EnsureMigratedAsync(
        IDbContextFactory<PlatformDbContext> contextFactory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        SemaphoreSlim gate = MigrationGates.GetValue(
            contextFactory,
            static _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using PlatformDbContext db = await contextFactory
                .CreateDbContextAsync(ct)
                .ConfigureAwait(false);
            await db.Database.MigrateAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
