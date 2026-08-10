using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderPlatform.Contracts.Persistence;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>
/// 应用级设置页（对齐旧 SettingsViewModel）：Tag Logging 开关与目录 +
/// Application 只读状态。设置通过平台的应用级键值存储持久化。
/// </summary>
public partial class AppSettingsViewModel : ObservableObject
{
    private readonly IAppSettingsStore store;

    public AppSettingsViewModel(IAppSettingsStore? store = null)
    {
        this.store = store ?? new LlrpReaderPlatform.Services.Persistence.InMemoryAppSettingsStore();
    }

    [ObservableProperty]
    private bool tagLoggingEnabled;

    [ObservableProperty]
    private string logDirectory = string.Empty;

    [ObservableProperty]
    private string? status;

    [ObservableProperty]
    private bool isBusy;

    private int operationInFlight;

    public string ApplicationStatus => "LlrpReaderPlatform.App.Wpf（数据源、设备设置、寻卡、Tag 内存、Tag Lists、运行记录）。";

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (!TryBeginOperation())
        {
            return;
        }

        try
        {
            TagLoggingEnabled = bool.TryParse(await store.GetAsync("tag-logging-enabled", ct), out bool enabled) && enabled;
            LogDirectory = await store.GetAsync("tag-log-directory", ct) ?? string.Empty;
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!TryBeginOperation())
        {
            return;
        }

        try
        {
            await store.SetAsync("tag-logging-enabled", TagLoggingEnabled.ToString(), CancellationToken.None);
            await store.SetAsync("tag-log-directory", LogDirectory ?? string.Empty, CancellationToken.None);
            Status = "应用设置已保存。";
        }
        catch (Exception ex)
        {
            Status = $"保存应用设置失败：{ex.Message}";
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

    private void EndOperation()
    {
        IsBusy = false;
        Volatile.Write(ref operationInFlight, 0);
    }
}
