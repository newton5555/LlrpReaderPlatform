using LlrpReaderPlatform.App.Wpf;
using LlrpReaderPlatform.App.Wpf.ViewModels;
using LlrpReaderPlatform.Extensions.Impinj;
using LlrpReaderPlatform.Infrastructure;
using LlrpReaderPlatform.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LlrpReaderPlatform.App.Wpf.Tests;

/// <summary>
/// 复现并守护 App 组合根的 DI 完整性：按 ConfigureServices 相同的注册序列，
/// 解析 MainViewModel 必须成功（此前 IReaderDiscoveryService 未注册导致运行时报错）。
/// </summary>
public sealed class AppServiceRegistrationTests
{
    [Fact]
    public async Task AppCompositionRoot_resolves_MainViewModel()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddLlrpReaderPlatform();
        services.AddLlrpInfrastructure();
        services.AddImpinjExtension();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        await using ServiceProvider provider = services.BuildServiceProvider();
        MainViewModel vm = provider.GetRequiredService<MainViewModel>();

        Assert.NotNull(vm);
        Assert.NotNull(vm.Settings);
        Assert.NotNull(vm.Inventory);
        Assert.NotNull(vm.TagMemory);
        Assert.NotNull(vm.Discovered);
    }
}
