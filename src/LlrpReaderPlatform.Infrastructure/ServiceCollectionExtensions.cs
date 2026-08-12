using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Discovery;
using LlrpReaderPlatform.Infrastructure.Data;
using LlrpReaderPlatform.Infrastructure.Discovery;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace LlrpReaderPlatform.Infrastructure;

/// <summary>
/// 基础设施层 DI 组合根扩展：持久化、发现、日志等实现细节。
/// 具体注册项随实现逐步补齐。
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>注册 LlrpReaderPlatform 基础设施实现。</summary>
    public static IServiceCollection AddLlrpInfrastructure(this IServiceCollection services, string? databasePath = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IReaderDiscoveryService, ZeroconfReaderDiscoveryService>();
        string path = string.IsNullOrWhiteSpace(databasePath)
            ? Path.Combine(
                string.IsNullOrWhiteSpace(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                    ? AppContext.BaseDirectory
                    : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LlrpReaderPlatform",
                "llrp-reader-platform.db")
            : databasePath;
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        services.AddDbContextFactory<PlatformDbContext>(options => options.UseSqlite($"Data Source={path}"));
        // Services 先注册内存兜底，Infrastructure 的最后一个注册覆盖它。
        services.AddSingleton<IReaderProfileStore, SqliteReaderProfileStore>();
        services.AddSingleton<IReaderSettingsPresetStore, SqliteReaderSettingsPresetStore>();
        services.AddSingleton<IAppSettingsStore, SqliteAppSettingsStore>();
        services.AddSingleton<ITagListStore, SqliteTagListStore>();
        services.AddSingleton<IInventoryRunStore, SqliteInventoryRunStore>();
        services.AddSingleton<IInventoryTagLog, JsonLinesInventoryTagLog>();
        services.AddSingleton<IInventorySnapshotStore, JsonInventorySnapshotStore>();
        return services;
    }
}
