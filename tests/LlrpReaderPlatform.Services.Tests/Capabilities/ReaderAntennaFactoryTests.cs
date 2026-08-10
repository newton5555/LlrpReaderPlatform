using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Services.Capabilities;
using LlrpReaderPlatform.Services.Lifecycle;
using LlrpReaderPlatform.TestKit;
using Xunit;

namespace LlrpReaderPlatform.Services.Tests.Capabilities;

public sealed class ReaderAntennaFactoryTests
{
    [Fact]
    public void FromMaxAntennas_zero_returns_empty()
    {
        Assert.Empty(ReaderAntennaFactory.FromMaxAntennas(0));
    }

    [Fact]
    public void FromMaxAntennas_generates_antennas()
    {
        IReadOnlyList<ReaderAntennaInfo> antennas = ReaderAntennaFactory.FromMaxAntennas(2);

        Assert.Equal(2, antennas.Count);
        Assert.Equal((ushort)1, antennas[0].AntennaId);
        Assert.Equal((ushort)2, antennas[1].AntennaId);
        Assert.Equal("Antenna 1", antennas[0].Name);
    }

    [Fact]
    public async Task ActivateAsync_without_capabilities_leaves_antennas_empty()
    {
        var sessionFactory = new FakeSessionFactory();
        await using var manager = new ReaderManager(sessionFactory, new FakeProfileStore());
        var profile = new ReaderProfile { Id = Guid.NewGuid(), Host = "10.0.0.11", Name = "Cap" };
        sessionFactory.Queue.Enqueue(new FakeSession()); // probe
        sessionFactory.Queue.Enqueue(new FakeSession()); // register（无能力）
        await manager.AddAsync(profile, enableAfterAdding: false);

        ReaderActivationResult result = await manager.ActivateAsync(profile.Id);

        Assert.True(result.Succeeded);
        Assert.Empty(manager.GetSnapshot(profile.Id).Antennas);
    }
}
