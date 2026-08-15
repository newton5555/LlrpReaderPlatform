using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Tagging;
using LlrpReaderPlatform.Services.Extensions;
using LlrpReaderPlatform.Services.Sdk;
using LlrpReaderPlatform.VirtualReader;
using LlrpSdk;
using Xunit;
using ContractTagAccessResult = LlrpReaderPlatform.Contracts.Tagging.TagAccessResult;
using ContractTagMemoryBank = LlrpReaderPlatform.Contracts.Tagging.TagMemoryBank;

namespace LlrpReaderPlatform.VirtualReader.Tests;

public sealed class VirtualReaderSessionTests
{
    [Fact]
    public async Task Session_connects_queries_settings_replays_step_and_stops()
    {
        VirtualReaderScenario scenario = new()
        {
            ReaderId = Guid.NewGuid(),
            Capabilities = new VirtualReaderCapabilities { MaxAntennas = 2 },
            Replay = new VirtualReplayOptions { Mode = VirtualReplayMode.Step },
        };
        TagObservation tag = Tag("3000AABB", "E2000017221101441890CDEF", 1);
        var dataset = new VirtualInventoryDataset
        {
            Scenario = scenario,
            Events =
            [
                new VirtualReplayEvent
                {
                    Sequence = 0,
                    Offset = TimeSpan.Zero,
                    Tag = tag,
                },
            ],
        };
        await using var session = new VirtualReaderSession(dataset);
        var reportReceived = new TaskCompletionSource<LlrpSdk.TagReport>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.TagReported += (_, args) => reportReceived.TrySetResult(args.Report);

        await session.ConnectAsync(CancellationToken.None);
        ReaderSettingsSnapshot snapshot = await session.QuerySettingsAsync(CancellationToken.None);

        Assert.True(session.IsConnected);
        Assert.Equal(VirtualReaderState.Ready, session.State);
        Assert.Equal((ushort)2, session.Capabilities!.MaxNumberOfAntennas);
        Assert.Equal([1, 2], snapshot.Settings.Inventory!.AntennaIds);

        await session.StartInventoryAsync(snapshot.Settings.Inventory with { AntennaIds = [1] }, CancellationToken.None);
        Assert.Equal(VirtualReaderState.InventoryRunning, session.State);
        session.AdvanceOneReplayEvent();

        LlrpSdk.TagReport report = await reportReceived.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("3000AABB", report.EpcHex);
        Assert.Equal((ushort)1, report.AntennaId);
        Assert.Equal("E2000017221101441890CDEF", report.Extensions!["virtual.tid"]);

        await session.StopInventoryAsync(CancellationToken.None);
        Assert.Equal(VirtualReaderState.Ready, session.State);
        Assert.Equal(1, session.StopInventoryCount);
    }

    [Fact]
    public async Task Session_rejects_zero_antenna_and_unsupported_power_index()
    {
        VirtualReaderScenario scenario = new()
        {
            ReaderId = Guid.NewGuid(),
            Capabilities = new VirtualReaderCapabilities
            {
                MaxAntennas = 2,
                TxPowerIndices = [1, 2],
            },
        };
        await using var session = new VirtualReaderSession(new VirtualInventoryDataset { Scenario = scenario });
        await session.ConnectAsync(CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentException>(() => session.StartInventoryAsync(
            new LlrpSdk.InventorySettings { AntennaIds = [0] },
            CancellationToken.None));

        ReaderSettings invalid = session.SettingsSnapshot.Settings with
        {
            Configuration = session.SettingsSnapshot.Settings.Configuration with
            {
                Antennas =
                [
                    new LlrpSdk.AntennaConfigurationSettings
                    {
                        AntennaId = 1,
                        TransmitPowerIndex = 99,
                    },
                ],
            },
        };
        await Assert.ThrowsAsync<ArgumentException>(() => session.ApplySettingsAsync(invalid, CancellationToken.None));
    }

    [Fact]
    public async Task Session_supports_tag_access_gpo_gpi_and_fault_events()
    {
        VirtualReaderScenario scenario = new()
        {
            ReaderId = Guid.NewGuid(),
            Capabilities = new VirtualReaderCapabilities { MaxAntennas = 1, GpiCount = 1, GpoCount = 1 },
            TagMemory =
            [
                new VirtualTagMemorySeed
                {
                    Epc = "3000AABB",
                    TidHex = "E20001",
                    UserHex = "11223344",
                    AccessPasswordHex = "01020304",
                },
            ],
        };
        await using var session = new VirtualReaderSession(new VirtualInventoryDataset { Scenario = scenario });
        await session.ConnectAsync(CancellationToken.None);

        ContractTagAccessResult read = await session.ReadTagMemoryAsync(new TagReadRequest
        {
            Epc = "3000AABB",
            MemoryBank = ContractTagMemoryBank.User,
            OffsetWords = 0,
            WordCount = 2,
            AccessPasswordHex = "01020304",
        }, CancellationToken.None);
        Assert.True(read.Succeeded);
        Assert.Equal("11223344", read.DataHex);

        ContractTagAccessResult write = await session.WriteTagMemoryAsync(new TagWriteRequest
        {
            Epc = "3000AABB",
            MemoryBank = ContractTagMemoryBank.User,
            DataHex = "A55A",
            AccessPasswordHex = "01020304",
        }, CancellationToken.None);
        Assert.True(write.Succeeded);

        await session.SetGpoAsync(1, true, CancellationToken.None);
        Assert.True(Assert.Single(await session.GetGpoStatusAsync(CancellationToken.None)).State);

        TaskCompletionSource<SdkGpiChangedEventArgs> gpiChanged = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.GpiChanged += (_, args) => gpiChanged.TrySetResult(args);
        session.RaiseGpiChanged(1, true);
        Assert.True((await gpiChanged.Task.WaitAsync(TimeSpan.FromSeconds(1))).State);

        TaskCompletionSource<ReaderConnectionFaultedEventArgs> faulted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.ConnectionFaulted += (_, args) => faulted.TrySetResult(args);
        session.RaiseConnectionFaulted("simulated");
        Assert.Equal("simulated", (await faulted.Task.WaitAsync(TimeSpan.FromSeconds(1))).Message);
        Assert.False(session.IsConnected);
    }

    [Fact]
    public void Factory_requires_registered_scenario_and_extension_projects_virtual_tid()
    {
        VirtualReaderScenario scenario = new() { ReaderId = Guid.NewGuid() };
        var catalog = new VirtualReaderCatalog();
        var factory = new VirtualReaderSessionFactory(catalog);
        ReaderProfile profile = scenario.ToReaderProfile();

        Assert.Throws<InvalidOperationException>(() => factory.Create(profile));
        catalog.Register(new VirtualInventoryDataset { Scenario = scenario });
        Assert.IsType<VirtualReaderSession>(factory.Create(profile));

        var module = new VirtualReaderExtensionModule(catalog);
        var report = new LlrpSdk.TagReport(
            new byte[] { 0x30, 0x00 },
            1,
            0,
            1,
            1,
            -30,
            1,
            null,
            null,
            1,
            0,
            Extensions: new Dictionary<string, object?>
            {
                [VirtualReaderExtensionModule.VirtualTidField] = "E20001",
            });
        ReaderTagReportProjection projection = module.ProjectTagReport(report);
        Assert.Equal("E20001", projection.TidHex);
    }

    [Fact]
    public async Task Catalog_factory_preserves_device_settings_and_tag_memory_across_sessions()
    {
        VirtualReaderScenario scenario = new()
        {
            ReaderId = Guid.NewGuid(),
            Capabilities = new VirtualReaderCapabilities { MaxAntennas = 1 },
            TagMemory = [new VirtualTagMemorySeed { Epc = "3000AABB", UserHex = "1122" }],
        };
        var catalog = new VirtualReaderCatalog();
        catalog.Register(new VirtualInventoryDataset { Scenario = scenario });
        var factory = new VirtualReaderSessionFactory(catalog);

        await using (IReaderSession first = factory.Create(scenario.ToReaderProfile()))
        {
            await first.ConnectAsync(CancellationToken.None);
            ReaderSettings settings = ((VirtualReaderSession)first).SettingsSnapshot.Settings with
            {
                Configuration = ((VirtualReaderSession)first).SettingsSnapshot.Settings.Configuration with
                {
                    Antennas =
                    [
                        new LlrpSdk.AntennaConfigurationSettings
                        {
                            AntennaId = 1,
                            TransmitPowerIndex = 2,
                        },
                    ],
                },
            };
            await first.ApplySettingsAsync(settings, CancellationToken.None);
            ContractTagAccessResult write = await first.WriteTagMemoryAsync(new TagWriteRequest
            {
                Epc = "3000AABB",
                MemoryBank = ContractTagMemoryBank.User,
                DataHex = "A55A",
            }, CancellationToken.None);
            Assert.True(write.Succeeded);
        }

        await using (IReaderSession second = factory.Create(scenario.ToReaderProfile()))
        {
            await second.ConnectAsync(CancellationToken.None);
            VirtualReaderSession virtualSession = Assert.IsType<VirtualReaderSession>(second);
            Assert.Equal((ushort)2, virtualSession.SettingsSnapshot.Settings.Configuration.Antennas.Single().TransmitPowerIndex);
            ContractTagAccessResult read = await second.ReadTagMemoryAsync(new TagReadRequest
            {
                Epc = "3000AABB",
                MemoryBank = ContractTagMemoryBank.User,
                WordCount = 1,
            }, CancellationToken.None);
            Assert.True(read.Succeeded);
            Assert.Equal("A55A", read.DataHex);
        }
    }

    private static TagObservation Tag(string epc, string tid, ushort antenna) => new()
    {
        Epc = epc,
        Tid = tid,
        ReadCount = 1,
        FirstSeen = DateTimeOffset.UtcNow,
        LastSeen = DateTimeOffset.UtcNow,
        LastRssi = -35,
        LastChannelIndex = 1,
        LastAntenna = antenna,
    };
}
