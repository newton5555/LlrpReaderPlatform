using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderPlatform.Contracts.Errors;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Contracts.Tagging;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>
/// Tag 内存读/写页（对齐旧 TagMemoryViewModel）：EPC/Target、memory bank、
/// offset/字数、数据 hex、结果展示。只消费 IInventoryService。
/// </summary>
public partial class TagMemoryViewModel : ObservableObject, IPageOperationOwner, IDisposable
{
    private readonly IInventoryService inventory;
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly CancellationToken lifetimeToken;
    private CancellationTokenSource? activeOperationCts;
    private long readerContextVersion;
    private ReaderCapabilityContextStamp? readerContextStamp;
    private bool disposed;

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
    private string wordPointer = "0";

    [ObservableProperty]
    private string wordCount = "6";

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

    /// <summary>Tag Memory 的按钮只有在页面已经绑定到一个 Reader 时才应呈现为可用。</summary>
    public bool IsReaderSelected => ReaderId is not null;

    public TagMemoryViewModel(IInventoryService inventory)
    {
        this.inventory = inventory;
        lifetimeToken = lifetimeCts.Token;
    }

    /// <summary>把当前 Reader 上下文投影到 Tag Memory 页面，避免 View 依赖窗口级 DataContext。</summary>
    public void SetReaderContext(ReaderItemViewModel? reader)
    {
        Guid? nextReaderId = reader?.ReaderId;
        ReaderCapabilityContextStamp nextContext = ReaderCapabilityContextStamp.From(reader);
        bool contextChanged = readerContextStamp is not { } currentContext
            || currentContext != nextContext;
        if (contextChanged)
        {
            Interlocked.Increment(ref readerContextVersion);
            try
            {
                activeOperationCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 页面切换与操作完成可能并发发生。
            }
            Result = null;
            DataHex = string.Empty;
        }

        readerContextStamp = nextContext;
        ReaderId = nextReaderId;
        ReaderName = reader?.Name ?? "No reader selected";
        IsTagAccessAvailable = nextContext.TagAccessAvailable;
    }

    partial void OnReaderIdChanged(Guid? value) => OnPropertyChanged(nameof(IsReaderSelected));

    // 保持旧 Reader Studio 的操作顺序，避免把 Reserved 放在用户最常用的 EPC 前面。
    public IReadOnlyList<TagMemoryBank> MemoryBanks { get; } =
        [TagMemoryBank.Epc, TagMemoryBank.Tid, TagMemoryBank.User, TagMemoryBank.Reserved];
    public IReadOnlyList<TagMemoryBank> SelectionBanks { get; } = [TagMemoryBank.Epc, TagMemoryBank.Tid];

    [RelayCommand]
    private async Task ReadAsync(Guid? id)
    {
        if (disposed)
        {
            return;
        }

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

        if (!TryReadWordRange(out ushort offsetWords, out ushort count))
        {
            return;
        }

        if (!TryBeginOperation())
        {
            Result = "Tag 操作进行中，请稍候。";
            return;
        }

        try
        {
            long contextVersion = Volatile.Read(ref readerContextVersion);
            using CancellationTokenSource operationCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            CancellationTokenSource? previousOperationCts = Interlocked.Exchange(ref activeOperationCts, operationCts);
            previousOperationCts?.Cancel();
            var request = new TagReadRequest
            {
                Epc = Epc.Trim(),
                SelectionBank = SelectionBank,
                MemoryBank = MemoryBank,
                OffsetWords = offsetWords,
                WordCount = count,
                AccessPasswordHex = AccessPassword,
            };
            TagAccessResult res = await inventory.ReadTagMemoryAsync(readerId, request, operationCts.Token);
            if (!IsCurrentReaderContext(readerId, contextVersion, operationCts))
            {
                return;
            }

            if (res.Succeeded)
            {
                DataHex = res.DataHex ?? string.Empty;
                Result = "读取成功。";
            }
            else
            {
                Result = PlatformErrorDisplay.Failure("读取", res.ErrorCode, res.Error);
            }
        }
        catch (OperationCanceledException) when (
            lifetimeCts.IsCancellationRequested
            || activeOperationCts?.IsCancellationRequested == true)
        {
            // 页面销毁时取消正在进行的短连接操作。
        }
        catch (Exception ex)
        {
            if (!disposed && ReaderId == readerId)
            {
                Result = PlatformErrorDisplay.Failure("读取", ex);
            }
        }
        finally
        {
            Interlocked.Exchange(ref activeOperationCts, null)?.Dispose();
            EndOperation();
        }
    }

    [RelayCommand]
    private async Task WriteAsync(Guid? id)
    {
        if (disposed)
        {
            return;
        }

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

        if (!TryReadWordPointer(out ushort offsetWords))
        {
            return;
        }

        if (!TryBeginOperation())
        {
            Result = "Tag 操作进行中，请稍候。";
            return;
        }

        try
        {
            long contextVersion = Volatile.Read(ref readerContextVersion);
            using CancellationTokenSource operationCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            CancellationTokenSource? previousOperationCts = Interlocked.Exchange(ref activeOperationCts, operationCts);
            previousOperationCts?.Cancel();
            var request = new TagWriteRequest
            {
                Epc = Epc.Trim(),
                SelectionBank = SelectionBank,
                MemoryBank = MemoryBank,
                OffsetWords = offsetWords,
                DataHex = DataHex.Trim(),
                AccessPasswordHex = AccessPassword,
            };
            TagAccessResult res = await inventory.WriteTagMemoryAsync(readerId, request, operationCts.Token);
            if (!IsCurrentReaderContext(readerId, contextVersion, operationCts))
            {
                return;
            }

            Result = res.Succeeded
                ? "写入成功。"
                : PlatformErrorDisplay.Failure("写入", res.ErrorCode, res.Error);
        }
        catch (OperationCanceledException) when (
            lifetimeCts.IsCancellationRequested
            || activeOperationCts?.IsCancellationRequested == true)
        {
            // 页面销毁时取消正在进行的短连接操作。
        }
        catch (Exception ex)
        {
            if (!disposed && ReaderId == readerId)
            {
                Result = PlatformErrorDisplay.Failure("写入", ex);
            }
        }
        finally
        {
            Interlocked.Exchange(ref activeOperationCts, null)?.Dispose();
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

    private bool IsCurrentReaderContext(Guid id, long version, CancellationTokenSource operationCts) =>
        !disposed
        && !operationCts.IsCancellationRequested
        && ReaderId == id
        && Volatile.Read(ref readerContextVersion) == version
        && ReferenceEquals(activeOperationCts, operationCts);

    public void CancelPendingOperations()
    {
        CancellationTokenSource? operationCts = Volatile.Read(ref activeOperationCts);
        try
        {
            operationCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 页面切换与读写完成的释放可能并发发生。
        }
    }

    private bool TryReadWordRange(out ushort offsetWords, out ushort count)
    {
        if (!TryReadWordPointer(out offsetWords))
        {
            count = 0;
            return false;
        }

        if (!ushort.TryParse(
                WordCount.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out count)
            || count == 0)
        {
            Result = "Word count 必须是大于 0 的整数。";
            return false;
        }

        return true;
    }

    private bool TryReadWordPointer(out ushort offsetWords)
    {
        if (ushort.TryParse(
                WordPointer.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out offsetWords))
        {
            return true;
        }

        Result = "Word pointer 必须是 0 到 65535 的整数。";
        return false;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CancelPendingOperations();
        lifetimeCts.Cancel();
        lifetimeCts.Dispose();
    }
}
