using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderPlatform.Contracts.Persistence;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>InventoryRun 历史页：只读展示服务层已完成的运行记录和日志路径。</summary>
public partial class InventoryRunsViewModel : ObservableObject
{
    private readonly IInventoryRunStore store;

    public InventoryRunsViewModel(IInventoryRunStore store)
    {
        this.store = store;
    }

    public ObservableCollection<InventoryRunRowViewModel> Runs { get; } = [];

    [ObservableProperty]
    private Guid? readerId;

    [ObservableProperty]
    private string? status;

    [ObservableProperty]
    private bool isBusy;

    private int operationInFlight;

    public void SelectReader(Guid? id)
    {
        ReaderId = id;
        _ = LoadAsync(id);
    }

    [RelayCommand]
    private async Task LoadAsync(Guid? id = null)
    {
        if (!TryBeginOperation())
        {
            Status = "运行记录加载中，请稍候。";
            return;
        }

        try
        {
            await LoadCoreAsync(id);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task LoadCoreAsync(Guid? id)
    {
        Guid? target = id ?? ReaderId;
        if (target is null)
        {
            Runs.Clear();
            Status = "请先在左侧选择 Reader。";
            return;
        }

        try
        {
            IReadOnlyList<InventoryRunRecord> records = await store.GetForReaderAsync(target.Value, CancellationToken.None);
            Runs.Clear();
            foreach (InventoryRunRecord record in records)
            {
                Runs.Add(new InventoryRunRowViewModel(record));
            }

            Status = $"已加载 {Runs.Count} 条运行记录。";
        }
        catch (Exception ex)
        {
            Status = $"读取运行记录失败：{ex.Message}";
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

public sealed record InventoryRunRowViewModel(InventoryRunRecord Record)
{
    public DateTimeOffset StartedAtUtc => Record.StartedAtUtc;
    public DateTimeOffset? EndedAtUtc => Record.EndedAtUtc;
    public string StopReason => Record.StopReason;
    public long TotalReadCount => Record.TotalReadCount;
    public int UniqueTagCount => Record.UniqueTagCount;
    public string LogFilePath => Record.LogFilePath ?? string.Empty;
}
