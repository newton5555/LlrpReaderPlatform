using LlrpReaderPlatform.Extensions.Impinj;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Contracts.Tagging;
using LlrpReaderPlatform.Services.Extensions;
using LlrpReaderPlatform.Services.Settings;
using LlrpNet.Protocol.Impinj.Enumerations.V1_0_1;
using LlrpSdk;
using LlrpSdk.Extensions.Impinj;
using Xunit;

namespace LlrpReaderPlatform.Extensions.Impinj.Tests;

public sealed class ImpinjReaderExtensionModuleTests
{
    [Fact]
    public void Id_is_impinj()
    {
        var module = new ImpinjReaderExtensionModule();
        Assert.Equal("impinj", module.Id);
        Assert.IsType<ImpinjSettingsContributor>(module.SettingsContributor);
    }

    [Fact]
    public void IsApplicable_matches_impinj_manufacturer()
    {
        var module = new ImpinjReaderExtensionModule();
        Assert.True(module.IsApplicable(new ReaderProbeInfo(
            ManufacturerId: ImpinjReaderExtensionModule.ImpinjManufacturerId, ModelId: null, Firmware: null, Model: null)));
    }

    [Fact]
    public void IsApplicable_rejects_other_manufacturer()
    {
        var module = new ImpinjReaderExtensionModule();
        Assert.False(module.IsApplicable(new ReaderProbeInfo(0xABCD, null, null, null)));
    }

    [Fact]
    public void GetFeatures_does_not_claim_r420_l4_for_another_impinj_model()
    {
        var module = new ImpinjReaderExtensionModule();

        Assert.Empty(module.GetFeatures(new ReaderProbeInfo(
            ImpinjReaderExtensionModule.ImpinjManufacturerId, 7000000, "6.4.1", "Other")));
    }

    [Fact]
    public void GetFeatures_contributes_stable_impinj_capabilities_only_when_matched()
    {
        var module = new ImpinjReaderExtensionModule();

        IReadOnlyList<Feature> features = module.GetFeatures(new ReaderProbeInfo(
            ImpinjReaderExtensionModule.ImpinjManufacturerId, 2001002, "6.4.1.240", "R420"));

        Assert.Contains(ReaderFeatures.ImpinjFastId, features);
        Assert.Contains(ReaderFeatures.ImpinjSearchMode, features);
        Assert.Contains(ReaderFeatures.ImpinjFixedFrequency, features);
        Assert.Contains(ReaderFeatures.ImpinjRfPhase, features);
        Assert.DoesNotContain(ReaderFeatures.ImpinjDoppler, features);
        Assert.DoesNotContain(ReaderFeatures.StandardInventory, features);
        Assert.Empty(module.GetFeatures(new ReaderProbeInfo(0xABCD, null, null, null)));
    }

    [Fact]
    public void GetFeatures_rejects_unverified_r420_firmware_profile()
    {
        var module = new ImpinjReaderExtensionModule();

        IReadOnlyList<Feature> features = module.GetFeatures(new ReaderProbeInfo(
            ImpinjReaderExtensionModule.ImpinjManufacturerId, 2001002, "5.0.0.0", "R420"));

        Assert.Empty(features);
    }

    [Fact]
    public void ConfigureBuilder_applies_impinj_extension_without_throwing()
    {
        var module = new ImpinjReaderExtensionModule();
        var builder = new LlrpReaderBuilder("192.0.2.1").WithPort(5084);

        // 应当能对标准 builder 配置 UseImpinj 而不抛异常。
        module.ConfigureBuilder(new ReaderBuilderContext(builder));
    }

    [Fact]
    public void ProjectTagReport_keeps_vendor_values_as_platform_strings()
    {
        var module = new ImpinjReaderExtensionModule();
        var timestamp = new TagTimestamp(100, 100);
        var report = new TagReport(
            ElectronicProductCode: new ReadOnlyMemory<byte>([0x30, 0x01]),
            RoSpecId: 1,
            SpecIndex: 0,
            InventoryParameterSpecId: 0,
            AntennaId: 1,
            PeakRssi: -42,
            ChannelIndex: 1,
            FirstSeen: timestamp,
            LastSeen: timestamp,
            SeenCount: 1,
            AccessSpecId: null,
            AccessOperationResults: null,
            Extensions: new Dictionary<string, object?>
            {
                ["impinj.peakRssi"] = -42,
                ["impinj.phaseAngle"] = 12.5m,
            },
            EpcBitLength: 16,
            PcBits: null);

        ReaderTagReportProjection projection = module.ProjectTagReport(report);

        Assert.Equal("-42", projection.Fields["impinj.peakRssi"]);
        Assert.Equal("12.5", projection.Fields["impinj.phaseAngle"]);
        Assert.Null(projection.TidHex);
    }

    [Fact]
    public void Settings_contributor_adds_vendor_fields_only_for_impinj_snapshot()
    {
        var contributor = new ImpinjSettingsContributor();
        Guid id = Guid.NewGuid();
        var reader = new ReaderRuntimeSnapshot
        {
            ReaderId = id,
            Profile = new ReaderProfile { Id = id, Host = "192.0.2.20" },
            State = ReaderState.Disconnected,
            ManufacturerId = ImpinjReaderExtensionModule.ImpinjManufacturerId,
            ModelId = ImpinjReaderExtensionModule.R420ModelId,
            CapabilityRevision = 1,
            GpiCount = 2,
            FeatureCatalog = R420Features(),
        };
        var runtime = new ReaderSettingsRuntimeSnapshot(
            new ReaderSettingsSnapshot(new ReaderSettings(), new ManagedRoSpecSnapshot(
                new InventorySettings(), InventoryRuntimeState.Disabled)),
            null);
        var entries = new List<SettingsEntry>();

        Assert.True(contributor.IsApplicable(reader));
        contributor.ContributeLayout(entries, reader, runtime);

        Assert.Contains(entries, entry => entry.Key == ImpinjSettingsContributor.FastId);
        Assert.All(entries, entry => Assert.Equal(SettingsSource.VendorExtension, entry.Source));
        Assert.Contains(entries, entry => entry.Key == ImpinjSettingsContributor.GpiDebounce(1));
        Assert.Contains(entries, entry => entry.Key == ImpinjSettingsContributor.GpiDebounce(2));
        Assert.DoesNotContain(entries, entry => entry.Key == ImpinjSettingsContributor.GpiDebounce(3));
        Assert.DoesNotContain(entries, entry => entry.Key == ImpinjSettingsContributor.GpiDebounce(4));

        var noGpiEntries = new List<SettingsEntry>();
        contributor.ContributeLayout(noGpiEntries, reader with { GpiCount = 0 }, runtime);
        Assert.DoesNotContain(noGpiEntries, entry => entry.Key.StartsWith("impinj.gpi-debounce-", StringComparison.Ordinal));
        Assert.False(contributor.IsApplicable(reader with { FeatureCatalog = ReaderFeatureCatalog.Empty }));
    }

    [Fact]
    public void Settings_contributor_hides_unverified_report_fields()
    {
        var contributor = new ImpinjSettingsContributor();
        Guid id = Guid.NewGuid();
        ReaderFeatureCatalog featuresWithoutFastId = new()
        {
            SupportedFeatures = R420Features().SupportedFeatures
                .Where(feature => feature is not { Vendor: "impinj", Id: "fast-id" or "doppler" })
                .ToArray(),
        };
        var reader = new ReaderRuntimeSnapshot
        {
            ReaderId = id,
            Profile = new ReaderProfile { Id = id, Host = "192.0.2.20" },
            State = ReaderState.Disconnected,
            ManufacturerId = ImpinjReaderExtensionModule.ImpinjManufacturerId,
            ModelId = ImpinjReaderExtensionModule.R420ModelId,
            CapabilityRevision = 1,
            FeatureCatalog = featuresWithoutFastId,
        };
        var runtime = new ReaderSettingsRuntimeSnapshot(
            new ReaderSettingsSnapshot(new ReaderSettings(), new ManagedRoSpecSnapshot(
                new InventorySettings(), InventoryRuntimeState.Disabled)), null);
        var entries = new List<SettingsEntry>();

        Assert.True(contributor.IsApplicable(reader));
        contributor.ContributeLayout(entries, reader, runtime);

        Assert.Contains(entries, entry => entry.Key == ImpinjSettingsContributor.PhaseAngle);
        Assert.DoesNotContain(entries, entry => entry.Key == ImpinjSettingsContributor.Doppler);
    }

    [Fact]
    public void Settings_contributor_compiles_gpi_debounce_into_reader_configuration()
    {
        var contributor = new ImpinjSettingsContributor();
        Guid id = Guid.NewGuid();
        var reader = new ReaderRuntimeSnapshot
        {
            ReaderId = id,
            Profile = new ReaderProfile { Id = id, Host = "192.0.2.20" },
            State = ReaderState.Disconnected,
            ManufacturerId = ImpinjReaderExtensionModule.ImpinjManufacturerId,
            ModelId = ImpinjReaderExtensionModule.R420ModelId,
            CapabilityRevision = 1,
            GpiCount = 2,
            FeatureCatalog = R420Features(),
        };
        var runtime = new ReaderSettingsRuntimeSnapshot(
            new ReaderSettingsSnapshot(new ReaderSettings(), new ManagedRoSpecSnapshot(
                new InventorySettings(), InventoryRuntimeState.Disabled)), null);
        var draft = new SettingsDraft { ReaderId = id, CapabilityRevision = 1 };
        draft.Values[ImpinjSettingsContributor.GpiDebounce(1)] = 250;

        var baseSettings = new ReaderSettings
        {
            Configuration = new ReaderConfiguration
            {
                Extensions = new Dictionary<string, object?>
                {
                    [ImpinjReaderConfiguration.ExtensionKey] = new ImpinjReaderConfiguration
                    {
                        GpiDebounce = [new ImpinjGpiDebounceSetting(3, 999)],
                    },
                },
            },
        };
        ReaderSettings applied = contributor.Apply(draft, new EffectiveSettingsLayout
        {
            ReaderId = id,
            CapabilityRevision = 1,
            Entries = [],
        }, reader, runtime, baseSettings);

        var configuration = Assert.IsType<ImpinjReaderConfiguration>(
            applied.Configuration.Extensions[ImpinjReaderConfiguration.ExtensionKey]);
        var debounce = Assert.Single(configuration.GpiDebounce);
        Assert.Equal((ushort)1, debounce.GpiPortNumber);
        Assert.Equal((uint)250, debounce.DebounceMilliseconds);
    }

    [Fact]
    public void Settings_contributor_compiles_all_legacy_inventory_extension_fields()
    {
        var contributor = new ImpinjSettingsContributor();
        Guid id = Guid.NewGuid();
        var reader = new ReaderRuntimeSnapshot
        {
            ReaderId = id,
            Profile = new ReaderProfile { Id = id, Host = "192.0.2.20" },
            State = ReaderState.Disconnected,
            ManufacturerId = ImpinjReaderExtensionModule.ImpinjManufacturerId,
            ModelId = ImpinjReaderExtensionModule.R420ModelId,
            CapabilityRevision = 1,
            FeatureCatalog = R420Features(),
        };
        var runtime = new ReaderSettingsRuntimeSnapshot(
            new ReaderSettingsSnapshot(new ReaderSettings(), new ManagedRoSpecSnapshot(
                new InventorySettings(), InventoryRuntimeState.Disabled)), null);
        var draft = new SettingsDraft { ReaderId = id, CapabilityRevision = 1 };
        draft.Values[ImpinjSettingsContributor.FastId] = true;
        draft.Values[ImpinjSettingsContributor.PhaseAngle] = true;
        draft.Values[ImpinjSettingsContributor.Doppler] = true;
        draft.Values[ImpinjSettingsContributor.SearchMode] = (int)Enum.GetValues<ImpinjInventorySearchType>().First();
        draft.Values[ImpinjSettingsContributor.LowDutyCycle] = true;
        draft.Values[ImpinjSettingsContributor.EmptyFieldTimeout] = 700;
        draft.Values[ImpinjSettingsContributor.FieldPingInterval] = 300;
        draft.Values[ImpinjSettingsContributor.FixedFrequencyMode] = (int)ImpinjFixedFrequencyMode.Channel_List;
        draft.Values[ImpinjSettingsContributor.FixedFrequencyChannels] = "1, 3";
        draft.Values[ImpinjSettingsContributor.GpiDebounce(1)] = 250;

        ReaderSettings applied = contributor.Apply(draft, EmptyLayout(id), reader, runtime, new ReaderSettings());

        ImpinjInventoryReportOptions report = Assert.IsType<ImpinjInventoryReportOptions>(
            applied.Inventory!.Extensions[ImpinjInventoryReportOptions.ExtensionKey]);
        Assert.True(report.IncludeSerializedTid);
        Assert.True(report.IncludeRfPhaseAngle);
        Assert.True(report.IncludeRfDopplerFrequency);

        ImpinjInventoryControlOptions control = Assert.IsType<ImpinjInventoryControlOptions>(
            applied.Inventory.Extensions[ImpinjInventoryControlOptions.ExtensionKey]);
        int searchMode = (int)draft.Values[ImpinjSettingsContributor.SearchMode]!;
        Assert.Equal((ImpinjInventorySearchType)searchMode, control.InventorySearchMode);
        Assert.Equal(ImpinjLowDutyCycleMode.Enabled, control.LowDutyCycle?.Mode);
        Assert.Equal((ushort)700, control.LowDutyCycle?.EmptyFieldTimeoutMilliseconds);
        Assert.Equal((ushort)300, control.LowDutyCycle?.FieldPingIntervalMilliseconds);
        Assert.Equal(ImpinjFixedFrequencyMode.Channel_List, control.FixedFrequency?.Mode);
        Assert.Equal(new ushort[] { 1, 3 }, control.FixedFrequency?.ChannelList);

        ImpinjReaderConfiguration configuration = Assert.IsType<ImpinjReaderConfiguration>(
            applied.Configuration.Extensions[ImpinjReaderConfiguration.ExtensionKey]);
        var debounce = Assert.Single(configuration.GpiDebounce);
        Assert.Equal((uint)250, debounce.DebounceMilliseconds);
    }

    [Fact]
    public void Settings_contributor_rejects_empty_fixed_frequency_channel_list()
    {
        var contributor = new ImpinjSettingsContributor();
        Guid id = Guid.NewGuid();
        var reader = new ReaderRuntimeSnapshot
        {
            ReaderId = id,
            Profile = new ReaderProfile { Id = id, Host = "192.0.2.20" },
            State = ReaderState.Disconnected,
            ManufacturerId = ImpinjReaderExtensionModule.ImpinjManufacturerId,
            ModelId = ImpinjReaderExtensionModule.R420ModelId,
            CapabilityRevision = 1,
            FeatureCatalog = R420Features(),
        };
        var runtime = new ReaderSettingsRuntimeSnapshot(
            new ReaderSettingsSnapshot(new ReaderSettings(), new ManagedRoSpecSnapshot(
                new InventorySettings(), InventoryRuntimeState.Disabled)), null);
        var draft = new SettingsDraft { ReaderId = id, CapabilityRevision = 1 };
        draft.Values[ImpinjSettingsContributor.FixedFrequencyMode] = (int)ImpinjFixedFrequencyMode.Channel_List;
        draft.Values[ImpinjSettingsContributor.FixedFrequencyChannels] = "  ";

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            contributor.Apply(draft, EmptyLayout(id), reader, runtime, new ReaderSettings()));

        Assert.Contains("至少需要一个频道", error.Message);
    }

    [Fact]
    public void ApplyInventoryReportSpec_sets_rf_phase_angle_when_phase_semantic_requested()
    {
        var module = new ImpinjReaderExtensionModule();
        var inventory = new InventorySettings();

        InventorySettings result = module.ApplyInventoryReportSpec(
            inventory,
            new List<string> { ReportFieldSemantics.Phase },
            R420Features());

        var options = Assert.IsType<ImpinjInventoryReportOptions>(
            result.Extensions[ImpinjInventoryReportOptions.ExtensionKey]);
        Assert.True(options.IncludeRfPhaseAngle);
    }

    [Fact]
    public void ApplyInventoryReportSpec_ignores_other_semantics()
    {
        var module = new ImpinjReaderExtensionModule();
        var inventory = new InventorySettings();

        InventorySettings result = module.ApplyInventoryReportSpec(
            inventory,
            new List<string> { "unrelated-semantic" },
            R420Features());

        Assert.DoesNotContain(ImpinjInventoryReportOptions.ExtensionKey, result.Extensions.Keys);
    }

    [Fact]
    public void ApplyInventoryReportSpec_respects_capability_gate()
    {
        var module = new ImpinjReaderExtensionModule();
        var inventory = new InventorySettings();

        // 请求含 phase-report，但能力目录不支持 Impinj RF Phase：不得写入。
        InventorySettings result = module.ApplyInventoryReportSpec(
            inventory,
            new List<string> { ReportFieldSemantics.Phase },
            ReaderFeatureCatalog.Empty);

        Assert.DoesNotContain(ImpinjInventoryReportOptions.ExtensionKey, result.Extensions.Keys);
    }

    [Fact]
    public void Settings_contributor_marks_phase_as_linked_readonly()
    {
        var contributor = new ImpinjSettingsContributor();
        Guid id = Guid.NewGuid();
        var reader = new ReaderRuntimeSnapshot
        {
            ReaderId = id,
            Profile = new ReaderProfile { Id = id, Host = "192.0.2.20" },
            State = ReaderState.Disconnected,
            ManufacturerId = ImpinjReaderExtensionModule.ImpinjManufacturerId,
            ModelId = ImpinjReaderExtensionModule.R420ModelId,
            CapabilityRevision = 1,
            FeatureCatalog = R420Features(),
        };
        var runtime = new ReaderSettingsRuntimeSnapshot(
            new ReaderSettingsSnapshot(new ReaderSettings(), new ManagedRoSpecSnapshot(
                new InventorySettings(), InventoryRuntimeState.Disabled)), null);
        var entries = new List<SettingsEntry>();

        contributor.ContributeLayout(entries, reader, runtime);

        SettingsEntry phase = Assert.Single(entries, entry => entry.Key == ImpinjSettingsContributor.PhaseAngle);
        Assert.True(phase.IsReadOnly);
        Assert.Equal("由寻卡页联动控制", phase.ReadOnlyReason);
    }

    private static EffectiveSettingsLayout EmptyLayout(Guid readerId) => new()
    {
        ReaderId = readerId,
        CapabilityRevision = 1,
        Entries = [],
    };

    private static ReaderFeatureCatalog R420Features() => new()
    {
        SupportedFeatures =
        [
            ReaderFeatures.ImpinjFastId,
            ReaderFeatures.ImpinjRfPhase,
            ReaderFeatures.ImpinjDoppler,
            ReaderFeatures.ImpinjSearchMode,
            ReaderFeatures.ImpinjLowDutyCycle,
            ReaderFeatures.ImpinjFixedFrequency,
            ReaderFeatures.ImpinjGpiDebounce,
        ],
    };
}
