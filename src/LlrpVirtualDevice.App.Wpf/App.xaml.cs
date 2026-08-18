using System.IO;
using System.Windows;
using LlrpVirtualDevice.App.Wpf.Services;
using LlrpVirtualDevice.App.Wpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace LlrpVirtualDevice.App.Wpf;

public partial class App : Application
{
    private ServiceProvider? _services;

    public IServiceProvider Services => _services
        ?? throw new InvalidOperationException("Services not initialized.");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);
        _services = serviceCollection.BuildServiceProvider();

        var mainVm = _services.GetRequiredService<MainViewModel>();
        await mainVm.InitializeAsync();

        var window = _services.GetRequiredService<MainWindow>();
        window.DataContext = mainVm;
        window.Show();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        string logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LlrpVirtualDeviceStudio",
            "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logDir, "virtual-device-.log"),
                rollingInterval: RollingInterval.Day,
                outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(Log.Logger, dispose: true);
        });

        services.AddSingleton<IDialogService, CustomDialogService>();
        services.AddSingleton<IVirtualDeviceManagerService, VirtualDeviceManagerService>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_services != null)
        {
            var manager = _services.GetService<IVirtualDeviceManagerService>();
            if (manager is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
            await _services.DisposeAsync();
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
