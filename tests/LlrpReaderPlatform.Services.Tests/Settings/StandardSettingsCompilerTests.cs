using System.Reflection;
using System.Runtime.CompilerServices;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Services.Settings;
using LlrpNet.Protocol.Messages;
using LlrpNet.Protocol.Parameters;
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
        draft.Values[SettingsKeys.TxPowerIndex] = (ushort)22;

        CompiledSettings compiled = compiler.Compile(draft, layout);

        Assert.Equal((ushort)2, compiled.AntennaId);
        Assert.Equal(3, compiled.Session);
        Assert.Equal((ushort)22, compiled.TxPowerIndex);
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
            GpiCount = 1,
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
        Assert.Contains(layout.Entries, static e => e.Key == SettingsKeys.AntennaTxPowerIndex(2));
        Assert.Equal(new SettingsRange(1, 1),
            Assert.Single(layout.Entries, e => e.Key == SettingsKeys.StartGpiPort).Range);
        Assert.Equal(new SettingsRange(1, 1),
            Assert.Single(layout.Entries, e => e.Key == SettingsKeys.StopGpiPort).Range);

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
    public void Runtime_layout_keeps_reader_current_rf_mode_when_capability_table_omits_it()
    {
        var compiler = new StandardSettingsCompiler();
        var capabilities = (ReaderCapabilities)Activator.CreateInstance(
            typeof(ReaderCapabilities),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                (ushort)4,
                false,
                false,
                Array.Empty<ILlrpParameter>(),
                (ILlrpMessage)RuntimeHelpers.GetUninitializedObject(
                    typeof(LlrpNet.Protocol.Messages.V1_0_1.GET_READER_CAPABILITIES_RESPONSE)),
                Array.Empty<ILlrpParameter>(),
                Array.Empty<TxPowerEntry>(),
                Array.Empty<RxSensitivityEntry>(),
                Array.Empty<uint>(),
                Array.Empty<FrequencyHopTableEntry>(),
                new[] { new C1G2RfModeEntry(7, "", false, 0, "", "", 640_000, 1_500, 6_250, 25_000, 6_250) },
                true,
                false,
                false,
                false,
                false,
                false,
                null,
            ],
            culture: null)!;
        var runtime = new ReaderSettingsRuntimeSnapshot(
            new ReaderSettingsSnapshot(new ReaderSettings(), new ManagedRoSpecSnapshot(
                new InventorySettings { ModeIndex = 1000 }, InventoryRuntimeState.Disabled)),
            capabilities);

        EffectiveSettingsLayout layout = compiler.BuildLayout(Snapshot(), runtime);

        SettingsEntry rfMode = Assert.Single(layout.Entries, static entry => entry.Key == SettingsKeys.RfMode);
        Assert.Equal(EditorKind.Choice, rfMode.EditorKind);
        Assert.Contains(rfMode.Options, static option => Equals(option.Value, 7));
        Assert.Contains(rfMode.Options, static option => Equals(option.Value, 1000));
        Assert.Equal(
            "7 (M0/640K, Tari: 6.3 uS, PIE: 1.5)",
            Assert.Single(rfMode.Options, static option => Equals(option.Value, 7)).Display);
        Assert.Equal(1000, rfMode.CurrentValue);
    }

    [Fact]
    public void Runtime_layout_uses_capability_tables_as_indexed_power_choices()
    {
        var compiler = new StandardSettingsCompiler();
        ReaderRuntimeSnapshot snapshot = Snapshot(new ReaderAntennaInfo { AntennaId = 1, Name = "A1" });
        var inventory = new InventorySettings
        {
            AntennaIds = [1],
            AntennaConfigurations =
            [
                new InventoryAntennaConfiguration
                {
                    AntennaId = 1,
                    TransmitPowerIndex = 7,
                    ReceiverSensitivityIndex = 2,
                },
            ],
        };
        var capabilities = CreateCapabilities(
            txPowers: [new TxPowerEntry(3, 1000), new TxPowerEntry(7, 3050)],
            rxSensitivities: [new RxSensitivityEntry(1, 0), new RxSensitivityEntry(2, 6)],
            hopTables: [new FrequencyHopTableEntry(1, [902750, 903250])]);
        var runtime = new ReaderSettingsRuntimeSnapshot(
            new ReaderSettingsSnapshot(new ReaderSettings(), new ManagedRoSpecSnapshot(
                inventory, InventoryRuntimeState.Disabled)),
            capabilities);

        EffectiveSettingsLayout layout = compiler.BuildLayout(snapshot, runtime);

        SettingsEntry tx = Assert.Single(layout.Entries, static entry => entry.Key == SettingsKeys.TxPowerIndex);
        Assert.Equal(EditorKind.Choice, tx.EditorKind);
        Assert.Equal((ushort)7, tx.CurrentValue);
        Assert.Equal(new object?[] { (ushort)3, (ushort)7 }, tx.Options.Select(static option => option.Value));
        Assert.Equal(new[] { "3 (10 dBm)", "7 (30.5 dBm)" }, tx.Options.Select(static option => option.Display));

        SettingsEntry rx = Assert.Single(layout.Entries, static entry => entry.Key == SettingsKeys.RxSensitivityIndex);
        Assert.Equal(EditorKind.Choice, rx.EditorKind);
        Assert.Equal((ushort)2, rx.CurrentValue);
        Assert.Equal(new object?[] { (ushort)1, (ushort)2 }, rx.Options.Select(static option => option.Value));
        Assert.Equal(new[] { "1 (0 dB offset)", "2 (6 dB offset)" }, rx.Options.Select(static option => option.Display));
    }

    [Fact]
    public void CompileSdk_writes_capability_table_indices_without_physical_value_conversion()
    {
        var compiler = new StandardSettingsCompiler();
        ReaderRuntimeSnapshot snapshot = Snapshot(
            new ReaderAntennaInfo { AntennaId = 1, Name = "A1" },
            new ReaderAntennaInfo { AntennaId = 2, Name = "A2" });
        var runtime = new ReaderSettingsRuntimeSnapshot(
            new ReaderSettingsSnapshot(new ReaderSettings(), new ManagedRoSpecSnapshot(
                new InventorySettings { AntennaIds = [1, 2] }, InventoryRuntimeState.Disabled)),
            CreateCapabilities(
                txPowers: [new TxPowerEntry(3, 1000), new TxPowerEntry(7, 3050)],
                rxSensitivities: [new RxSensitivityEntry(1, 0), new RxSensitivityEntry(2, 6)],
                hopTables: [new FrequencyHopTableEntry(1, [902750, 903250])]));
        EffectiveSettingsLayout layout = compiler.BuildLayout(snapshot, runtime);
        var draft = new SettingsDraft { ReaderId = ReaderId, CapabilityRevision = 7 };
        draft.Values[SettingsKeys.AntennaIds] = "1, 2";
        draft.Values[SettingsKeys.TxPowerIndex] = (ushort)3;
        draft.Values[SettingsKeys.RxSensitivityIndex] = (ushort)1;

        ReaderSettings compiled = compiler.CompileSdk(draft, layout, runtime, snapshot);

        Assert.Collection(
            compiled.Inventory!.AntennaConfigurations,
            configuration => AssertGlobalAntennaConfiguration(configuration, 1),
            configuration => AssertGlobalAntennaConfiguration(configuration, 2));
    }

    [Fact]
    public void CompileSdk_uses_advertised_minimum_tari_when_selected_mode_has_no_tari_value()
    {
        var compiler = new StandardSettingsCompiler();
        ReaderRuntimeSnapshot snapshot = Snapshot(new ReaderAntennaInfo { AntennaId = 1, Name = "A1" });
        var runtime = new ReaderSettingsRuntimeSnapshot(
            new ReaderSettingsSnapshot(
                new ReaderSettings(),
                new ManagedRoSpecSnapshot(
                    new InventorySettings { AntennaIds = [1], ModeIndex = 20, Tari = 0 },
                    InventoryRuntimeState.Disabled)),
            CreateCapabilities(
                rfModes:
                [
                    new C1G2RfModeEntry(20, "DRV_64_3", true, 2, "PR_ASK", "DI", 64_000, 2_000, 12_500, 23_000, 2_100),
                ]));
        EffectiveSettingsLayout layout = compiler.BuildLayout(snapshot, runtime);
        var draft = new SettingsDraft { ReaderId = ReaderId, CapabilityRevision = 7 };
        draft.Values[SettingsKeys.AntennaIds] = "1";
        draft.Values[SettingsKeys.RfMode] = 20;
        draft.Values[SettingsKeys.Tari] = 0;

        ReaderSettings compiled = compiler.CompileSdk(draft, layout, runtime, snapshot);

        Assert.Equal((ushort)20, compiled.Inventory!.ModeIndex);
        Assert.Equal((ushort)12_500, compiled.Inventory.Tari);
        Assert.Equal(
            12_500,
            Assert.IsType<int>(Assert.Single(layout.Entries, static entry => entry.Key == SettingsKeys.Tari).CurrentValue));
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

        draft.Values[SettingsKeys.FilterBitLength(1)] = 24;
        Assert.Throws<FormatException>(() => compiler.CompileSdk(draft, layout, runtime, snapshot));
    }

    [Fact]
    public void CompileSdk_rejects_empty_antenna_selection()
    {
        var compiler = new StandardSettingsCompiler();
        ReaderRuntimeSnapshot snapshot = Snapshot(new ReaderAntennaInfo { AntennaId = 1, Name = "A1" });
        var runtime = new ReaderSettingsRuntimeSnapshot(
            new ReaderSettingsSnapshot(new ReaderSettings(), new ManagedRoSpecSnapshot(
                new InventorySettings { AntennaIds = [1] }, InventoryRuntimeState.Disabled)),
            Capabilities: null);
        EffectiveSettingsLayout layout = compiler.BuildLayout(snapshot, runtime);
        var draft = new SettingsDraft { ReaderId = ReaderId, CapabilityRevision = 7 };
        foreach (SettingsEntry entry in layout.Entries.Where(static entry => !entry.IsReadOnly))
        {
            draft.Values[entry.Key] = entry.CurrentValue;
        }

        draft.Values[SettingsKeys.AntennaIds] = string.Empty;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => compiler.CompileSdk(draft, layout, runtime, snapshot));

        Assert.Contains("explicit device antenna IDs", error.Message, StringComparison.Ordinal);
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
                        Antennas =
                        [
                            new AntennaConfigurationSettings
                            {
                                AntennaId = 1,
                                TransmitPowerIndex = 5,
                                ChannelIndex = 1,
                            },
                        ],
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
        Assert.Single(compiled.Configuration.Antennas);
        Assert.Equal((ushort)5, compiled.Configuration.Antennas[0].TransmitPowerIndex);
        Assert.Equal((ushort)1, compiled.Configuration.Antennas[0].ChannelIndex);
    }

    [Fact]
    public void CompileSdk_without_initial_rospec_keeps_reader_configuration_and_creates_inventory()
    {
        var compiler = new StandardSettingsCompiler();
        ReaderRuntimeSnapshot snapshot = Snapshot(new ReaderAntennaInfo { AntennaId = 1, Name = "A1" });
        var readerConfiguration = new ReaderConfiguration
        {
            Antennas =
            [
                new AntennaConfigurationSettings
                {
                    AntennaId = 1,
                    TransmitPowerIndex = 7,
                    ReceiverSensitivityIndex = 2,
                    HopTableId = 1,
                    ChannelIndex = 1,
                },
            ],
        };
        var runtime = new ReaderSettingsRuntimeSnapshot(
            new ReaderSettingsSnapshot(new ReaderSettings { Configuration = readerConfiguration }, ManagedRoSpec: null),
            CreateCapabilities(
                txPowers: [new TxPowerEntry(3, 1000), new TxPowerEntry(7, 3050)],
                rxSensitivities: [new RxSensitivityEntry(1, 0), new RxSensitivityEntry(2, 6)],
                hopTables: [new FrequencyHopTableEntry(1, [902750, 903250])]));
        EffectiveSettingsLayout layout = compiler.BuildLayout(snapshot, runtime);
        Assert.Equal("1", Assert.Single(layout.Entries, static entry => entry.Key == SettingsKeys.AntennaIds).CurrentValue);
        Assert.Equal((ushort)7, Assert.Single(layout.Entries, static entry => entry.Key == SettingsKeys.TxPowerIndex).CurrentValue);
        Assert.Equal((ushort)2, Assert.Single(layout.Entries, static entry => entry.Key == SettingsKeys.RxSensitivityIndex).CurrentValue);
        Assert.True(Assert.Single(layout.Entries, static entry => entry.Key == SettingsKeys.IndividualAntennaSettings).CurrentValue is true);
        var draft = new SettingsDraft { ReaderId = ReaderId, CapabilityRevision = 7 };
        draft.Values[SettingsKeys.AntennaIds] = "1";
        draft.Values[SettingsKeys.TxPowerIndex] = (ushort)7;
        draft.Values[SettingsKeys.RxSensitivityIndex] = (ushort)2;

        ReaderSettings compiled = compiler.CompileSdk(draft, layout, runtime, snapshot);

        Assert.Null(runtime.Settings.ManagedRoSpec);
        Assert.NotNull(compiled.Inventory);
        Assert.Equal(new ushort[] { 1 }, compiled.Inventory!.AntennaIds);
        Assert.Equal(readerConfiguration.Antennas, compiled.Configuration.Antennas);
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
                    HopTableId = 1,
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
    public void CompileSdk_completes_rf_transmitter_tuple_when_channel_is_missing()
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
                    HopTableId = 1,
                },
            ],
        };
        var runtime = new ReaderSettingsRuntimeSnapshot(
            new ReaderSettingsSnapshot(new ReaderSettings(), new ManagedRoSpecSnapshot(
                managedInventory, InventoryRuntimeState.Disabled)),
            Capabilities: null);
        EffectiveSettingsLayout layout = compiler.BuildLayout(snapshot, runtime);
        var draft = new SettingsDraft { ReaderId = ReaderId, CapabilityRevision = 7 };
        draft.Values[SettingsKeys.Session] = 0;

        ReaderSettings compiled = compiler.CompileSdk(draft, layout, runtime, snapshot);

        InventoryAntennaConfiguration configuration = Assert.Single(compiled.Inventory!.AntennaConfigurations);
        Assert.Equal((ushort)5, configuration.TransmitPowerIndex);
        Assert.Equal((ushort)1, configuration.HopTableId);
        Assert.Equal((ushort)1, configuration.ChannelIndex);
    }

    [Fact]
    public void Runtime_layout_and_compile_disable_gpi_when_capability_catalog_reports_no_gpi()
    {
        var compiler = new StandardSettingsCompiler();
        ReaderRuntimeSnapshot snapshot = Snapshot(new ReaderAntennaInfo { AntennaId = 1, Name = "A1" }) with
        {
            GpiCount = 0,
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
        Assert.True(Assert.Single(layout.Entries, e => e.Key == SettingsKeys.StartGpiPort).IsReadOnly);
        Assert.True(Assert.Single(layout.Entries, e => e.Key == SettingsKeys.StopGpiEnabled).IsReadOnly);
        Assert.True(Assert.Single(layout.Entries, e => e.Key == SettingsKeys.StopGpiPort).IsReadOnly);

        ReaderSettings compiled = compiler.CompileSdk(
            new SettingsDraft { ReaderId = ReaderId, CapabilityRevision = 7 },
            layout,
            runtime,
            snapshot);

        Assert.Equal(InventoryStartTriggerType.Immediate, compiled.Inventory?.StartTrigger.Type);
        Assert.Equal(InventoryStopTriggerType.None, compiled.Inventory?.StopTrigger.Type);
        Assert.False(compiled.Configuration.Events.GpiEventEnabled);
    }

    private static void AssertGlobalAntennaConfiguration(
        InventoryAntennaConfiguration configuration,
        ushort antennaId)
    {
        Assert.Equal(antennaId, configuration.AntennaId);
        Assert.Equal((ushort)3, configuration.TransmitPowerIndex);
        Assert.Equal((ushort)1, configuration.ReceiverSensitivityIndex);
        Assert.Equal((ushort)1, configuration.HopTableId);
        Assert.Equal((ushort)1, configuration.ChannelIndex);
    }

    private static ReaderCapabilities CreateCapabilities(
        IEnumerable<TxPowerEntry>? txPowers = null,
        IEnumerable<RxSensitivityEntry>? rxSensitivities = null,
        IEnumerable<FrequencyHopTableEntry>? hopTables = null,
        IEnumerable<C1G2RfModeEntry>? rfModes = null)
    {
        return (ReaderCapabilities)Activator.CreateInstance(
            typeof(ReaderCapabilities),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                (ushort)4,
                true,
                false,
                Array.Empty<ILlrpParameter>(),
                (ILlrpMessage)RuntimeHelpers.GetUninitializedObject(
                    typeof(LlrpNet.Protocol.Messages.V1_0_1.GET_READER_CAPABILITIES_RESPONSE)),
                Array.Empty<ILlrpParameter>(),
                txPowers ?? Array.Empty<TxPowerEntry>(),
                rxSensitivities ?? Array.Empty<RxSensitivityEntry>(),
                Array.Empty<uint>(),
                hopTables ?? Array.Empty<FrequencyHopTableEntry>(),
                rfModes ?? Array.Empty<C1G2RfModeEntry>(),
                true,
                false,
                false,
                false,
                false,
                false,
                null,
            ],
            culture: null)!;
    }
}
