using System.IO;
using System.Windows;
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
    private ILoggerFactory? sdkLoggerFactory;

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

        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddDebug();
            builder.AddSerilog(CreateRollingLogger(logDirectory, "platform-.log", excludeSdkCategories: true), dispose: true);
        });

        sdkLoggerFactory = LoggerFactory.Create(builder =>
        {
            // Keep SDK lifecycle/protocol errors and useful informational events, but do not
            // send one Debug line for every RO_ACCESS_REPORT through the Debug provider. The
            // transport Debug stream is a second bottleneck under ReportEveryNTags=1 and can
            // make Visual Studio's output listener starve the WPF dispatcher.
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddDebug();
            builder.AddSerilog(CreateRollingLogger(logDirectory, "sdk-.log", excludeSdkCategories: false), dispose: true);
        });

        // 组合根：显式注册共享服务层、基础设施与已启用的厂商扩展。
        services.AddLlrpReaderPlatform();
        services.AddLlrpInfrastructure();
        services.AddImpinjExtension();

        // SDK / LLRP wire logs use a dedicated rolling file. The platform logger above
        // keeps service and UI diagnostics separate from protocol traffic.
        services.AddSingleton<IReaderSessionFactory>(_ =>
            new LlrpReaderSessionFactory(sdkLoggerFactory
                ?? throw new InvalidOperationException("SDK logger factory is not initialized.")));

        services.AddLlrpReaderPlatformWpf();
    }

    private static Serilog.ILogger CreateRollingLogger(
        string logDirectory,
        string fileName,
        bool excludeSdkCategories) =>
        new LoggerConfiguration()
            .MinimumLevel.Is(DefaultLogLevel)
            .Filter.ByExcluding(logEvent => excludeSdkCategories && IsSdkLogEvent(logEvent))
            .WriteTo.Async(configuration => configuration.File(
                Path.Combine(logDirectory, fileName),
                outputTemplate: LogOutputTemplate,
                fileSizeLimitBytes: LogFileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14))
            .CreateLogger();

    private static bool IsSdkLogEvent(LogEvent logEvent)
    {
        if (!logEvent.Properties.TryGetValue("SourceContext", out LogEventPropertyValue? sourceContext))
        {
            return false;
        }

        string category = sourceContext.ToString().Trim('"');
        return category.StartsWith("LlrpSdk", StringComparison.Ordinal)
            || category.StartsWith("LlrpNet", StringComparison.Ordinal);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
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
            try
            {
                sdkLoggerFactory?.Dispose();
            }
            finally
            {
                base.OnExit(e);
            }
        }
    }
}
