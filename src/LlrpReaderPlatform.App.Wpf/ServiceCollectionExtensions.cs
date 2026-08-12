using LlrpReaderPlatform.App.Wpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LlrpReaderPlatform.App.Wpf;

/// <summary>
/// WPF 消费者自己的组合根注册。共享 Services/Infrastructure 只提供平台服务，
/// 页面 ViewModel 由消费者组合根创建并注入 Shell，不在 MainViewModel 内部直接 new。
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLlrpReaderPlatformWpf(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<InventoryViewModel>();
        services.AddSingleton<TagMemoryViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();
        services.AddSingleton<ReaderSettingsViewModel>();
        services.AddSingleton<AboutViewModel>();
        services.AddSingleton<AppSettingsViewModel>();
        services.AddSingleton<TagListsViewModel>();
        services.AddSingleton<InventoryRunsViewModel>();
        services.AddSingleton<AddDataSourceViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services;
    }
}
