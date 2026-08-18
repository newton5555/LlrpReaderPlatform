using System.Collections.ObjectModel;
using LlrpVirtualDevice.App.Wpf.Models;

namespace LlrpVirtualDevice.App.Wpf.Services;

public interface IDialogService
{
    DialogRequest? CurrentDialog { get; }
    ObservableCollection<ToastItem> ActiveToasts { get; }

    event EventHandler? DialogChanged;

    Task ShowInfoAsync(string title, string message);
    Task ShowSuccessAsync(string title, string message);
    Task ShowWarningAsync(string title, string message);
    Task ShowErrorAsync(string title, string message);
    Task<bool> ShowConfirmAsync(string title, string message, string confirmText = "确定", string cancelText = "取消", bool isDanger = false);
    Task<string?> ShowInputAsync(string title, string message, string defaultText = "");

    void ShowToast(string title, string message, DialogType type = DialogType.Info, int durationMs = 3000);
    void CloseCurrentDialog(bool confirmed);
    void DismissToast(string toastId);
}
