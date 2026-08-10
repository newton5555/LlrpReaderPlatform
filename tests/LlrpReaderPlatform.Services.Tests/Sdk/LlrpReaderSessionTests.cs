using LlrpReaderPlatform.Services.Sdk;
using LlrpSdk;
using Xunit;

namespace LlrpReaderPlatform.Services.Tests.Sdk;

public sealed class LlrpReaderSessionTests
{
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
