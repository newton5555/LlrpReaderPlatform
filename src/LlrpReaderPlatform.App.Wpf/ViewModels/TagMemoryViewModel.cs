using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpReaderPlatform.Contracts.Errors;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Contracts.Tagging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>
/// Tag 内存读/写页（对齐旧 TagMemoryViewModel）：EPC/Target、memory bank、
/// offset/字数、数据 hex、结果展示。只消费 IInventoryService。
/// </summary>
public partial class TagMemoryViewModel : ObservableObject, IPageOperationOwner, IDisposable
{
    private const int MaxTargetSuggestions = 500;
    private readonly IInventoryService inventory;
    private readonly ILogger<TagMemoryViewModel> logger;
    private readonly DispatcherTimer targetSuggestionTimer;
    private readonly CancellationTokenSource lifetimeCts = new();
    private readonly CancellationToken lifetimeToken;
    private CancellationTokenSource? activeOperationCts;
    private long readerContextVersion;
    private ReaderCapabilityContextStamp? readerContextStamp;
    private bool updatingAvailableReaders;
    private bool disposed;
    private int targetSuggestionsDirty;

    [ObservableProperty]
    private Guid? readerId;

    [ObservableProperty]
    private string readerName = "No reader selected";

    [ObservableProperty]
    private ReaderItemViewModel? selectedAccessReader;

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

    partial void OnAccessPasswordChanged(string value)
    {
        OnPropertyChanged(nameof(AccessPasswordHelperText));
    }

    partial void OnDataHexChanged(string value)
    {
        OnPropertyChanged(nameof(DataHexHelperText));
    }

    public string AccessPasswordHelperText
    {
        get
        {
            string pwd = AccessPassword?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(pwd))
            {
                return "0/8 HEX（需 8 位十六进制 / 32-bit）";
            }

            bool isValidHex = pwd.All(Uri.IsHexDigit);
            if (!isValidHex)
            {
                return "包含非法非十六进制字符（仅支持 0-9, A-F）";
            }

            if (pwd.Length == 8)
            {
                return "8/8 HEX（32-bit 密码格式正确）";
            }

            return $"{pwd.Length}/8 HEX（需 8 位十六进制）";
        }
    }

    public string DataHexHelperText
    {
        get
        {
            string hex = DataHex?.Replace(" ", "").Replace("-", "").Replace("\r", "").Replace("\n", "") ?? string.Empty;
            if (string.IsNullOrEmpty(hex))
            {
                return "0 Words（1 Word = 16-bit / 4 位十六进制）";
            }

            bool isValidHex = hex.All(Uri.IsHexDigit);
            if (!isValidHex)
            {
                return "包含非法非十六进制字符（仅支持 0-9, A-F）";
            }

            int words = hex.Length / 4;
            int rem = hex.Length % 4;
            if (rem == 0)
            {
                return $"{words} Words（{hex.Length * 4}-bit / {hex.Length / 2} 字节）";
            }

            return $"{words} Words + {rem} 位HEX（未对齐，需为 4 位HEX的倍数）";
        }
    }

    public void SetTargetEpc(string epc)
    {
        if (!string.IsNullOrWhiteSpace(epc))
        {
            Epc = epc.Trim();
        }
    }

    [ObservableProperty]
    private string? result;

    [ObservableProperty]
    private bool isBusy;

    // 默认 true 表示能力未知时仍允许调用服务层；只有当前能力快照明确报告
    // 不支持 Tag Access 时才在 UI 层禁用操作。
    [ObservableProperty]
    private bool isTagAccessAvailable = true;

    /// <summary>Reader 是否声明支持块擦除；能力未知时默认 true（交给服务层按明确 false 拒绝）。</summary>
    [ObservableProperty]
    private bool isBlockEraseAvailable = true;

    private int operationInFlight;

    /// <summary>Tag Memory 的按钮只有在页面已经绑定到一个 Reader 时才应呈现为可用。</summary>
    public bool IsReaderSelected => ReaderId is not null;

    public bool CanSelectReader => !IsBusy;

    /// <summary>Tag Memory 只允许选择左侧开关已启用的 Reader。</summary>
    public ObservableCollection<ReaderItemViewModel> AvailableReaders { get; } = [];

    /// <summary>当前 Reader 已寻到的 EPC 或 TID；输入框仍允许录入列表外的值。</summary>
    public ObservableCollection<string> TargetMatches { get; } = [];

    public TagMemoryViewModel(
        IInventoryService inventory,
        ILogger<TagMemoryViewModel>? logger = null)
    {
        this.inventory = inventory;
        this.logger = logger ?? NullLogger<TagMemoryViewModel>.Instance;
        lifetimeToken = lifetimeCts.Token;
        Dispatcher dispatcher = System.Windows.Application.Current?.Dispatcher
            ?? Dispatcher.CurrentDispatcher;
        targetSuggestionTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(300),
            DispatcherPriority.Background,
            OnTargetSuggestionTimerTick,
            dispatcher);
        targetSuggestionTimer.Start();
        inventory.TagObserved += OnTagObserved;
    }

    /// <summary>
    /// 用 ReaderManager 的最新快照刷新操作目标。按 ReaderId 保留 Tag Memory 页内的
    /// 下拉选择，避免短连接状态刷新把用户选中的设备切回左侧当前项。
    /// </summary>
    public void UpdateAvailableReaders(
        IEnumerable<ReaderItemViewModel> readers,
        Guid? preferredReaderId = null)
    {
        ArgumentNullException.ThrowIfNull(readers);
        Guid? currentReaderId = SelectedAccessReader?.ReaderId;
        ReaderItemViewModel[] enabledReaders = readers
            .Where(static reader => reader.IsEnabled)
            .ToArray();

        updatingAvailableReaders = true;
        try
        {
            AvailableReaders.Clear();
            foreach (ReaderItemViewModel reader in enabledReaders)
            {
                AvailableReaders.Add(reader);
            }

            Guid? targetReaderId = currentReaderId is { } current
                && enabledReaders.Any(reader => reader.ReaderId == current)
                    ? current
                    : preferredReaderId is { } preferred
                        && enabledReaders.Any(reader => reader.ReaderId == preferred)
                            ? preferred
                            : enabledReaders.FirstOrDefault()?.ReaderId;
            SelectedAccessReader = targetReaderId is { } target
                ? enabledReaders.First(reader => reader.ReaderId == target)
                : null;
        }
        finally
        {
            updatingAvailableReaders = false;
        }

        // ComboBox 会在 ItemsSource.Clear() 时短暂回写 null。只在列表完成替换后
        // 应用一次最终目标，避免 Reader 状态刷新误取消正在进行的 Tag Access。
        ApplyReaderContext(SelectedAccessReader);
    }

    /// <summary>用户点击左侧启用 Reader 时，同步 Tag Memory 的本地选择。</summary>
    public void SelectReaderFromSidebar(ReaderItemViewModel? reader)
    {
        if (reader is null || !reader.IsEnabled)
        {
            return;
        }

        ReaderItemViewModel? available = AvailableReaders
            .FirstOrDefault(item => item.ReaderId == reader.ReaderId);
        if (available is not null)
        {
            SelectedAccessReader = available;
        }
    }

    /// <summary>把当前 Reader 上下文投影到 Tag Memory 页面，避免 View 依赖窗口级 DataContext。</summary>
    public void SetReaderContext(ReaderItemViewModel? reader)
    {
        SelectedAccessReader = reader;
        ApplyReaderContext(reader);
    }

    partial void OnSelectedAccessReaderChanged(ReaderItemViewModel? value)
    {
        if (!updatingAvailableReaders)
        {
            ApplyReaderContext(value);
        }
    }

    private void ApplyReaderContext(ReaderItemViewModel? reader)
    {
        Guid? nextReaderId = reader?.ReaderId;
        ReaderCapabilityContextStamp nextContext = ReaderCapabilityContextStamp.From(reader);
        bool contextChanged = readerContextStamp is not { } currentContext
            || currentContext != nextContext;
        bool readerChanged = readerContextStamp?.ReaderId != nextReaderId;
        if (contextChanged && (readerChanged || !IsBusy))
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
        IsBlockEraseAvailable = reader?.Snapshot?.FeatureCatalog.SupportsOrUnknown(ReaderFeatures.StandardBlockTagAccess) ?? false;
        RefreshTargetMatches();
    }

    partial void OnReaderIdChanged(Guid? value) => OnPropertyChanged(nameof(IsReaderSelected));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanSelectReader));

    partial void OnSelectionBankChanged(TagMemoryBank value) => RefreshTargetMatches();

    private void OnTagObserved(object? sender, TagObservedEventArgs args)
    {
        if (!disposed && ReaderId == args.ReaderId)
        {
            // 高频 TagReport 只置脏标记，ObservableCollection 最多每 300ms 更新一次。
            Interlocked.Exchange(ref targetSuggestionsDirty, 1);
        }
    }

    private void OnTargetSuggestionTimerTick(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref targetSuggestionsDirty, 0) != 0)
        {
            RefreshTargetMatches();
        }
    }

    public void RefreshTargetMatches()
    {
        if (disposed || ReaderId is not { } readerId)
        {
            TargetMatches.Clear();
            return;
        }

        string[] matches = inventory.GetTags(readerId)
            .OrderByDescending(static tag => tag.LastSeen)
            .Select(tag => SelectionBank == TagMemoryBank.Tid ? tag.Tid : tag.Epc)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTargetSuggestions)
            .ToArray();
        if (TargetMatches.SequenceEqual(matches, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        TargetMatches.Clear();
        foreach (string match in matches)
        {
            TargetMatches.Add(match);
        }
    }

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
            Result = "正在读取标签内存...";
            Guid operationId = Guid.NewGuid();
            logger.LogInformation(
                "WPF operation {Operation} started: {OperationId}, reader {ReaderId}, bank {MemoryBank}, words {WordCount}.",
                "ReadTagMemory",
                operationId,
                readerId,
                MemoryBank,
                count);
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
                Result = res.ErrorCode == PlatformErrorCode.NotFound
                    ? res.Error ?? "未找到匹配标签，操作超时。"
                    : PlatformErrorDisplay.Failure("读取", res.ErrorCode, res.Error);
            }
            logger.LogInformation(
                "WPF operation {Operation} completed: {OperationId}, reader {ReaderId}, succeeded {Succeeded}, error code {ErrorCode}.",
                "ReadTagMemory",
                operationId,
                readerId,
                res.Succeeded,
                res.ErrorCode);
        }
        catch (OperationCanceledException) when (
            lifetimeCts.IsCancellationRequested
            || activeOperationCts?.IsCancellationRequested == true)
        {
            // 页面销毁时取消正在进行的短连接操作。
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WPF operation {Operation} failed for reader {ReaderId}.", "ReadTagMemory", readerId);
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
            Result = "正在写入标签内存...";
            Guid operationId = Guid.NewGuid();
            logger.LogInformation(
                "WPF operation {Operation} started: {OperationId}, reader {ReaderId}, bank {MemoryBank}.",
                "WriteTagMemory",
                operationId,
                readerId,
                MemoryBank);
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
                : res.ErrorCode == PlatformErrorCode.NotFound
                    ? res.Error ?? "未找到匹配标签，操作超时。"
                    : PlatformErrorDisplay.Failure("写入", res.ErrorCode, res.Error);
            logger.LogInformation(
                "WPF operation {Operation} completed: {OperationId}, reader {ReaderId}, succeeded {Succeeded}, error code {ErrorCode}.",
                "WriteTagMemory",
                operationId,
                readerId,
                res.Succeeded,
                res.ErrorCode);
        }
        catch (OperationCanceledException) when (
            lifetimeCts.IsCancellationRequested
            || activeOperationCts?.IsCancellationRequested == true)
        {
            // 页面销毁时取消正在进行的短连接操作。
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WPF operation {Operation} failed for reader {ReaderId}.", "WriteTagMemory", readerId);
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

    [RelayCommand]
    private async Task BlockEraseAsync(Guid? id)
    {
        if (disposed || !IsBlockEraseAvailable)
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
            Result = "正在块擦除标签内存...";
            Guid operationId = Guid.NewGuid();
            logger.LogInformation(
                "WPF operation {Operation} started: {OperationId}, reader {ReaderId}, bank {MemoryBank}, words {WordCount}.",
                "BlockEraseTagMemory",
                operationId,
                readerId,
                MemoryBank,
                count);
            long contextVersion = Volatile.Read(ref readerContextVersion);
            using CancellationTokenSource operationCts = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
            CancellationTokenSource? previousOperationCts = Interlocked.Exchange(ref activeOperationCts, operationCts);
            previousOperationCts?.Cancel();
            var request = new TagBlockEraseRequest
            {
                Epc = Epc.Trim(),
                SelectionBank = SelectionBank,
                MemoryBank = MemoryBank,
                OffsetWords = offsetWords,
                WordCount = count,
                AccessPasswordHex = AccessPassword,
            };
            TagAccessResult res = await inventory.BlockEraseTagMemoryAsync(readerId, request, operationCts.Token);
            if (!IsCurrentReaderContext(readerId, contextVersion, operationCts))
            {
                return;
            }

            Result = res.Succeeded
                ? "块擦除成功。"
                : res.ErrorCode == PlatformErrorCode.NotFound
                    ? res.Error ?? "未找到匹配标签，操作超时。"
                    : PlatformErrorDisplay.Failure("块擦除", res.ErrorCode, res.Error);
            logger.LogInformation(
                "WPF operation {Operation} completed: {OperationId}, reader {ReaderId}, succeeded {Succeeded}, error code {ErrorCode}.",
                "BlockEraseTagMemory",
                operationId,
                readerId,
                res.Succeeded,
                res.ErrorCode);
        }
        catch (OperationCanceledException) when (
            lifetimeCts.IsCancellationRequested
            || activeOperationCts?.IsCancellationRequested == true)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "WPF operation {Operation} failed for reader {ReaderId}.", "BlockEraseTagMemory", readerId);
            if (!disposed && ReaderId == readerId)
            {
                Result = PlatformErrorDisplay.Failure("块擦除", ex);
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
        inventory.TagObserved -= OnTagObserved;
        targetSuggestionTimer.Stop();
        CancelPendingOperations();
        lifetimeCts.Cancel();
        lifetimeCts.Dispose();
    }
}
