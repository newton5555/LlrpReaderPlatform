using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Services.Settings;
using LlrpSdk;
using Xunit;

namespace LlrpReaderPlatform.Services.Tests.Settings;

public sealed class StandardSettingsCompilerTests
{
    private static readonly Guid ReaderId = Guid.NewGuid();

    private static ReaderRuntimeSnapshot Snapshot(params ReaderAntennaInfo[] antennas) => new()
    {
        ReaderId = ReaderId,
        Profile = new ReaderProfile { Id = ReaderId, Host = "192.0.2.1" },
        State = ReaderState.Disconnected,
        CapabilityRevision = 7,
        Antennas = antennas,
    };

    [Fact]
    public void BuildLayout_drives_antenna_options_from_capability()
    {
        var compiler = new StandardSettingsCompiler();
        ReaderRuntimeSnapshot snapshot = Snapshot(
            new ReaderAntennaInfo { AntennaId = 1, Name = "A1" },
            new ReaderAntennaInfo { AntennaId = 2, Name = "A2" });

        EffectiveSettingsLayout layout = compiler.BuildLayout(snapshot);

        SettingsEntry antenna = Assert.Single(layout.Entries, static e => e.Key == SettingsKeys.Antenna);
        Assert.False(antenna.IsReadOnly);
        Assert.Equal(2, antenna.Options.Count);
    }

    [Fact]
    public void BuildLayout_marks_antenna_readonly_when_no_capability()
    {
        var compiler = new StandardSettingsCompiler();
        EffectiveSettingsLayout layout = compiler.BuildLayout(Snapshot());

        SettingsEntry antenna = Assert.Single(layout.Entries, static e => e.Key == SettingsKeys.Antenna);
        Assert.True(antenna.IsReadOnly);
        Assert.NotNull(antenna.ReadOnlyReason);
        Assert.True(layout.HasEditableSettings); // session / tx-power 仍可编辑
    }

    [Fact]
    public void BuildSnapshot_exposes_current_values()
    {
        var compiler = new StandardSettingsCompiler();
        SettingsSnapshot snapshot = compiler.BuildSnapshot(Snapshot(new ReaderAntennaInfo { AntennaId = 1 }));

        Assert.Equal(ReaderId, snapshot.ReaderId);
        Assert.Equal(7, snapshot.CapabilityRevision);
        Assert.Equal((ushort)1, snapshot.Values["antenna"]);
        Assert.Equal((int)0, snapshot.Values["session"]);
    }

    [Fact]
    public void Compile_maps_draft_to_compiled_settings()
    {
        var compiler = new StandardSettingsCompiler();
        EffectiveSettingsLayout layout = compiler.BuildLayout(Snapshot(new ReaderAntennaInfo { AntennaId = 2 }));
        var draft = new SettingsDraft { ReaderId = ReaderId, CapabilityRevision = 7 };
        draft.Values["antenna"] = (ushort)2;
        draft.Values["session"] = 3;
        draft.Values["tx-power-dbm"] = 22.5m;

        CompiledSettings compiled = compiler.Compile(draft, layout);

        Assert.Equal((ushort)2, compiled.AntennaId);
        Assert.Equal(3, compiled.Session);
        Assert.Equal(22.5m, compiled.TxPowerDbm);
    }

    [Fact]
    public void Runtime_layout_exposes_inventory_filters_antennas_triggers_and_report_fields()
    {
        var compiler = new StandardSettingsCompiler();
        ReaderRuntimeSnapshot snapshot = Snapshot(
            new ReaderAntennaInfo { AntennaId = 1, Name = "A1" },
            new ReaderAntennaInfo { AntennaId = 2, Name = "A2" });
        Feature feature = new("test-runtime-feature", "test-vendor");
        snapshot = snapshot with
        {
            FeatureCatalog = new ReaderFeatureCatalog { SupportedFeatures = [feature] },
        };
        var runtime = new ReaderSettingsRuntimeSnapshot(
            new ReaderSettingsSnapshot(new ReaderSettings(), new ManagedRoSpecSnapshot(
                new InventorySettings(), InventoryRuntimeState.Disabled)),
            Capabilities: null);

        EffectiveSettingsLayout layout = compiler.BuildLayout(snapshot, runtime);

        Assert.Contains(feature, layout.FeatureCatalog.SupportedFeatures);
        Assert.Contains(layout.Entries, static e => e.Key == SettingsKeys.AntennaIds);
        Assert.Contains(layout.Entries, static e => e.Key == SettingsKeys.FilterMask(1));
        Assert.Contains(layout.Entries, static e => e.Key == SettingsKeys.StartGpiPort);
        Assert.Contains(layout.Entries, static e => e.Key == SettingsKeys.ReportPcBits);
        Assert.Contains(layout.Entries, static e => e.Key == SettingsKeys.AntennaTxPowerDbm(2));

        SettingsEntry memoryBank = Assert.Single(layout.Entries, e => e.Key == SettingsKeys.FilterMemoryBank(1));
        Assert.Equal(
            new[] { "EPC", "TID", "User", "Reserved" },
            memoryBank.Options.Select(static option => option.Display));
    }

    [Fact]
    public void Runtime_layout_keeps_rf_mode_editable_when_reader_does_not_report_rf_modes()
    {
        var compiler = new StandardSettingsCompiler();
        var runtime = new ReaderSettingsRuntimeSnapshot(
            new ReaderSettingsSnapshot(new ReaderSettings(), new ManagedRoSpecSnapshot(
                new InventorySettings { ModeIndex = 3 }, InventoryRuntimeState.Disabled)),
            Capabilities: null);

        EffectiveSettingsLayout layout = compiler.BuildLayout(Snapshot(), runtime);

        SettingsEntry rfMode = Assert.Single(layout.Entries, static entry => entry.Key == SettingsKeys.RfMode);
        Assert.Equal(EditorKind.Integer, rfMode.EditorKind);
        Assert.False(rfMode.IsReadOnly);
        Assert.Empty(rfMode.Options);
        Assert.Equal(new SettingsRange(0, ushort.MaxValue), rfMode.Range);
        Assert.Equal(3, rfMode.CurrentValue);
    }

    [Fact]
    public void CompileSdk_maps_standard_filter_report_and_gpi_trigger_values()
    {
        var compiler = new StandardSettingsCompiler();
        ReaderRuntimeSnapshot snapshot = Snapshot(new ReaderAntennaInfo { AntennaId = 1, Name = "A1" });
        var runtime = new ReaderSettingsRuntimeSnapshot(
            new ReaderSettingsSnapshot(new ReaderSettings(), new ManagedRoSpecSnapshot(
                new InventorySettings(), InventoryRuntimeState.Disabled)),
            Capabilities: null);
        EffectiveSettingsLayout layout = compiler.BuildLayout(snapshot, runtime);
        var draft = new SettingsDraft { ReaderId = ReaderId, CapabilityRevision = 7 };
        foreach ((string key, object? value) in layout.Entries
                     .Where(static entry => !entry.IsReadOnly)
                     .ToDictionary(static entry => entry.Key, static entry => entry.CurrentValue))
        {
            draft.Values[key] = value;
        }

        draft.Values[SettingsKeys.AntennaIds] = "1, 2";
        draft.Values[SettingsKeys.FilterEnabled(1)] = true;
        draft.Values[SettingsKeys.FilterMemoryBank(1)] = 1;
        draft.Values[SettingsKeys.FilterOffset(1)] = 32;
        draft.Values[SettingsKeys.FilterBitLength(1)] = 16;
        draft.Values[SettingsKeys.FilterMask(1)] = "0x30:08";
        draft.Values[SettingsKeys.FilterMatchAction(1)] = 1;
        draft.Values[SettingsKeys.FilterNonMatchAction(1)] = 2;
        draft.Values[SettingsKeys.StartGpiEnabled] = true;
        draft.Values[SettingsKeys.StartGpiPort] = 2;
        draft.Values[SettingsKeys.StartGpiLevel] = true;
        draft.Values[SettingsKeys.StopGpiEnabled] = true;
        draft.Values[SettingsKeys.StopGpiPort] = 3;
        draft.Values[SettingsKeys.StopGpiLevel] = false;
        draft.Values[SettingsKeys.StopGpiTimeoutMs] = 1500;
        draft.Values[SettingsKeys.ReportPcBits] = true;
        draft.Values[SettingsKeys.ReportEvery] = 2;

        ReaderSettings compiled = compiler.CompileSdk(draft, layout, runtime, snapshot);
        InventorySettings inventory = compiled.Inventory!;

        Assert.Equal(new ushort[] { 1, 2 }, inventory.AntennaIds);
        InventorySelectFilter filter = Assert.Single(inventory.Filters);
        Assert.Equal("3008", Convert.ToHexString(filter.Mask.Span));
        Assert.True(inventory.Report.IncludePcBits);
        Assert.Equal((ushort)2, inventory.ReportEveryNTags);
        Assert.Equal(InventoryReportTrigger.UponNTagsOrEndOfAiSpec, inventory.Report.Trigger);
        Assert.Equal(InventoryStartTriggerType.Gpi, inventory.StartTrigger.Type);
        Assert.Equal((ushort)2, inventory.StartTrigger.GpiPortNumber);
        Assert.Equal(InventoryStopTriggerType.GpiWithTimeout, inventory.StopTrigger.Type);
        Assert.Equal((ushort)3, inventory.StopTrigger.GpiPortNumber);
        Assert.False(inventory.StopTrigger.GpiState);
        Assert.Equal((uint)1500, inventory.StopTrigger.TimeoutMilliseconds);
        Assert.True(compiled.Configuration.Events.GpiEventEnabled);
    }

    [Fact]
    public void CompileSdk_enables_gpi_events_without_resetting_other_event_flags()
    {
        var compiler = new StandardSettingsCompiler();
        ReaderRuntimeSnapshot snapshot = Snapshot(new ReaderAntennaInfo { AntennaId = 1, Name = "A1" });
        var runtime = new ReaderSettingsRuntimeSnapshot(
            new ReaderSettingsSnapshot(
                new ReaderSettings
                {
                    Configuration = new ReaderConfiguration
                    {
                        Events = new EventNotificationConfiguration
                        {
                            AntennaEventEnabled = true,
                            ReaderExceptionEventEnabled = true,
                        },
                    },
                },
                new ManagedRoSpecSnapshot(new InventorySettings(), InventoryRuntimeState.Disabled)),
            Capabilities: null);
        EffectiveSettingsLayout layout = compiler.BuildLayout(snapshot, runtime);
        var draft = new SettingsDraft { ReaderId = ReaderId, CapabilityRevision = 7 };
        foreach (SettingsEntry entry in layout.Entries.Where(static entry => !entry.IsReadOnly))
        {
            draft.Values[entry.Key] = entry.CurrentValue;
        }

        draft.Values[SettingsKeys.StartGpiEnabled] = true;

        ReaderSettings compiled = compiler.CompileSdk(draft, layout, runtime, snapshot);

        Assert.True(compiled.Configuration.Events.GpiEventEnabled);
        Assert.True(compiled.Configuration.Events.AntennaEventEnabled);
        Assert.True(compiled.Configuration.Events.ReaderExceptionEventEnabled);
    }

    [Fact]
    public void CompileSdk_uses_managed_inventory_as_apply_baseline()
    {
        var compiler = new StandardSettingsCompiler();
        ReaderRuntimeSnapshot snapshot = Snapshot(new ReaderAntennaInfo { AntennaId = 1, Name = "A1" });
        var managedInventory = new InventorySettings
        {
            AntennaIds = [1],
            AntennaConfigurations =
            [
                new InventoryAntennaConfiguration
                {
                    AntennaId = 0,
                    TransmitPowerIndex = 5,
                    ReceiverSensitivityIndex = 7,
                },
            ],
        };
        var runtime = new ReaderSettingsRuntimeSnapshot(
            new ReaderSettingsSnapshot(new ReaderSettings(), new ManagedRoSpecSnapshot(
                managedInventory, InventoryRuntimeState.Disabled)),
            Capabilities: null);
        EffectiveSettingsLayout layout = compiler.BuildLayout(snapshot, runtime);
        var draft = new SettingsDraft { ReaderId = ReaderId, CapabilityRevision = 7 };
        draft.Values[SettingsKeys.Session] = 2;

        ReaderSettings compiled = compiler.CompileSdk(draft, layout, runtime, snapshot);
        InventorySettings inventory = compiled.Inventory!;

        Assert.Equal((byte)2, inventory.Session);
        Assert.Equal(new ushort[] { 1 }, inventory.AntennaIds);
        InventoryAntennaConfiguration configuration = Assert.Single(inventory.AntennaConfigurations);
        Assert.Equal((ushort)5, configuration.TransmitPowerIndex);
        Assert.Equal((ushort)7, configuration.ReceiverSensitivityIndex);
    }

    [Fact]
    public void Runtime_layout_and_compile_disable_gpi_when_capability_catalog_reports_no_gpi()
    {
        var compiler = new StandardSettingsCompiler();
        ReaderRuntimeSnapshot snapshot = Snapshot() with
        {
            FeatureCatalog = new ReaderFeatureCatalog
            {
                SupportedFeatures =
                [
                    ReaderFeatures.StandardSettings,
                    ReaderFeatures.StandardInventory,
                ],
            },
        };
        var inventory = new InventorySettings
        {
            StartTrigger = new InventoryStartTrigger
            {
                Type = InventoryStartTriggerType.Gpi,
                GpiPortNumber = 1,
                GpiState = true,
            },
            StopTrigger = new InventoryStopTrigger
            {
                Type = InventoryStopTriggerType.GpiWithTimeout,
                GpiPortNumber = 1,
                GpiState = false,
                TimeoutMilliseconds = 1000,
            },
        };
        var runtime = new ReaderSettingsRuntimeSnapshot(
            new ReaderSettingsSnapshot(
                new ReaderSettings
                {
                    Configuration = new ReaderConfiguration
                    {
                        Events = new EventNotificationConfiguration
                        {
                            GpiEventEnabled = true,
                        },
                    },
                },
                new ManagedRoSpecSnapshot(inventory, InventoryRuntimeState.Disabled)),
            Capabilities: null);

        EffectiveSettingsLayout layout = compiler.BuildLayout(snapshot, runtime);
        Assert.True(Assert.Single(layout.Entries, e => e.Key == SettingsKeys.StartGpiEnabled).IsReadOnly);
        Assert.True(Assert.Single(layout.Entries, e => e.Key == SettingsKeys.StopGpiEnabled).IsReadOnly);

        ReaderSettings compiled = compiler.CompileSdk(
            new SettingsDraft { ReaderId = ReaderId, CapabilityRevision = 7 },
            layout,
            runtime,
            snapshot);

        Assert.Equal(InventoryStartTriggerType.Immediate, compiled.Inventory?.StartTrigger.Type);
        Assert.Equal(InventoryStopTriggerType.None, compiled.Inventory?.StopTrigger.Type);
        Assert.False(compiled.Configuration.Events.GpiEventEnabled);
    }
}
