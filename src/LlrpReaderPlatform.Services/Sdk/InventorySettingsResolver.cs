using LlrpSdk;

namespace LlrpReaderPlatform.Services.Sdk;

/// <summary>
/// Resolves the inventory settings used by a new managed ROSpec.
/// </summary>
/// <remarks>
/// GET_READER_CONFIG exposes the reader-wide antenna RF values, while the SDK-managed
/// ROSpec exposes the inventory-specific values. A reader can legitimately have no
/// managed ROSpec yet, so the former must be projected into the latter before starting
/// inventory. This keeps device-reported power/sensitivity values as indexes; no dBm
/// conversion is performed here.
/// </remarks>
internal static class InventorySettingsResolver
{
    public static InventorySettings Resolve(ReaderSettingsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        InventorySettings baseline = snapshot.ManagedRoSpec?.Inventory
            ?? snapshot.Settings.Inventory
            ?? new InventorySettings();

        InventorySettings resolved = baseline.AntennaConfigurations.Count > 0
            ? baseline
            : ProjectReaderAntennaConfiguration(baseline, snapshot.Settings.Configuration);

        return ExpandAllAntennas(resolved, snapshot.Settings.Configuration);
    }

    private static InventorySettings ProjectReaderAntennaConfiguration(
        InventorySettings baseline,
        ReaderConfiguration configuration)
    {
        IReadOnlyList<AntennaConfigurationSettings> source = configuration.Antennas ?? [];
        ushort[] availableAntennaIds = source
            .Select(static antenna => antenna.AntennaId)
            .Where(static antennaId => antennaId > 0)
            .Distinct()
            .OrderBy(static antennaId => antennaId)
            .ToArray();

        if (availableAntennaIds.Length == 0)
        {
            return baseline;
        }

        bool selectsAllAntennas = baseline.AntennaIds.Count == 0
            || (baseline.AntennaIds.Count == 1 && baseline.AntennaIds[0] == 0);
        ushort[] selectedAntennaIds = selectsAllAntennas
            ? availableAntennaIds
            : baseline.AntennaIds
                .Where(availableAntennaIds.Contains)
                .Distinct()
                .OrderBy(static antennaId => antennaId)
                .ToArray();

        if (selectedAntennaIds.Length == 0)
        {
            return baseline;
        }

        AntennaConfigurationSettings[] rfSources = source
            .Where(antenna =>
                selectsAllAntennas || selectedAntennaIds.Contains(antenna.AntennaId))
            .Where(static antenna =>
                antenna.TransmitPowerIndex.HasValue
                || antenna.ReceiverSensitivityIndex.HasValue
                || antenna.HopTableId.HasValue
                || antenna.ChannelIndex.HasValue)
            .ToArray();

        InventoryAntennaConfiguration[] antennaConfigurations = rfSources
            .Select(ToInventoryAntennaConfiguration)
            .ToArray();

        return baseline with
        {
            AntennaIds = selectedAntennaIds,
            AntennaConfigurations = antennaConfigurations,
        };
    }

    private static InventorySettings ExpandAllAntennas(
        InventorySettings baseline,
        ReaderConfiguration configuration)
    {
        ushort[] availableAntennaIds = (configuration.Antennas ?? [])
            .Select(static antenna => antenna.AntennaId)
            .Where(static antennaId => antennaId > 0)
            .Distinct()
            .OrderBy(static antennaId => antennaId)
            .ToArray();

        bool selectsAllAntennas = baseline.AntennaIds.Count == 0
            || baseline.AntennaIds.Contains((ushort)0);
        ushort[] selectedAntennaIds = selectsAllAntennas
            ? availableAntennaIds
            : baseline.AntennaIds
                .Where(static antennaId => antennaId > 0)
                .Distinct()
                .ToArray();
        if (selectedAntennaIds.Length == 0)
        {
            return baseline;
        }

        InventoryAntennaConfiguration? commonConfiguration = baseline.AntennaConfigurations
            .FirstOrDefault(static antenna => antenna.AntennaId == 0);
        Dictionary<ushort, InventoryAntennaConfiguration> explicitConfigurations = baseline.AntennaConfigurations
            .Where(static antenna => antenna.AntennaId > 0)
            .Where(antenna => selectedAntennaIds.Contains(antenna.AntennaId))
            .GroupBy(static antenna => antenna.AntennaId)
            .ToDictionary(static group => group.Key, static group => group.First());

        InventoryAntennaConfiguration[] configurations = selectedAntennaIds
            .Select(antennaId => explicitConfigurations.TryGetValue(antennaId, out InventoryAntennaConfiguration? explicitConfiguration)
                ? explicitConfiguration
                : commonConfiguration is null
                    ? null
                    : commonConfiguration with { AntennaId = antennaId })
            .Where(static antenna => antenna is not null)
            .Select(static antenna => antenna!)
            .ToArray();

        return baseline with
        {
            AntennaIds = selectedAntennaIds,
            AntennaConfigurations = configurations,
        };
    }

    private static InventoryAntennaConfiguration ToInventoryAntennaConfiguration(
        AntennaConfigurationSettings source)
    {
        bool hasCompleteTransmitter = source.TransmitPowerIndex.HasValue
            && source.HopTableId.HasValue
            && source.ChannelIndex.HasValue;

        return new InventoryAntennaConfiguration
        {
            AntennaId = source.AntennaId,
            ReceiverSensitivityIndex = source.ReceiverSensitivityIndex,
            TransmitPowerIndex = hasCompleteTransmitter ? source.TransmitPowerIndex : null,
            HopTableId = hasCompleteTransmitter ? source.HopTableId : null,
            ChannelIndex = hasCompleteTransmitter ? source.ChannelIndex : null,
        };
    }
}
