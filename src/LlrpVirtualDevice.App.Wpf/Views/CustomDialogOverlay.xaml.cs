using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LlrpVirtualDevice.App.Wpf.Services;

namespace LlrpVirtualDevice.App.Wpf.Views;

public partial class CustomDialogOverlay : UserControl
{
    private IDialogService? DialogService => DataContext as IDialogService;

    public CustomDialogOverlay()
    {
        InitializeComponent();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogService?.CloseCurrentDialog(confirmed: true);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogService?.CloseCurrentDialog(confirmed: false);
    }

    private void Scrim_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DialogService?.CurrentDialog is { IsDangerConfirm: false, ShowCancelButton: true })
        {
            DialogService.CloseCurrentDialog(confirmed: false);
        }
    }

    private void InputTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            DialogService?.CloseCurrentDialog(confirmed: true);
        }
        else if (e.Key == Key.Escape)
        {
            DialogService?.CloseCurrentDialog(confirmed: false);
        }
    }

    private void InputTextBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox textBox && textBox.IsVisible)
        {
            Dispatcher.InvokeAsync(() =>
            {
                textBox.Focus();
                textBox.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    private void ToastDismiss_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string toastId })
        {
            DialogService?.DismissToast(toastId);
        }
    }
}
