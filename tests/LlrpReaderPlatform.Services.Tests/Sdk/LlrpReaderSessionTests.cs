using LlrpReaderPlatform.Services.Sdk;
using LlrpSdk;
using Xunit;

namespace LlrpReaderPlatform.Services.Tests.Sdk;

public sealed class LlrpReaderSessionTests
{
    [Fact]
    public void InventorySettingsResolver_projects_reader_antenna_configuration_without_inventory()
    {
        var snapshot = new ReaderSettingsSnapshot(
            new ReaderSettings
            {
                Configuration = new ReaderConfiguration
                {
                    Antennas =
                    [
                        new AntennaConfigurationSettings
                        {
                            AntennaId = 1,
                            TransmitPowerIndex = 7,
                            HopTableId = 1,
                            ReceiverSensitivityIndex = 0,
                            ChannelIndex = 1,
                        },
                    ],
                },
            },
            ManagedRoSpec: null);

        InventorySettings resolved = InventorySettingsResolver.Resolve(snapshot);

        Assert.Equal(new ushort[] { 1 }, resolved.AntennaIds);
        InventoryAntennaConfiguration configuration = Assert.Single(resolved.AntennaConfigurations);
        Assert.Equal((ushort)1, configuration.AntennaId);
        Assert.Equal((ushort)7, configuration.TransmitPowerIndex);
        Assert.Equal((ushort)1, configuration.HopTableId);
        Assert.Equal((ushort)0, configuration.ReceiverSensitivityIndex);
        Assert.Equal((ushort)1, configuration.ChannelIndex);
    }

    [Fact]
    public void InventorySettingsResolver_expands_all_antennas_from_managed_rospec()
    {
        var snapshot = new ReaderSettingsSnapshot(
            new ReaderSettings
            {
                Configuration = new ReaderConfiguration
                {
                    Antennas =
                    [
                        new AntennaConfigurationSettings { AntennaId = 1 },
                        new AntennaConfigurationSettings { AntennaId = 2 },
                    ],
                },
            },
            new ManagedRoSpecSnapshot(
                new InventorySettings
                {
                    AntennaIds = [0],
                    AntennaConfigurations =
                    [
                        new InventoryAntennaConfiguration
                        {
                            AntennaId = 0,
                            ReceiverSensitivityIndex = 0,
                            TransmitPowerIndex = 192,
                            HopTableId = 1,
                            ChannelIndex = 1,
                        },
                    ],
                },
                InventoryRuntimeState.Disabled));

        InventorySettings resolved = InventorySettingsResolver.Resolve(snapshot);

        Assert.Equal(new ushort[] { 1, 2 }, resolved.AntennaIds);
        Assert.Equal(new ushort[] { 1, 2 }, resolved.AntennaConfigurations.Select(static antenna => antenna.AntennaId));
        Assert.All(resolved.AntennaConfigurations, antenna =>
        {
            Assert.Equal((ushort)192, antenna.TransmitPowerIndex);
            Assert.Equal((ushort)1, antenna.HopTableId);
            Assert.Equal((ushort)1, antenna.ChannelIndex);
        });
    }

    [Fact]
    public void InventorySettingsResolver_expands_common_rf_configuration_for_explicit_antennas()
    {
        var snapshot = new ReaderSettingsSnapshot(
            new ReaderSettings
            {
                Configuration = new ReaderConfiguration
                {
                    Antennas =
                    [
                        new AntennaConfigurationSettings { AntennaId = 1 },
                        new AntennaConfigurationSettings { AntennaId = 2 },
                    ],
                },
            },
            new ManagedRoSpecSnapshot(
                new InventorySettings
                {
                    AntennaIds = [1, 2],
                    AntennaConfigurations =
                    [
                        new InventoryAntennaConfiguration
                        {
                            AntennaId = 0,
                            TransmitPowerIndex = 20,
                            HopTableId = 1,
                            ChannelIndex = 1,
                        },
                    ],
                },
                InventoryRuntimeState.Disabled));

        InventorySettings resolved = InventorySettingsResolver.Resolve(snapshot);

        Assert.Equal(new ushort[] { 1, 2 }, resolved.AntennaIds);
        Assert.Equal(new ushort[] { 1, 2 }, resolved.AntennaConfigurations.Select(static antenna => antenna.AntennaId));
        Assert.All(resolved.AntennaConfigurations, antenna => Assert.Equal((ushort)20, antenna.TransmitPowerIndex));
    }

    [Fact]
    public void MaterializeTagAccessSettings_prefers_managed_inventory_over_defaults()
    {
        var managedInventory = new InventorySettings { Session = 3 };
        var current = new ReaderSettings
        {
            Configuration = new ReaderConfiguration
            {
                Events = new EventNotificationConfiguration { GpiEventEnabled = true },
            },
        };
        var snapshot = new ReaderSettingsSnapshot(
            current,
            new ManagedRoSpecSnapshot(managedInventory, InventoryRuntimeState.Disabled));
        var fallback = new ReaderSettings { Inventory = new InventorySettings { Session = 0 } };

        ReaderSettings materialized = LlrpReaderSession.MaterializeTagAccessSettings(snapshot, fallback);

        Assert.Same(managedInventory, materialized.Inventory);
        Assert.True(materialized.Configuration.Events.GpiEventEnabled);
    }

    [Fact]
    public void MaterializeTagAccessSettings_uses_default_inventory_without_resetting_current_configuration()
    {
        var snapshot = new ReaderSettingsSnapshot(
            new ReaderSettings
            {
                Configuration = new ReaderConfiguration
                {
                    Events = new EventNotificationConfiguration { GpiEventEnabled = true },
                },
            },
            ManagedRoSpec: null);
        var fallback = new ReaderSettings { Inventory = new InventorySettings { Session = 1 } };

        ReaderSettings materialized = LlrpReaderSession.MaterializeTagAccessSettings(snapshot, fallback);

        Assert.Equal((byte)1, materialized.Inventory?.Session);
        Assert.True(materialized.Configuration.Events.GpiEventEnabled);
    }

    [Fact]
    public void MaterializeTagAccessSettings_accepts_missing_fallback_when_current_inventory_exists()
    {
        var current = new ReaderSettings
        {
            Inventory = new InventorySettings { Session = 2 },
        };
        var snapshot = new ReaderSettingsSnapshot(current, ManagedRoSpec: null);

        ReaderSettings materialized = LlrpReaderSession.MaterializeTagAccessSettings(snapshot, fallbackSettings: null);

        Assert.Same(current, materialized);
    }
}
