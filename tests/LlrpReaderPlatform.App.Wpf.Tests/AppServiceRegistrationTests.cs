using System.IO;
using LlrpReaderPlatform.App.Wpf;
using LlrpReaderPlatform.App.Wpf.ViewModels;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Extensions.Impinj;
using LlrpReaderPlatform.Infrastructure;
using LlrpReaderPlatform.Infrastructure.Data;
using LlrpReaderPlatform.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
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
        Assert.IsType<SqliteReaderProfileStore>(provider.GetRequiredService<IReaderProfileStore>());
        Assert.IsType<SqliteReaderSettingsPresetStore>(provider.GetRequiredService<IReaderSettingsPresetStore>());
        Assert.IsType<SqliteAppSettingsStore>(provider.GetRequiredService<IAppSettingsStore>());
        Assert.IsType<SqliteTagListStore>(provider.GetRequiredService<ITagListStore>());
        Assert.IsType<SqliteInventoryRunStore>(provider.GetRequiredService<IInventoryRunStore>());
    }

    [Fact]
    public async Task AppCompositionRoot_initializes_from_sqlite_profile_store()
    {
        string databasePath = Path.Combine(
            Path.GetTempPath(),
            $"llrp-reader-platform-app-{Guid.NewGuid():N}.db");
        ServiceProvider? provider = null;

        try
        {
            ServiceCollection services = new();
            services.AddLogging();
            services.AddLlrpReaderPlatform();
            services.AddLlrpInfrastructure(databasePath);
            services.AddImpinjExtension();
            services.AddSingleton<MainViewModel>();

            provider = services.BuildServiceProvider();
            IReaderProfileStore store = provider.GetRequiredService<IReaderProfileStore>();
            var profile = new LlrpReaderPlatform.Contracts.Readers.ReaderProfile
            {
                Id = Guid.NewGuid(),
                Name = "SQLite restored",
                Host = "192.0.2.200",
                IsEnabled = false,
            };
            await store.SaveAsync(profile);

            MainViewModel viewModel = provider.GetRequiredService<MainViewModel>();
            await viewModel.InitializeAsync();

            ReaderItemViewModel restored = Assert.Single(viewModel.Readers);
            Assert.Equal(profile.Id, restored.ReaderId);
            Assert.Equal(profile.Name, restored.Name);
            Assert.False(restored.IsEnabled);
            Assert.Contains("已就绪", viewModel.Status);
        }
        finally
        {
            // Dispose the provider before removing the file so SQLite's factory
            // and migration gate cannot keep the production database open.
            // The provider is scoped to the try body and is disposed explicitly
            // here because cleanup must happen after its async disposal.
            if (provider is not null)
            {
                await provider.DisposeAsync();
            }

            SqliteConnection.ClearAllPools();

            foreach (string suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                string path = databasePath + suffix;
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }
}
