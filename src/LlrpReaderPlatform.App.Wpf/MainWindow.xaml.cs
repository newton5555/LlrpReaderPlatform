using LlrpReaderPlatform.App.Wpf.ViewModels;
using MahApps.Metro.Controls;

namespace LlrpReaderPlatform.App.Wpf;

public partial class MainWindow : MetroWindow
{
    private bool initialized;

    public MainWindow(MainViewModel viewModel)
    {
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
                await viewModel.InitializeAsync();
            }
            catch
            {
                // 初始化错误已投影到状态栏；保留窗口，使用户可以查看错误并处理配置。
            }
        };
    }
}
