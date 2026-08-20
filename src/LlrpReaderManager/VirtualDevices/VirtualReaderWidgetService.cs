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
/// Consumer-owned virtual reader widget manager for UI Client testing.
/// Manages a single configurable virtual device instance.
/// </summary>
public sealed class VirtualReaderWidgetService : IAsyncDisposable
{
    private readonly IReaderManager readerManager;
    private readonly ReaderManagerState state;
    private readonly SemaphoreSlim gate = new(1, 1);
    private VirtualReaderInstance? currentInstance;
    private int disposed;

    public VirtualReaderWidgetService(IReaderManager readerManager, ReaderManagerState state)
    {
        this.readerManager = readerManager;
        this.state = state;
    }

    public event EventHandler? Changed;

    public VirtualReaderInstance? CurrentInstance => currentInstance;

    public bool IsRunning => currentInstance is not null;

    public IReadOnlyList<VirtualReaderInstance> Instances =>
        currentInstance is not null ? [currentInstance] : [];

    public bool IsBusy { get; private set; }

    public async Task<VirtualReaderStartResult> StartAsync(
        string profileId = VirtualDeviceProfiles.Standard101Id,
        string? name = null,
        int port = 0,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IsBusy = true;
        NotifyChanged();

        IVirtualDeviceHost? host = null;
        try
        {
            // If already running, stop previous instance first
            if (currentInstance is not null)
            {
                await StopInternalAsync(cancellationToken).ConfigureAwait(false);
            }

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
            currentInstance = instance;
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

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IsBusy = true;
        NotifyChanged();
        try
        {
            await StopInternalAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
            gate.Release();
            NotifyChanged();
        }
    }

    public Task StopAsync(Guid readerId, CancellationToken cancellationToken = default)
    {
        if (currentInstance?.ReaderId == readerId)
        {
            return StopAsync(cancellationToken);
        }

        return Task.CompletedTask;
    }

    private async Task StopInternalAsync(CancellationToken cancellationToken)
    {
        if (currentInstance is null)
        {
            return;
        }

        VirtualReaderInstance instance = currentInstance;
        currentInstance = null;

        try
        {
            try
            {
                await readerManager.SetEnabledAsync(instance.ReaderId, false, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                try
                {
                    await readerManager.RemoveAsync(instance.ReaderId, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    instance.Host.LifecycleChanged -= OnHostLifecycleChanged;
                    instance.Host.ClientChanged -= OnHostClientChanged;
                    await instance.Host.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            state.Refresh();
            NotifyChanged();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (currentInstance is not null)
        {
            VirtualReaderInstance instance = currentInstance;
            currentInstance = null;
            try
            {
                await readerManager.SetEnabledAsync(instance.ReaderId, false).ConfigureAwait(false);
                await readerManager.RemoveAsync(instance.ReaderId).ConfigureAwait(false);
            }
            catch
            {
                // Application shutdown is best effort
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

