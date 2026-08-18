using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LlrpDevice.Virtual;
using LlrpVirtualDevice.App.Wpf.Models;

namespace LlrpVirtualDevice.App.Wpf.ViewModels;

public sealed partial class AddDeviceViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = "Virtual-Reader-1";

    [ObservableProperty]
    private string _listenAddress = "127.0.0.1";

    [ObservableProperty]
    private int _port = 5084;

    [ObservableProperty]
    private string _protocolVersion = "1.0.1";

    [ObservableProperty]
    private string _deviceProfile = "Impinj-R420";

    [ObservableProperty]
    private ushort _maxAntennas = 4;

    [ObservableProperty]
    private VirtualRfScenario _scenario = VirtualRfScenario.Static;

    [ObservableProperty]
    private double _detectionProbability = 1.0;

    [ObservableProperty]
    private int _rssiJitterDb = 2;

    [ObservableProperty]
    private int _presenceCycleRounds = 3;

    public IReadOnlyList<string> AvailableProfiles { get; } = ["Standard", "Impinj-R420", "Zebra-FX9600"];
    public IReadOnlyList<string> AvailableProtocolVersions { get; } = ["1.0.1", "1.1", "2.0"];
    public IReadOnlyList<VirtualRfScenario> AvailableScenarios { get; } = [VirtualRfScenario.Static, VirtualRfScenario.MovingTags, VirtualRfScenario.Noisy];

    public event Action<VirtualDeviceInstanceConfig?>? RequestClose;

    public void LoadFrom(VirtualDeviceInstanceConfig config)
    {
        Name = config.Name;
        ListenAddress = config.ListenAddress;
        Port = config.Port;
        ProtocolVersion = config.ProtocolVersion;
        DeviceProfile = config.DeviceProfile;
        MaxAntennas = config.MaxAntennas;
        Scenario = config.Scenario;
        DetectionProbability = config.DetectionProbability;
        RssiJitterDb = config.RssiJitterDb;
        PresenceCycleRounds = config.PresenceCycleRounds;
    }

    [RelayCommand]
    private void Save()
    {
        var config = new VirtualDeviceInstanceConfig
        {
            Name = Name,
            ListenAddress = ListenAddress,
            Port = Port,
            ProtocolVersion = ProtocolVersion,
            DeviceProfile = DeviceProfile,
            MaxAntennas = MaxAntennas,
            Scenario = Scenario,
            DetectionProbability = DetectionProbability,
            RssiJitterDb = RssiJitterDb,
            PresenceCycleRounds = PresenceCycleRounds,
            Tags =
            [
                new VirtualTagConfig { EpcHex = "E28011606000020485981001", TidHex = "E280116020007001", AntennaId = 1, PeakRssi = -38 },
                new VirtualTagConfig { EpcHex = "E28011606000020485981002", TidHex = "E280116020007002", AntennaId = 1, PeakRssi = -42 },
                new VirtualTagConfig { EpcHex = "E28011606000020485981003", TidHex = "E280116020007003", AntennaId = 2, PeakRssi = -45 },
            ]
        };

        RequestClose?.Invoke(config);
    }

    [RelayCommand]
    private void Cancel()
    {
        RequestClose?.Invoke(null);
    }
}
