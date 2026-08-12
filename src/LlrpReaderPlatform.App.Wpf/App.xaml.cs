using System.IO;
using System.Windows;
using System.Windows.Threading;
using LlrpReaderPlatform.App.Wpf.ViewModels;
using LlrpReaderPlatform.Extensions.Impinj;
using LlrpReaderPlatform.Infrastructure;
using LlrpReaderPlatform.Services;
using LlrpReaderPlatform.Services.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace LlrpReaderPlatform.App.Wpf;

public partial class App : Application
{
    private ServiceProvider? services;
    private ILogger<App>? logger;

    private const string LogOutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    private const long LogFileSizeLimitBytes = 50L * 1024 * 1024;

    private static LogEventLevel DefaultLogLevel =>
#if DEBUG
        LogEventLevel.Debug;
#else
        LogEventLevel.Information;
#endif

    public IServiceProvider Services => services
        ?? throw new InvalidOperationException("Application services have not been initialized.");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ServiceCollection collection = new();
        ConfigureServices(collection);
        services = collection.BuildServiceProvider();
        logger = services.GetRequiredService<ILogger<App>>();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        MainWindow window = services.GetRequiredService<MainWindow>();
        window.Show();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        string logDirectory = Path.Combine(
            string.IsNullOrWhiteSpace(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
                ? AppContext.BaseDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LlrpReaderPlatform",
            "logs");
        Directory.CreateDirectory(logDirectory);

        Serilog.ILogger applicationLogger = CreateApplicationLogger(logDirectory);
        services.AddLogging(builder =>
        {
            // Serilog is the sole logging provider. Its category-level overrides below are
            // also the effective source of truth for the rolling files, so no event can
            // bypass the configured SDK and EF Core thresholds after provider filtering.
            builder.ClearProviders();
            builder.AddSerilog(applicationLogger, dispose: true);
        });

        // 组合根：显式注册共享服务层、基础设施与已启用的厂商扩展。
        services.AddLlrpReaderPlatform();
        services.AddLlrpInfrastructure();
        services.AddImpinjExtension();

        // The reader stack receives the same application logger factory as every other
        // component. Serilog then routes SDK categories to sdk-*.log.
        services.AddSingleton<IReaderSessionFactory>(provider =>
            new LlrpReaderSessionFactory(provider.GetRequiredService<ILoggerFactory>()));

        services.AddLlrpReaderPlatformWpf();
    }

    private static Serilog.ILogger CreateApplicationLogger(string logDirectory) =>
        new LoggerConfiguration()
            .MinimumLevel.Is(DefaultLogLevel)
            // These category overrides must live in Serilog rather than only in the
            // Microsoft.Extensions.Logging builder: Serilog is the file writer and the
            // provider consults this logger when deciding whether Debug is enabled.
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("LlrpSdk", LogEventLevel.Information)
            .MinimumLevel.Override("LlrpNet", LogEventLevel.Information)
            .MinimumLevel.Override("LlrpReaderPlatform.Services.Sdk", LogEventLevel.Information)
            .WriteTo.Async(configuration => configuration.Logger(logger => logger
                .Filter.ByIncludingOnly(static logEvent => IsUiLogEvent(logEvent))
                .WriteTo.File(
                    Path.Combine(logDirectory, "ui-.log"),
                    outputTemplate: LogOutputTemplate,
                    fileSizeLimitBytes: LogFileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14)))
            .WriteTo.Async(configuration => configuration.Logger(logger => logger
                .Filter.ByIncludingOnly(static logEvent => IsSdkLogEvent(logEvent))
                .WriteTo.File(
                    Path.Combine(logDirectory, "sdk-.log"),
                    outputTemplate: LogOutputTemplate,
                    fileSizeLimitBytes: LogFileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14)))
            .WriteTo.Async(configuration => configuration.Logger(logger => logger
                .Filter.ByExcluding(static logEvent => IsSdkLogEvent(logEvent) || IsUiLogEvent(logEvent))
                .WriteTo.File(
                    Path.Combine(logDirectory, "platform-.log"),
                    outputTemplate: LogOutputTemplate,
                    fileSizeLimitBytes: LogFileSizeLimitBytes,
                    rollOnFileSizeLimit: true,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14)))
            .CreateLogger();

    private static bool IsSdkLogEvent(LogEvent logEvent)
    {
        if (!logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? sourceContext))
        {
            return false;
        }

        string category = sourceContext.ToString().Trim('"');
        return category.StartsWith("LlrpSdk", StringComparison.Ordinal)
            || category.StartsWith("LlrpNet", StringComparison.Ordinal)
            || category.StartsWith("LlrpReaderPlatform.Services.Sdk", StringComparison.Ordinal);
    }

    private static bool IsUiLogEvent(LogEvent logEvent)
    {
        if (!logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? sourceContext))
        {
            return false;
        }

        string category = sourceContext.ToString().Trim('"');
        return category.StartsWith("LlrpReaderPlatform.App.Wpf", StringComparison.Ordinal);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            if (services is not null)
            {
                try
                {
                    // WPF 的 OnExit 没有异步覆盖点；同步等待 ValueTask，确保 Reader
                    // Stop/排空/断开和 DI 异步释放在进程退出前完成。
                    services.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    // 退出阶段不能因为某个可释放服务异常而跳过其余日志工厂和
                    // WPF 基类清理；ReaderManager 自身会继续逐个释放 Reader。
                    System.Diagnostics.Debug.WriteLine(
                        $"LlrpReaderPlatform service disposal failed: {ex}");
                }
            }
        }
        finally
        {
            base.OnExit(e);
        }
    }

    private void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        logger?.LogError(e.Exception, "Unhandled WPF dispatcher exception.");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        logger?.LogError(e.Exception, "Unobserved WPF task exception.");
        e.SetObserved();
    }
}
