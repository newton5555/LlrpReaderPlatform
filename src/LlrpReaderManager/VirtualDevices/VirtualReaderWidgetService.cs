using System.Collections.ObjectModel;
using System.Net;
using LlrpDevice.Virtual.Hosting;
using LlrpReaderManager.State;
using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Readers;

namespace LlrpReaderManager.VirtualDevices;

/// <summary>Result of starting a virtual reader widget and registering its TCP endpoint.</summary>
public sealed record VirtualReaderStartResult(
    bool Succeeded,
    string? Error = null,
    VirtualReaderInstance? Instance = null);

/// <summary>A live SDK virtual host that is also registered as a normal platform Reader.</summary>
public sealed class VirtualReaderInstance
{
    internal VirtualReaderInstance(
        Guid readerId,
        string name,
        string profileId,
        int port,
        IVirtualDeviceHost host)
    {
        ReaderId = readerId;
        Name = name;
        ProfileId = profileId;
        Port = port;
        Host = host;
    }

    public Guid ReaderId { get; }
    public string Name { get; }
    public string ProfileId { get; }
    public int Port { get; }
    public IVirtualDeviceHost Host { get; }
    public VirtualLlrpDeviceHostState State => Host.State;
    public int ConnectedClientCount => Host.ConnectedClientCount;
}

/// <summary>
/// Consumer-owned virtual reader widget manager. The SDK host is deliberately kept here,
/// outside the platform Services layer; once started, its loopback endpoint follows the
/// same ReaderManager add/activate path as a physical reader.
/// </summary>
public sealed class VirtualReaderWidgetService : IAsyncDisposable
{
    private readonly IReaderManager readerManager;
    private readonly ReaderManagerState state;
    private readonly Dictionary<Guid, VirtualReaderInstance> instances = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    private int disposed;

    public VirtualReaderWidgetService(IReaderManager readerManager, ReaderManagerState state)
    {
        this.readerManager = readerManager;
        this.state = state;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<VirtualReaderInstance> Instances =>
        new ReadOnlyCollection<VirtualReaderInstance>(instances.Values.ToArray());

    public bool IsBusy { get; private set; }

    public async Task<VirtualReaderStartResult> StartAsync(
        string profileId,
        string? name,
        int port,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IsBusy = true;
        NotifyChanged();

        IVirtualDeviceHost? host = null;
        try
        {
            VirtualDeviceProfileInfo profile = VirtualDeviceProfiles.Get(profileId);
            string readerName = string.IsNullOrWhiteSpace(name)
                ? $"Virtual · {profile.Name}"
                : name.Trim();

            host = VirtualLlrpDeviceHost.Create(new VirtualDeviceHostOptions
            {
                ProfileId = profile.Id,
                Name = readerName,
                ListenAddress = IPAddress.Loopback,
                Port = port,
                ProtocolVersion = VirtualDeviceProtocolVersion.Llrp101,
                MaximumClientConnections = 1,
                RelaxedRoSpecStateChecks = true,
                ReportInterval = TimeSpan.FromMilliseconds(100),
                RepeatReports = true,
            });
            host.LifecycleChanged += OnHostLifecycleChanged;
            host.ClientChanged += OnHostClientChanged;
            await host.StartAsync(cancellationToken).ConfigureAwait(false);

            int boundPort = host.BoundPort;
            var readerProfile = new ReaderProfile
            {
                Id = Guid.NewGuid(),
                Name = readerName,
                Host = IPAddress.Loopback.ToString(),
                Port = boundPort,
                LlrpVersion = LlrpProtocolVersionOption.Force101,
                IsEnabled = true,
            };
            ReaderAddResult added = await readerManager
                .AddAsync(readerProfile, enableAfterAdding: true, cancellationToken)
                .ConfigureAwait(false);
            if (!added.Succeeded || added.ReaderId is not { } readerId)
            {
                return new VirtualReaderStartResult(
                    false,
                    added.Error ?? "虚拟 Reader 已启动，但未能加入 Reader 列表。");
            }

            var instance = new VirtualReaderInstance(readerId, readerName, profile.Id, boundPort, host);
            instances[readerId] = instance;
            host = null;
            state.Refresh();
            NotifyChanged();
            return new VirtualReaderStartResult(true, Instance: instance);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new VirtualReaderStartResult(false, exception.Message);
        }
        finally
        {
            if (host is not null)
            {
                host.LifecycleChanged -= OnHostLifecycleChanged;
                host.ClientChanged -= OnHostClientChanged;
                await host.DisposeAsync().ConfigureAwait(false);
            }

            IsBusy = false;
            gate.Release();
            NotifyChanged();
        }
    }

    public async Task StopAsync(Guid readerId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IsBusy = true;
        NotifyChanged();
        try
        {
            if (!instances.Remove(readerId, out VirtualReaderInstance? instance))
            {
                return;
            }

            try
            {
                await readerManager.SetEnabledAsync(readerId, false, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await readerManager.RemoveAsync(readerId, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    instance.Host.LifecycleChanged -= OnHostLifecycleChanged;
                    instance.Host.ClientChanged -= OnHostClientChanged;
                    await instance.Host.DisposeAsync().ConfigureAwait(false);
                }
            }

            state.Refresh();
            NotifyChanged();
        }
        finally
        {
            IsBusy = false;
            gate.Release();
            NotifyChanged();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        VirtualReaderInstance[] current = instances.Values.ToArray();
        instances.Clear();
        foreach (VirtualReaderInstance instance in current)
        {
            try
            {
                await readerManager.SetEnabledAsync(instance.ReaderId, false).ConfigureAwait(false);
                await readerManager.RemoveAsync(instance.ReaderId).ConfigureAwait(false);
            }
            catch
            {
                // Application shutdown is best effort; the host must still be disposed.
            }

            instance.Host.LifecycleChanged -= OnHostLifecycleChanged;
            instance.Host.ClientChanged -= OnHostClientChanged;
            await instance.Host.DisposeAsync().ConfigureAwait(false);
        }

        gate.Dispose();
    }

    private void OnHostLifecycleChanged(object? sender, VirtualDeviceHostLifecycleChangedEventArgs args) => NotifyChanged();

    private void OnHostClientChanged(object? sender, VirtualDeviceClientChangedEventArgs args) => NotifyChanged();

    private void NotifyChanged()
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
