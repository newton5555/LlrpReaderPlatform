using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderPlatform.Contracts.Errors;
using LlrpReaderPlatform.Contracts.Persistence;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>
/// 应用级设置页（对齐旧 SettingsViewModel）：Tag Logging 开关与目录 +
/// Application 只读状态。设置通过平台的应用级键值存储持久化。
/// </summary>
public partial class AppSettingsViewModel : ObservableObject, IPageOperationOwner, IDisposable
{
    private const string TagLoggingEnabledKey = "tag-logging-enabled";
    private const string TagLogDirectoryKey = "tag-log-directory";
    private readonly IAppSettingsStore store;
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly CancellationToken lifetimeToken;
    private CancellationTokenSource? activeOperationCts;
    private bool disposed;

    public AppSettingsViewModel(IAppSettingsStore store)
    {
        this.store = store;
        lifetimeToken = lifetimeCts.Token;
    }

    [ObservableProperty]
    private bool tagLoggingEnabled;

    [ObservableProperty]
    private string logDirectory = GetDefaultTagLogDirectory();

    [ObservableProperty]
    private string? status;

    [ObservableProperty]
    private bool isBusy;

    private int operationInFlight;

    public string ApplicationStatus => "LlrpReaderPlatform.App.Wpf（数据源、设备设置、寻卡、Tag 内存、Tag Lists、运行记录）。";

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (disposed)
        {
            return;
        }

        if (!TryBeginOperation())
        {
            return;
        }

        CancellationTokenSource operationCts = BeginOperation(ct);
        try
        {
            TagLoggingEnabled = bool.TryParse(
                await store.GetAsync(TagLoggingEnabledKey, operationCts.Token),
                out bool enabled) && enabled;
            string? configuredDirectory = await store.GetAsync(TagLogDirectoryKey, operationCts.Token);
            LogDirectory = string.IsNullOrWhiteSpace(configuredDirectory)
                ? GetDefaultTagLogDirectory()
                : configuredDirectory.Trim();
        }
        catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
        {
            // 页面离开或窗口退出时取消应用设置读取。
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (disposed)
        {
            return;
        }

        if (!TryBeginOperation())
        {
            return;
        }

        CancellationTokenSource operationCts = BeginOperation();
        try
        {
            CancellationToken token = operationCts.Token;
            LogDirectory = string.IsNullOrWhiteSpace(LogDirectory)
                ? GetDefaultTagLogDirectory()
                : LogDirectory.Trim();
            await store.SetAsync(TagLoggingEnabledKey, TagLoggingEnabled.ToString(), token);
            await store.SetAsync(TagLogDirectoryKey, LogDirectory, token);
            Status = "应用设置已保存。";
        }
        catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
        {
            // 页面离开或窗口退出时取消应用设置保存。
        }
        catch (Exception ex)
        {
            if (!disposed)
            {
                Status = PlatformErrorDisplay.Failure("保存应用设置", PlatformErrorCode.PersistenceFailed, ex.Message);
            }
        }
        finally
        {
            EndOperation();
        }
    }

    private bool TryBeginOperation() =>
        Interlocked.CompareExchange(ref operationInFlight, 1, 0) == 0
        && SetBusyAndReturnTrue();

    private bool SetBusyAndReturnTrue()
    {
        IsBusy = true;
        return true;
    }

    public void CancelPendingOperations() => CancelActiveOperation();

    private CancellationTokenSource BeginOperation(CancellationToken external = default)
    {
        CancellationTokenSource operationCts = external.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, external)
            : CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref activeOperationCts,
            operationCts);
        CancelAndDispose(previous);
        return operationCts;
    }

    private void EndOperation()
    {
        CancellationTokenSource? operationCts = Interlocked.Exchange(ref activeOperationCts, null);
        operationCts?.Dispose();
        EndOperationState();
    }

    private void EndOperationState()
    {
        IsBusy = false;
        Volatile.Write(ref operationInFlight, 0);
    }

    private void CancelActiveOperation()
    {
        CancellationTokenSource? operationCts = Volatile.Read(ref activeOperationCts);
        try
        {
            operationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 操作刚好完成时，取消请求与释放可能并发发生。
        }
    }

    private static void CancelAndDispose(CancellationTokenSource? operationCts)
    {
        if (operationCts is null)
        {
            return;
        }

        try
        {
            operationCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        operationCts.Dispose();
    }

    private static string GetDefaultTagLogDirectory()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(
            string.IsNullOrWhiteSpace(localAppData) ? AppContext.BaseDirectory : localAppData,
            "LlrpReaderPlatform",
            "tag-logs");
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CancelActiveOperation();
        lifetimeCts.Cancel();
        lifetimeCts.Dispose();
    }
}
