using LlrpSdk;
using LlrpReaderPlatform.Contracts.Errors;
using Tagging = LlrpReaderPlatform.Contracts.Tagging;

namespace LlrpReaderPlatform.Services.Sdk;

/// <summary>
/// 平台 TagAccess 请求/结果 与 LlrpSdk 之间的映射（纯函数，便于独立测试）。
/// </summary>
public static class SdkTagAccessMapper
{
    /// <summary>
    /// 在建立 Reader 短连接前校验读请求，避免明显的用户输入错误被误报成设备/网络故障。
    /// </summary>
    public static void ValidateReadRequest(Tagging.TagReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateMemoryBank(request.MemoryBank);
        ValidateTarget(request.Epc, request.SelectionBank);
        if (request.WordCount == 0)
        {
            throw new FormatException("读取字数必须大于 0。");
        }

        _ = ParseAccessPassword(request.AccessPasswordHex);
    }

    /// <summary>在建立 Reader 短连接前校验写请求。</summary>
    public static void ValidateWriteRequest(Tagging.TagWriteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateMemoryBank(request.MemoryBank);
        ValidateTarget(request.Epc, request.SelectionBank);
        _ = ParseWords(request.DataHex);
        _ = ParseAccessPassword(request.AccessPasswordHex);
    }

    /// <summary>在建立 Reader 短连接前校验块擦除请求。</summary>
    public static void ValidateBlockEraseRequest(Tagging.TagBlockEraseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateMemoryBank(request.MemoryBank);
        ValidateTarget(request.Epc, request.SelectionBank);
        if (request.WordCount == 0)
        {
            throw new FormatException("Block erase word count must be greater than zero.");
        }

        _ = ParseAccessPassword(request.AccessPasswordHex);
    }

    /// <summary>按 EPC 十六进制构造 TagSelection（BitPointer=32 跳过 EPC 段头）。</summary>
    public static LlrpSdk.TagSelection BuildEpcSelection(string epcHex)
        => BuildSelection(epcHex, Tagging.TagMemoryBank.Epc);

    /// <summary>
    /// 构造旧 WPF 同语义的精确目标选择：EPC 目标跳过 32-bit EPC header，TID 目标从 bit 0 匹配。
    /// </summary>
    public static LlrpSdk.TagSelection BuildSelection(string targetHex, Tagging.TagMemoryBank selectionBank)
    {
        if (selectionBank is not (Tagging.TagMemoryBank.Epc or Tagging.TagMemoryBank.Tid))
        {
            throw new ArgumentOutOfRangeException(nameof(selectionBank), "目标匹配只支持 EPC 或 TID。");
        }

        string normalized = NormalizeHex(targetHex);
        if (normalized.Length == 0)
        {
            throw new FormatException("目标匹配数据不能为空。");
        }

        if ((normalized.Length & 1) != 0)
        {
            throw new FormatException("目标匹配数据必须包含偶数个十六进制字符。");
        }

        byte[] target = Convert.FromHexString(normalized);
        if (target.Length > ushort.MaxValue / 8)
        {
            throw new FormatException("目标匹配数据超过 LLRP 选择条件的最大长度。");
        }

        return new LlrpSdk.TagSelection
        {
            MemoryBank = (LlrpSdk.TagMemoryBank)selectionBank,
            BitPointer = selectionBank == Tagging.TagMemoryBank.Epc ? (ushort)32 : (ushort)0,
            BitLength = (ushort)(target.Length * 8),
            Data = target,
            Mask = Enumerable.Repeat((byte)0xFF, target.Length).ToArray(),
            Match = true,
        };
    }

    /// <summary>
    /// 把平台层的访问密码文本规范化为 SDK 1.5.0 要求的“恰好 8 位十六进制”表示。
    /// 空/空白输入返回 SDK 默认 {@code 00000000}；已有输入做数字语义左补零到 8 位，
    /// 与旧版（uint 密码）保持同一数值含义。
    /// </summary>
    public static string ParseAccessPassword(string? passwordHex)
    {
        if (string.IsNullOrWhiteSpace(passwordHex))
        {
            return "00000000";
        }

        string value = NormalizeHex(passwordHex);

        if (value.Length == 0 || value.Length > 8
            || !uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out uint password))
        {
            throw new FormatException("Access password must be up to 8 hexadecimal characters.");
        }

        return password.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 把平台层的大端十六进制字节串解析为 LLRP C1G2 的 16-bit word 列表。
    /// TagAccess 的 WriteData 是 U16Vector，不能把每个 byte 当成一个 word。
    /// </summary>
    public static IReadOnlyList<ushort> ParseWords(string dataHex)
    {
        if (string.IsNullOrWhiteSpace(dataHex))
        {
            throw new FormatException("写入数据不能为空。");
        }

        string value = dataHex.Trim()
            .Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        if (value.Length == 0 || value.Length % 4 != 0)
        {
            throw new FormatException("写入数据必须是偶数个 16-bit word（每个 word 4 个十六进制字符）。");
        }

        var words = new ushort[value.Length / 4];
        for (int i = 0; i < words.Length; i++)
        {
            if (!ushort.TryParse(value.AsSpan(i * 4, 4), System.Globalization.NumberStyles.HexNumber,
                    provider: null, out words[i]))
            {
                throw new FormatException("写入数据包含无效的十六进制字符。");
            }
        }

        return words;
    }

    /// <summary>SDK TagAccessOperationResult → 平台 TagAccessResult（ReadData 为 word 大端字节序 hex）。</summary>
    public static Tagging.TagAccessResult MapOperationResult(LlrpSdk.TagAccessOperationResult? operation)
    {
        if (operation is null)
        {
            return new Tagging.TagAccessResult(false, "Tag access 无操作结果。")
            { ErrorCode = PlatformErrorCode.DeviceFailed };
        }

        if (operation.Success)
        {
            string? dataHex = operation.ReadData is { Count: > 0 }
                ? Convert.ToHexString(ToWordBytes(operation.ReadData))
                : null;
            return new Tagging.TagAccessResult(true, DataHex: dataHex);
        }

        return new Tagging.TagAccessResult(false, operation.Error)
        { ErrorCode = PlatformErrorCode.DeviceFailed };
    }

    /// <summary>ushort word 列表 → 大端字节序。</summary>
    public static byte[] ToWordBytes(IReadOnlyList<ushort> words)
    {
        var bytes = new byte[words.Count * 2];
        for (int i = 0; i < words.Count; i++)
        {
            bytes[i * 2] = (byte)(words[i] >> 8);
            bytes[i * 2 + 1] = (byte)(words[i] & 0xFF);
        }

        return bytes;
    }

    private static void ValidateTarget(string targetHex, Tagging.TagMemoryBank selectionBank) =>
        _ = BuildSelection(targetHex, selectionBank);

    private static void ValidateMemoryBank(Tagging.TagMemoryBank memoryBank)
    {
        if (!Enum.IsDefined(memoryBank))
        {
            throw new ArgumentOutOfRangeException(nameof(memoryBank), "Memory bank 无效。");
        }
    }

    private static string NormalizeHex(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string normalized = new string(value
            .Where(static character => !char.IsWhiteSpace(character) && character is not '-' and not ':')
            .ToArray());
        return normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? normalized[2..]
            : normalized;
    }
}
