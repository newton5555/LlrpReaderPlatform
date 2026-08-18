using CommunityToolkit.Mvvm.ComponentModel;

namespace LlrpVirtualDevice.App.Wpf.Models;

public enum DialogType
{
    Info,
    Success,
    Warning,
    Error,
    Confirm,
    Input
}

public sealed partial class DialogRequest : ObservableObject
{
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public DialogType Type { get; init; } = DialogType.Info;
    public string ConfirmText { get; init; } = "确定";
    public string CancelText { get; init; } = "取消";
    public bool ShowCancelButton { get; init; }
    public bool IsDangerConfirm { get; init; }
    public bool IsInput { get; init; }

    [ObservableProperty]
    private string _inputText = string.Empty;

    public TaskCompletionSource<DialogResult> CompletionSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed record DialogResult
{
    public bool Confirmed { get; init; }
    public string? InputText { get; init; }

    public static DialogResult Cancelled => new() { Confirmed = false };
    public static DialogResult Success(string? input = null) => new() { Confirmed = true, InputText = input };
}

public sealed partial class ToastItem : ObservableObject
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public DialogType Type { get; init; } = DialogType.Info;
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;
}
