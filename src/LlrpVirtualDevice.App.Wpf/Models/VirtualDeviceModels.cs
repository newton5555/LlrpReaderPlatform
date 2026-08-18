using System.Text.Json.Serialization;
using LlrpDevice.Virtual;

namespace LlrpVirtualDevice.App.Wpf.Models;

public sealed record VirtualTagConfig
{
    public string EpcHex { get; init; } = "E28011606000020485984444";
    public string TidHex { get; init; } = "E280116020007001";
    public ushort AntennaId { get; init; } = 1;
    public short PeakRssi { get; init; } = -42;
    public string UserMemoryHex { get; init; } = "00000000";
    public string AccessPasswordHex { get; init; } = "00000000";
    public string KillPasswordHex { get; init; } = "00000000";
}

public sealed class VirtualDeviceInstanceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Virtual-Reader-1";
    public string ListenAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5084;
    public string ProtocolVersion { get; set; } = "1.0.1";
    public string DeviceProfile { get; set; } = "Standard";
    public ushort MaxAntennas { get; set; } = 4;
    public VirtualRfScenario Scenario { get; set; } = VirtualRfScenario.Static;
    public double DetectionProbability { get; set; } = 1.0;
    public int RssiJitterDb { get; set; } = 2;
    public int PresenceCycleRounds { get; set; } = 3;
    public bool AllowImplicitStopOnDisable { get; set; } = true;
    public bool AutoStart { get; set; }
    public List<VirtualTagConfig> Tags { get; set; } = [];

    public static VirtualDeviceInstanceConfig CreateDefault(int port = 5084, string name = "Virtual-Reader-1")
    {
        return new VirtualDeviceInstanceConfig
        {
            Port = port,
            Name = name,
            Tags =
            [
                new VirtualTagConfig { EpcHex = "E28011606000020485981001", TidHex = "E280116020007001", AntennaId = 1, PeakRssi = -38 },
                new VirtualTagConfig { EpcHex = "E28011606000020485981002", TidHex = "E280116020007002", AntennaId = 1, PeakRssi = -42 },
                new VirtualTagConfig { EpcHex = "E28011606000020485981003", TidHex = "E280116020007003", AntennaId = 2, PeakRssi = -45 },
                new VirtualTagConfig { EpcHex = "E28011606000020485981004", TidHex = "E280116020007004", AntennaId = 2, PeakRssi = -50 },
                new VirtualTagConfig { EpcHex = "E28011606000020485981005", TidHex = "E280116020007005", AntennaId = 3, PeakRssi = -55 },
            ]
        };
    }
}

public sealed class ObservedMessageItem
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public bool Incoming { get; init; }
    public string Direction => Incoming ? "Rx" : "Tx";
    public string ProtocolVersion { get; init; } = "1.0.1";
    public ushort MessageType { get; init; }
    public string MessageName { get; init; } = string.Empty;
    public uint MessageId { get; init; }
    public string? Detail { get; init; }

    public static string ResolveMessageName(ushort type) => type switch
    {
        1 => "GET_READER_CAPABILITIES",
        11 => "GET_READER_CAPABILITIES_RESPONSE",
        2 => "SET_READER_CONFIG",
        12 => "SET_READER_CONFIG_RESPONSE",
        3 => "GET_READER_CONFIG",
        13 => "GET_READER_CONFIG_RESPONSE",
        20 => "ADD_ROSPEC",
        30 => "ADD_ROSPEC_RESPONSE",
        21 => "DELETE_ROSPEC",
        31 => "DELETE_ROSPEC_RESPONSE",
        22 => "START_ROSPEC",
        32 => "START_ROSPEC_RESPONSE",
        23 => "STOP_ROSPEC",
        33 => "STOP_ROSPEC_RESPONSE",
        24 => "ENABLE_ROSPEC",
        34 => "ENABLE_ROSPEC_RESPONSE",
        25 => "DISABLE_ROSPEC",
        35 => "DISABLE_ROSPEC_RESPONSE",
        26 => "GET_ROSPECS",
        36 => "GET_ROSPECS_RESPONSE",
        40 => "ADD_ACCESSSPEC",
        50 => "ADD_ACCESSSPEC_RESPONSE",
        41 => "DELETE_ACCESSSPEC",
        51 => "DELETE_ACCESSSPEC_RESPONSE",
        42 => "ENABLE_ACCESSSPEC",
        52 => "ENABLE_ACCESSSPEC_RESPONSE",
        43 => "DISABLE_ACCESSSPEC",
        53 => "DISABLE_ACCESSSPEC_RESPONSE",
        44 => "GET_ACCESSSPECS",
        54 => "GET_ACCESSSPECS_RESPONSE",
        61 => "RO_ACCESS_REPORT",
        62 => "KEEPALIVE",
        72 => "KEEPALIVE_ACK",
        63 => "READER_EVENT_NOTIFICATION",
        64 => "ENABLE_EVENTS_AND_REPORTS",
        65 => "ERROR_MESSAGE",
        1023 => "CUSTOM_MESSAGE",
        _ => $"MSG_{type}",
    };
}

public sealed class ClientConnectionItem
{
    public string ConnectionId { get; set; } = string.Empty;
    public string RemoteEndPoint { get; set; } = string.Empty;
    public DateTimeOffset ConnectedAt { get; set; }
    public string NegotiatedVersion { get; set; } = "1.0.1";
    public bool IsConnected { get; set; } = true;
}
