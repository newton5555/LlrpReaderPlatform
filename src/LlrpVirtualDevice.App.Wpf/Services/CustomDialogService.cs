using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using LlrpVirtualDevice.App.Wpf.Models;

namespace LlrpVirtualDevice.App.Wpf.Services;

public sealed partial class CustomDialogService : ObservableObject, IDialogService
{
    private readonly Dispatcher _dispatcher;
    private readonly Queue<DialogRequest> _dialogQueue = new();

    [ObservableProperty]
    private DialogRequest? _currentDialog;

    public ObservableCollection<ToastItem> ActiveToasts { get; } = [];

    public event EventHandler? DialogChanged;

    public CustomDialogService()
    {
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public Task ShowInfoAsync(string title, string message) =>
        ShowDialogInternalAsync(new DialogRequest
        {
            Title = title,
            Message = message,
            Type = DialogType.Info,
            ConfirmText = "我知道了",
            ShowCancelButton = false,
        });

    public Task ShowSuccessAsync(string title, string message) =>
        ShowDialogInternalAsync(new DialogRequest
        {
            Title = title,
            Message = message,
            Type = DialogType.Success,
            ConfirmText = "确定",
            ShowCancelButton = false,
        });

    public Task ShowWarningAsync(string title, string message) =>
        ShowDialogInternalAsync(new DialogRequest
        {
            Title = title,
            Message = message,
            Type = DialogType.Warning,
            ConfirmText = "我知道了",
            ShowCancelButton = false,
        });

    public Task ShowErrorAsync(string title, string message) =>
        ShowDialogInternalAsync(new DialogRequest
        {
            Title = title,
            Message = message,
            Type = DialogType.Error,
            ConfirmText = "关闭",
            ShowCancelButton = false,
        });

    public async Task<bool> ShowConfirmAsync(string title, string message, string confirmText = "确定", string cancelText = "取消", bool isDanger = false)
    {
        var result = await ShowDialogInternalAsync(new DialogRequest
        {
            Title = title,
            Message = message,
            Type = DialogType.Confirm,
            ConfirmText = confirmText,
            CancelText = cancelText,
            ShowCancelButton = true,
            IsDangerConfirm = isDanger,
        });

        return result.Confirmed;
    }

    public async Task<string?> ShowInputAsync(string title, string message, string defaultText = "")
    {
        var request = new DialogRequest
        {
            Title = title,
            Message = message,
            Type = DialogType.Input,
            ConfirmText = "确定",
            CancelText = "取消",
            ShowCancelButton = true,
            IsInput = true,
            InputText = defaultText,
        };

        var result = await ShowDialogInternalAsync(request);
        return result.Confirmed ? request.InputText : null;
    }

    public void ShowToast(string title, string message, DialogType type = DialogType.Info, int durationMs = 3000)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(() => ShowToast(title, message, type, durationMs));
            return;
        }

        var item = new ToastItem
        {
            Title = title,
            Message = message,
            Type = type,
        };

        ActiveToasts.Insert(0, item);
        if (ActiveToasts.Count > 5)
        {
            ActiveToasts.RemoveAt(ActiveToasts.Count - 1);
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(durationMs) };
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            ActiveToasts.Remove(item);
        };
        timer.Start();
    }

    public void DismissToast(string toastId)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(() => DismissToast(toastId));
            return;
        }

        var item = ActiveToasts.FirstOrDefault(t => t.Id == toastId);
        if (item != null)
        {
            ActiveToasts.Remove(item);
        }
    }

    public void CloseCurrentDialog(bool confirmed)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(() => CloseCurrentDialog(confirmed));
            return;
        }

        if (CurrentDialog == null) return;

        var active = CurrentDialog;
        CurrentDialog = null;
        DialogChanged?.Invoke(this, EventArgs.Empty);

        if (confirmed)
        {
            active.CompletionSource.TrySetResult(DialogResult.Success(active.InputText));
        }
        else
        {
            active.CompletionSource.TrySetResult(DialogResult.Cancelled);
        }

        ProcessNextDialog();
    }

    private Task<DialogResult> ShowDialogInternalAsync(DialogRequest request)
    {
        if (_dispatcher.CheckAccess())
        {
            EnqueueOrShowDialog(request);
        }
        else
        {
            _dispatcher.InvokeAsync(() => EnqueueOrShowDialog(request));
        }

        return request.CompletionSource.Task;
    }

    private void EnqueueOrShowDialog(DialogRequest request)
    {
        if (CurrentDialog == null)
        {
            CurrentDialog = request;
            DialogChanged?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            _dialogQueue.Enqueue(request);
        }
    }

    private void ProcessNextDialog()
    {
        if (_dialogQueue.Count > 0)
        {
            CurrentDialog = _dialogQueue.Dequeue();
            DialogChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
