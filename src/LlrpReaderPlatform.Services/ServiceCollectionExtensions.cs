using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Contracts.Tagging;
using LlrpReaderPlatform.Services.Lifecycle;
using LlrpReaderPlatform.Services.Persistence;
using LlrpReaderPlatform.Services.Sdk;
using LlrpReaderPlatform.Services.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace LlrpReaderPlatform.Services;

/// <summary>
/// 应用服务层的 DI 组合根扩展。UI 消费者在各自组合根调用，以注册共享服务。
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>注册 LlrpReaderPlatform 服务层共享服务（生命周期、能力、设置、盘存等）。</summary>
    public static IServiceCollection AddLlrpReaderPlatform(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IReaderSessionFactory, LlrpReaderSessionFactory>();
        // 默认内存 store 兜底；Infrastructure 可 TryAdd 覆盖为 SQLite。
        services.AddSingleton<IReaderProfileStore, InMemoryProfileStore>();
        services.AddSingleton<IReaderSettingsPresetStore, InMemorySettingsPresetStore>();
        services.AddSingleton<IAppSettingsStore, InMemoryAppSettingsStore>();
        services.AddSingleton<ITagListStore, InMemoryTagListStore>();
        services.AddSingleton<IInventoryRunStore, InMemoryInventoryRunStore>();
        services.AddSingleton<IInventoryTagLog, NullInventoryTagLog>();

        // ReaderManager 以单一实例同时提供生命周期与盘存服务。
        services.AddSingleton<ReaderManager>();
        services.AddSingleton<IReaderManager>(sp => sp.GetRequiredService<ReaderManager>());
        services.AddSingleton<IInventoryService>(sp => sp.GetRequiredService<ReaderManager>());
        services.AddSingleton<IReaderSettingsRuntime>(sp => sp.GetRequiredService<ReaderManager>());

        // F3：能力驱动设置。
        services.AddSingleton<ISettingsCompiler, StandardSettingsCompiler>();
        services.AddSingleton<IReaderSettingsService, SettingsService>();

        return services;
    }
}
