using System.IO;
using LlrpReaderPlatform.App.Wpf.ViewModels;
using LlrpReaderPlatform.Services.Persistence;
using Xunit;

namespace LlrpReaderPlatform.App.Wpf.Tests;

public sealed class AppSettingsViewModelTests
{
    [Fact]
    public async Task Load_and_save_use_the_platform_default_tag_log_directory()
    {
        var store = new InMemoryAppSettingsStore();
        using var vm = new AppSettingsViewModel(store);
        string expected = Path.Combine(
            string.IsNullOrWhiteSpace(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                ? AppContext.BaseDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LlrpReaderPlatform",
            "tag-logs");
        await store.SetAsync("tag-log-directory", "  ");

        await vm.LoadAsync();

        Assert.Equal(expected, vm.LogDirectory);

        vm.LogDirectory = "  ";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(expected, await store.GetAsync("tag-log-directory"));
        Assert.Equal(expected, vm.LogDirectory);
    }
}
