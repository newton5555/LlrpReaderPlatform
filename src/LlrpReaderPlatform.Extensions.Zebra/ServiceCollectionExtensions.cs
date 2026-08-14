using LlrpReaderPlatform.Services.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace LlrpReaderPlatform.Extensions.Zebra;

/// <summary>
/// Zebra 扩展模块 DI 组合根扩展。宿主显式调用以启用 Zebra 实验性能力。
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddZebraExtension(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IReaderExtensionModule, ZebraReaderExtensionModule>();
        return services;
    }
}
