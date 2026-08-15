using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Tagging;
using LlrpReaderPlatform.Services.Extensions;
using LlrpReaderPlatform.Services.Lifecycle;
using LlrpReaderPlatform.Services.Persistence;
using LlrpReaderPlatform.VirtualReader;
using Xunit;

namespace LlrpReaderPlatform.VirtualReader.Tests;

public sealed class VirtualReaderReaderManagerTests
{
    [Fact]
    public async Task ReaderManager_add_activate_inventory_and_stop_use_the_same_platform_lifecycle()
    {
        VirtualReaderScenario scenario = new()
        {
            ReaderId = Guid.NewGuid(),
            ReaderName = "Virtual acceptance reader",
            Replay = new VirtualReplayOptions { Mode = VirtualReplayMode.Accelerated, Speed = 1000 },
        };
        TagObservation tag = new()
        {
            Epc = "3000AABB",
            Tid = "E20001",
            ReadCount = 1,
            FirstSeen = DateTimeOffset.UtcNow,
            LastSeen = DateTimeOffset.UtcNow,
            LastAntenna = 1,
            LastRssi = -40,
        };
        var dataset = new VirtualInventoryDataset
        {
            Scenario = scenario,
            Events =
            [
                new VirtualReplayEvent { Sequence = 0, Tag = tag },
            ],
        };
        var catalog = new VirtualReaderCatalog();
        catalog.Register(dataset);
        var factory = new VirtualReaderSessionFactory(catalog);
        var extensions = new IReaderExtensionModule[] { new VirtualReaderExtensionModule(catalog) };
        await using var manager = new ReaderManager(
            factory,
            new InMemoryProfileStore(),
            extensions: extensions);
        var observed = new TaskCompletionSource<TagObservedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        manager.TagObserved += (_, args) => observed.TrySetResult(args);

        ReaderAddResult add = await manager.AddAsync(scenario.ToReaderProfile(), enableAfterAdding: true);
        Assert.True(add.Succeeded, add.Error);
        Assert.Equal(scenario.ReaderId, add.ReaderId);

        StartInventoryResult start = await manager.StartInventoryAsync(
            scenario.ReaderId,
            new InventorySpec { Antennas = [1] });
        Assert.True(start.Succeeded, start.Message);

        TagObservedEventArgs received = await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("3000AABB", received.Tag.Epc);
        Assert.Equal("E20001", received.Tag.Tid);

        await manager.StopInventoryAsync(scenario.ReaderId);
        Assert.Contains(manager.GetTags(scenario.ReaderId), item => item.Epc == "3000AABB");
    }
}
