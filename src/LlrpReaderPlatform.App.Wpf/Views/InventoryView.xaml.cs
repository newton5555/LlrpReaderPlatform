using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using LlrpReaderPlatform.App.Wpf.ViewModels;

namespace LlrpReaderPlatform.App.Wpf.Views;

public partial class InventoryView : UserControl
{
    public InventoryView()
    {
        InitializeComponent();
    }

    private void CopyEpcMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: TagRowViewModel row })
        {
            TrySetClipboardText(row.Epc);
        }
    }

    private void CopyTidMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: TagRowViewModel row } && row.HasTid)
        {
            TrySetClipboardText(row.Tid);
        }
    }

    private void CopyRowDetailsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: TagRowViewModel row })
        {
            string rowData = string.Join("\t",
                row.Index,
                row.Epc,
                row.Tid,
                row.ReadCount,
                row.FirstSeen,
                row.LastSeen,
                row.ReaderName,
                row.LastAntenna?.ToString() ?? string.Empty,
                row.PeakRssi?.ToString() ?? string.Empty,
                row.LastChannelIndex?.ToString() ?? string.Empty,
                row.PcBitsHex ?? string.Empty);
            TrySetClipboardText(rowData);
        }
    }

    private void OpenInTagMemoryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: TagRowViewModel row } && !string.IsNullOrWhiteSpace(row.Epc))
        {
            var mainVm = (Application.Current?.MainWindow as MainWindow)?.DataContext as MainViewModel;
            mainVm?.NavigateToTagMemoryWithTarget(row.Epc, row.ReaderId);
        }
    }

    private static void TrySetClipboardText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        // The Windows clipboard can be held briefly by another process. A few short
        // retries make a right-click copy reliable without turning it into VM/service logic.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(value);
                return;
            }
            catch (COMException)
            {
                if (attempt < 2)
                {
                    Thread.Sleep(20);
                }
            }
        }
    }
}
