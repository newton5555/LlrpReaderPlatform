using LlrpReaderPlatform.Services.Extensions;
using LlrpReaderPlatform.Services.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace LlrpReaderPlatform.VirtualReader;

/// <summary>
/// 开发/测试组合根的 Virtual Reader 注册。调用方负责加载场景并注册到 catalog。
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddVirtualReader(this IServiceCollection services)
        => AddVirtualReader(services, new VirtualReaderCatalog());

    public static IServiceCollection AddVirtualReader(
        this IServiceCollection services,
        VirtualReaderCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(catalog);
        services.AddSingleton(catalog);
        services.AddSingleton<IReaderSessionFactory, VirtualReaderSessionFactory>();
        services.AddSingleton<IReaderExtensionModule, VirtualReaderExtensionModule>();
        return services;
    }
}
