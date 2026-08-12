using LlrpReaderPlatform.Services.Extensions;
using Microsoft.Extensions.DependencyInjection;

namespace LlrpReaderPlatform.Extensions.Impinj;

/// <summary>
/// Impinj 扩展模块 DI 组合根扩展。宿主显式调用以启用 Impinj 能力。
/// 服务层通过 <see cref="IReaderExtensionModule"/> 集合收集并在两阶段匹配后应用。
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>注册 Impinj R420 扩展模块。</summary>
    public static IServiceCollection AddImpinjExtension(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IReaderExtensionModule, ImpinjReaderExtensionModule>();
        return services;
    }
}
