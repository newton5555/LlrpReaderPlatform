using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace LlrpReaderPlatform.App.Wpf.Views;

public partial class AppSettingsView : UserControl
{
    public AppSettingsView()
    {
        InitializeComponent();
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string folder = Path.Combine(
            string.IsNullOrWhiteSpace(localAppData) ? AppContext.BaseDirectory : localAppData,
            "LlrpReaderPlatform",
            "logs");

        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"无法打开日志文件夹：{ex.Message}\n{folder}",
                "打开日志文件夹",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
