using System.Collections.ObjectModel;
using SdkProtocolVersion = LlrpNet.Core.Protocol.LlrpProtocolVersion;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Contracts.Tagging;
using LlrpReaderPlatform.Extensions.Zebra;
using LlrpReaderPlatform.Services.Extensions;
using LlrpReaderPlatform.Services.Settings;
using LlrpSdk;
using LlrpSdk.Extensions.Zebra;
using Xunit;

namespace LlrpReaderPlatform.Extensions.Zebra.Tests;

public sealed class ZebraReaderExtensionModuleTests
{
    [Fact]
    public void IsApplicable_requires_zebra_and_llrp_101()
    {
        var module = new ZebraReaderExtensionModule();

        Assert.True(module.IsApplicable(Probe(SdkProtocolVersion.Version101)));
        Assert.False(module.IsApplicable(Probe(SdkProtocolVersion.Version11)));
        Assert.False(module.IsApplicable(new ReaderProbeInfo(1, ZebraReaderExtensionModule.Fx9600ModelId, "3.32.37.0", "FX9600", SdkProtocolVersion.Version101)));
    }

    [Fact]
    public void GetFeatures_exposes_only_report_projection_for_unknown_profile()
    {
        var module = new ZebraReaderExtensionModule();

        IReadOnlyList<Feature> features = module.GetFeatures(Probe(SdkProtocolVersion.Version101, modelId: 99999, firmware: "unknown"));

        Assert.Contains(ZebraFeatures.ReportPhase, features);
        Assert.Contains(ZebraFeatures.ReportGps, features);
        Assert.Contains(ZebraFeatures.ReportXpc, features);
        Assert.DoesNotContain(ZebraFeatures.Configuration, features);
        Assert.DoesNotContain(ZebraFeatures.InventoryOptions, features);
    }

    [Fact]
    public void GetFeatures_exposes_verified_fx9600_profile()
    {
        var module = new ZebraReaderExtensionModule();

        IReadOnlyList<Feature> features = module.GetFeatures(Probe(SdkProtocolVersion.Version101));

        Assert.Contains(ZebraFeatures.Configuration, features);
        Assert.Contains(ZebraFeatures.InventoryOptions, features);
        Assert.Contains(ZebraFeatures.ReportXpc, features);
    }

    [Fact]
    public void ApplyInventoryReportSpec_compiles_report_semantics_to_zebra_options()
    {
        var module = new ZebraReaderExtensionModule();
        InventorySettings result = module.ApplyInventoryReportSpec(
            new InventorySettings(),
            [ReportFieldSemantics.Phase, ReportFieldSemantics.Gps, ReportFieldSemantics.Xpc],
            VerifiedFeatures());

        var options = Assert.IsType<ZebraInventoryReportOptions>(
            result.Extensions[ZebraInventoryReportOptions.ExtensionKey]);
        Assert.True(options.IncludePhase);
        Assert.True(options.IncludeGps);
        Assert.True(options.IncludeMltReport);
    }

    [Fact]
    public void ProjectTagReport_emits_stable_semantic_fields_and_keeps_vendor_diagnostics()
    {
        var module = new ZebraReaderExtensionModule();
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
                [ZebraTagReportExtensions.PhaseExtensionKey] = (short)12,
                [ZebraTagReportExtensions.GpsExtensionKey] = new ZebraGpsCoordinates(1, 2, 3),
                [ZebraTagReportExtensions.ExtendedPcExtensionKey] = new ZebraExtendedPc(0x1234, 0x5678),
                ["zebra.raw-diagnostic"] = 7,
            },
            EpcBitLength: 16,
            PcBits: null);

        ReaderTagReportProjection projection = module.ProjectTagReport(report);

        Assert.Equal("12", projection.Fields[ReportFieldSemantics.Phase]);
        Assert.Equal("1;2;3", projection.Fields[ReportFieldSemantics.Gps]);
        Assert.Equal("12345678", projection.Fields[ReportFieldSemantics.Xpc]);
        Assert.Equal("7", projection.Fields["zebra.raw-diagnostic"]);
    }

    [Fact]
    public void Settings_contributor_marks_report_entries_with_platform_metadata()
    {
        var contributor = new ZebraSettingsContributor();
        Guid id = Guid.NewGuid();
        var reader = new ReaderRuntimeSnapshot
        {
            ReaderId = id,
            Profile = new ReaderProfile { Id = id, Host = "192.0.2.40" },
            State = ReaderState.Disconnected,
            ManufacturerId = ZebraReaderExtensionModule.ZebraManufacturerId,
            ModelId = ZebraReaderExtensionModule.Fx9600ModelId,
            CapabilityRevision = 1,
            FeatureCatalog = VerifiedFeatures(),
        };
        var runtime = new ReaderSettingsRuntimeSnapshot(
            new ReaderSettingsSnapshot(
                new ReaderSettings
                {
                    Configuration = new ReaderConfiguration
                    {
                        Extensions = new Dictionary<string, object?>
                        {
                            [ZebraReaderConfiguration.ExtensionKey] = new ZebraReaderConfiguration
                            {
                                RadioPowerState = true,
                            },
                        },
                    },
                    Inventory = new InventorySettings
                    {
                        Extensions = new Dictionary<string, object?>
                        {
                            [ZebraInventoryReportOptions.ExtensionKey] = new ZebraInventoryReportOptions(),
                        },
                    },
                },
                new ManagedRoSpecSnapshot(new InventorySettings(), InventoryRuntimeState.Disabled)),
            null);
        var entries = new List<SettingsEntry>();

        contributor.ContributeLayout(entries, reader, runtime);

        SettingsEntry phase = Assert.Single(entries, entry => entry.Key == ZebraSettingsContributor.IncludePhase);
        Assert.Equal(SettingsSemantics.PhaseReport, phase.SemanticId);
        Assert.Equal(SettingsGroups.Report, phase.GroupKey);
        Assert.True(phase.IsReadOnly);
    }

    private static ReaderProbeInfo Probe(
        SdkProtocolVersion version,
        uint modelId = ZebraReaderExtensionModule.Fx9600ModelId,
        string firmware = ZebraReaderExtensionModule.VerifiedFx9600Firmware) =>
        new(ZebraReaderExtensionModule.ZebraManufacturerId, modelId, firmware, "FX9600", version);

    private static ReaderFeatureCatalog VerifiedFeatures() => new()
    {
        SupportedFeatures =
        [
            ZebraFeatures.Configuration,
            ZebraFeatures.InventoryOptions,
            ZebraFeatures.ReportPhase,
            ZebraFeatures.ReportGps,
            ZebraFeatures.ReportXpc,
        ],
    };
}
