using LlrpReaderPlatform.Contracts.Readers;

namespace LlrpReaderPlatform.Services.Capabilities;

/// <summary>
/// 从 Reader 能力提取平台天线列表。输入为平台语义值（最大天线数），
/// 由 ReaderManager 从 LlrpSdk ReaderCapabilities.MaxNumberOfAntennas 提取，
/// 工厂本身不依赖 SDK 类型，便于测试。
/// </summary>
public static class ReaderAntennaFactory
{
    public static IReadOnlyList<ReaderAntennaInfo> FromMaxAntennas(ushort maxAntennas)
    {
        if (maxAntennas == 0)
        {
            return [];
        }

        var antennas = new List<ReaderAntennaInfo>(maxAntennas);
        for (ushort i = 1; i <= maxAntennas; i++)
        {
            antennas.Add(new ReaderAntennaInfo { AntennaId = i, Name = $"Antenna {i}" });
        }

        return antennas;
    }
}
