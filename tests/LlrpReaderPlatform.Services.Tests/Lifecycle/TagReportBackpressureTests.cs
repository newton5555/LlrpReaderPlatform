using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Tagging;
using LlrpReaderPlatform.Services.Lifecycle;
using LlrpReaderPlatform.TestKit;
using Xunit;

namespace LlrpReaderPlatform.Services.Tests.Lifecycle;

public sealed class TagReportBackpressureTests
{
    [Fact]
    public async Task Saturated_tag_report_channel_drops_reports_but_stop_drains_and_disconnects()
    {
        var factory = new FakeSessionFactory();
        await using var manager = new ReaderManager(factory, new FakeProfileStore());
        var profile = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Host = "192.0.2.100",
        };
        factory.Queue.Enqueue(new FakeSession()); // Probe
        var session = new FakeSession();
        factory.Queue.Enqueue(session); // Registered session
        await manager.AddAsync(profile, enableAfterAdding: false);
        await manager.StartInventoryAsync(profile.Id, new InventorySpec());

        using var consumerGate = new ManualResetEventSlim(false);
        var firstReportConsumed = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int consumed = 0;
        manager.TagObserved += (_, _) =>
        {
            if (Interlocked.Increment(ref consumed) == 1)
            {
                firstReportConsumed.TrySetResult(true);
                consumerGate.Wait();
            }
        };

        session.EmitTag([0x30, 0x00]);
        await firstReportConsumed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // The consumer is deliberately blocked after the first report. This makes the
        // bounded channel reach capacity deterministically instead of relying on timing.
        for (int i = 1; i <= 100_100; i++)
        {
            session.EmitTag([(byte)(i >> 8), (byte)i]);
        }

        Assert.True(manager.DroppedTagReportCount > 0);

        consumerGate.Set();
        await manager.StopInventoryAsync(profile.Id).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(session.IsConnected);
        Assert.False(session.InventoryRunning);
    }
}
