using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Contracts.Tagging;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>
/// Tag 内存读/写页（对齐旧 TagMemoryViewModel）：EPC/Target、memory bank、
/// offset/字数、数据 hex、结果展示。只消费 IInventoryService。
/// </summary>
public partial class TagMemoryViewModel : ObservableObject
{
    private readonly IInventoryService inventory;

    [ObservableProperty]
    private Guid? readerId;

    [ObservableProperty]
    private string readerName = "No reader selected";

    [ObservableProperty]
    private string epc = string.Empty;

    [ObservableProperty]
    private TagMemoryBank selectionBank = TagMemoryBank.Epc;

    [ObservableProperty]
    // 旧 Reader Studio 默认把 Tag Memory 写入目标放在 User Bank；EPC/TID 仍可由用户显式选择。
    private TagMemoryBank memoryBank = TagMemoryBank.User;

    [ObservableProperty]
    private ushort wordPointer;

    [ObservableProperty]
    private ushort wordCount = 6;

    [ObservableProperty]
    private string accessPassword = "00000000";

    [ObservableProperty]
    private string dataHex = string.Empty;

    [ObservableProperty]
    private string? result;

    [ObservableProperty]
    private bool isBusy;

    // 默认 true 表示能力未知时仍允许调用服务层；只有当前能力快照明确报告
    // 不支持 Tag Access 时才在 UI 层禁用操作。
    [ObservableProperty]
    private bool isTagAccessAvailable = true;

    private int operationInFlight;

    public TagMemoryViewModel(IInventoryService inventory)
    {
        this.inventory = inventory;
    }

    /// <summary>把当前 Reader 上下文投影到 Tag Memory 页面，避免 View 依赖窗口级 DataContext。</summary>
    public void SetReaderContext(ReaderItemViewModel? reader)
    {
        ReaderId = reader?.ReaderId;
        ReaderName = reader?.Name ?? "No reader selected";
        IsTagAccessAvailable = reader is null
            || reader.Snapshot.IsStale
            || reader.Snapshot.CapabilityRevision == 0
            || reader.Snapshot.FeatureCatalog.SupportsOrUnknown(ReaderFeatures.StandardTagAccess);
    }

    // 保持旧 Reader Studio 的操作顺序，避免把 Reserved 放在用户最常用的 EPC 前面。
    public IReadOnlyList<TagMemoryBank> MemoryBanks { get; } =
        [TagMemoryBank.Epc, TagMemoryBank.Tid, TagMemoryBank.User, TagMemoryBank.Reserved];
    public IReadOnlyList<TagMemoryBank> SelectionBanks { get; } = [TagMemoryBank.Epc, TagMemoryBank.Tid];

    [RelayCommand]
    private async Task ReadAsync(Guid? id)
    {
        if (id is not { } readerId)
        {
            Result = "请先从左侧选择 Reader。";
            return;
        }

        ReaderId = readerId;
        if (!IsTagAccessAvailable)
        {
            Result = "当前 Reader 未声明标准 Tag Access 能力。";
            return;
        }

        if (string.IsNullOrWhiteSpace(Epc))
        {
            Result = "请先填写 EPC。";
            return;
        }

        if (!TryBeginOperation())
        {
            Result = "Tag 操作进行中，请稍候。";
            return;
        }

        try
        {
            var request = new TagReadRequest
            {
                Epc = Epc.Trim(),
                SelectionBank = SelectionBank,
                MemoryBank = MemoryBank,
                OffsetWords = WordPointer,
                WordCount = WordCount,
                AccessPasswordHex = AccessPassword,
            };
            TagAccessResult res = await inventory.ReadTagMemoryAsync(readerId, request, CancellationToken.None);
            if (res.Succeeded)
            {
                DataHex = res.DataHex ?? string.Empty;
                Result = "读取成功。";
            }
            else
            {
                Result = $"读取失败: {res.Error}";
            }
        }
        catch (Exception ex)
        {
            Result = $"读取失败: {ex.Message}";
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand]
    private async Task WriteAsync(Guid? id)
    {
        if (id is not { } readerId)
        {
            Result = "请先从左侧选择 Reader。";
            return;
        }

        ReaderId = readerId;
        if (!IsTagAccessAvailable)
        {
            Result = "当前 Reader 未声明标准 Tag Access 能力。";
            return;
        }

        if (string.IsNullOrWhiteSpace(Epc) || string.IsNullOrWhiteSpace(DataHex))
        {
            Result = "请先填写 EPC 与数据。";
            return;
        }

        if (!TryBeginOperation())
        {
            Result = "Tag 操作进行中，请稍候。";
            return;
        }

        try
        {
            var request = new TagWriteRequest
            {
                Epc = Epc.Trim(),
                SelectionBank = SelectionBank,
                MemoryBank = MemoryBank,
                OffsetWords = WordPointer,
                DataHex = DataHex.Trim(),
                AccessPasswordHex = AccessPassword,
            };
            TagAccessResult res = await inventory.WriteTagMemoryAsync(readerId, request, CancellationToken.None);
            Result = res.Succeeded ? "写入成功。" : $"写入失败: {res.Error}";
        }
        catch (Exception ex)
        {
            Result = $"写入失败: {ex.Message}";
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
