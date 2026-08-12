using LlrpReaderPlatform.App.Wpf.ViewModels;
using Microsoft.Extensions.Logging;
using MahApps.Metro.Controls;

namespace LlrpReaderPlatform.App.Wpf;

public partial class MainWindow : MetroWindow
{
    private bool initialized;
    private readonly ILogger<MainWindow> logger;

    public MainWindow(MainViewModel viewModel, ILogger<MainWindow>? logger = null)
    {
        this.logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MainWindow>.Instance;
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) =>
        {
            if (initialized)
            {
                return;
            }

            initialized = true;
            try
            {
                this.logger.LogInformation("WPF main window initialized.");
                await viewModel.InitializeAsync();
            }
            catch (Exception ex)
            {
                this.logger.LogError(ex, "WPF main window initialization failed.");
                // 初始化错误已投影到状态栏；保留窗口，使用户可以查看错误并处理配置。
            }
        };
    }
}
