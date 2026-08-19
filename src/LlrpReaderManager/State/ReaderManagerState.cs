using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Discovery;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Contracts.Tagging;

namespace LlrpReaderManager.State;

/// <summary>
/// Blazor consumer state over the platform contracts. It keeps the UI reactive without
/// exposing SDK sessions or putting lifecycle orchestration into Razor components.
/// </summary>
public sealed class ReaderManagerState : IAsyncDisposable
{
    private readonly IReaderManager readerManager;
    private readonly IReaderDiscoveryService discovery;
    private readonly IInventoryService inventory;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private int notificationPending;
    private int disposed;

    public ReaderManagerState(
        IReaderManager readerManager,
        IInventoryService inventory,
        IReaderSettingsService settings,
        IReaderDiscoveryService discovery)
    {
        this.readerManager = readerManager;
        this.inventory = inventory;
        this.discovery = discovery;
        Settings = settings;
        readerManager.StateChanged += OnReaderStateChanged;
        inventory.TagObserved += OnTagObserved;
        inventory.LifecycleChanged += OnInventoryLifecycleChanged;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<ReaderRuntimeSnapshot> Readers { get; private set; } = [];

    public IReadOnlyList<DiscoveredReader> DiscoveredReaders { get; private set; } = [];

    public bool IsInitialized { get; private set; }

    public bool IsBusy { get; private set; }

    public string? LastError { get; private set; }

    public IReaderManager ReaderManager => readerManager;

    public IInventoryService Inventory => inventory;

    public IReaderSettingsService Settings { get; }

    public IReaderDiscoveryService Discovery => discovery;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsInitialized)
        {
            Refresh();
            return;
        }

        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsInitialized)
            {
                return;
            }

            await readerManager.InitializeAsync(cancellationToken).ConfigureAwait(false);
            IsInitialized = true;
            LastError = null;
            Refresh();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastError = exception.Message;
            Refresh();
        }
        finally
        {
            operationGate.Release();
        }
    }

    public ReaderRuntimeSnapshot? Find(Guid? readerId) => readerId is { } id
        ? Readers.FirstOrDefault(reader => reader.ReaderId == id)
        : null;

    public IReadOnlyList<TagObservation> GetTags(Guid readerId) => inventory.GetTags(readerId);

    public void Select(Guid? readerId)
    {
        if (readerId is { } id && Readers.All(reader => reader.ReaderId != id))
        {
            return;
        }

        SelectedReaderId = readerId;
        NotifyChanged();
    }

    public Guid? SelectedReaderId { get; private set; }

    public async Task<ReaderAddResult> AddAsync(
        ReaderProfile profile,
        bool enableAfterAdding,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return await RunBusyAsync(
            () => readerManager.AddAsync(profile, enableAfterAdding, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task DiscoverAsync(
        TimeSpan scanDuration,
        CancellationToken cancellationToken = default)
    {
        await RunBusyAsync(
            async () =>
            {
                DiscoveredReaders = await discovery
                    .DiscoverAsync(scanDuration, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(Guid readerId, CancellationToken cancellationToken = default)
    {
        await RunBusyAsync(
            async () =>
            {
                await readerManager.RemoveAsync(readerId, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SetEnabledAsync(
        Guid readerId,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await RunBusyAsync(
            async () =>
            {
                await readerManager.SetEnabledAsync(readerId, enabled, cancellationToken).ConfigureAwait(false);
                if (!enabled)
                {
                    return true;
                }

                ReaderActivationResult activation;
                try
                {
                    activation = await readerManager.ActivateAsync(readerId, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await readerManager.SetEnabledAsync(readerId, false, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }

                if (!activation.Succeeded)
                {
                    await readerManager.SetEnabledAsync(readerId, false, CancellationToken.None).ConfigureAwait(false);
                    throw new InvalidOperationException(
                        activation.Error ?? "Reader activation and capability synchronization failed.");
                }

                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<StartInventoryResult> StartInventoryAsync(
        Guid readerId,
        InventorySpec spec,
        CancellationToken cancellationToken = default)
    {
        return await RunBusyAsync(
            () => inventory.StartInventoryAsync(readerId, spec, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<StartInventoryResult>> StartInventoryForReadersAsync(
        IReadOnlyList<Guid> readerIds,
        InventorySpec spec,
        CancellationToken cancellationToken = default) =>
        RunBusyAsync(
            async () =>
            {
                StartInventoryResult[] results = await Task.WhenAll(
                    readerIds.Select(readerId => inventory.StartInventoryAsync(readerId, spec, cancellationToken)))
                    .ConfigureAwait(false);
                return (IReadOnlyList<StartInventoryResult>)results;
            },
            cancellationToken);

    public async Task StopInventoryAsync(Guid readerId, CancellationToken cancellationToken = default)
    {
        await RunBusyAsync(
            async () =>
            {
                await inventory.StopInventoryAsync(readerId, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task StopInventoryForReadersAsync(
        IReadOnlyList<Guid> readerIds,
        CancellationToken cancellationToken = default)
    {
        await RunBusyAsync(
            async () =>
            {
                await Task.WhenAll(readerIds.Select(readerId => inventory.StopInventoryAsync(readerId, cancellationToken)))
                    .ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public Task<GpioStatusSnapshot> GetGpioStatusAsync(
        Guid readerId,
        CancellationToken cancellationToken = default) =>
        RunBusyAsync(
            () => inventory.GetGpioStatusAsync(readerId, cancellationToken),
            cancellationToken);

    public async Task SetGpoAsync(
        Guid readerId,
        ushort portNumber,
        bool state,
        CancellationToken cancellationToken = default)
    {
        await RunBusyAsync(
            async () =>
            {
                await inventory.SetGpoAsync(
                    readerId,
                    new GpioCommand { PortNumber = portNumber, State = state },
                    cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public void ClearError()
    {
        LastError = null;
        NotifyChanged();
    }

    public void Refresh()
    {
        Readers = readerManager.Readers.ToArray();
        if (SelectedReaderId is { } selected
            && Readers.All(reader => reader.ReaderId != selected))
        {
            SelectedReaderId = Readers.FirstOrDefault()?.ReaderId;
        }

        NotifyChanged();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        readerManager.StateChanged -= OnReaderStateChanged;
        inventory.TagObserved -= OnTagObserved;
        inventory.LifecycleChanged -= OnInventoryLifecycleChanged;
        operationGate.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task<T> RunBusyAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        IsBusy = true;
        LastError = null;
        NotifyChanged();
        try
        {
            T result = await operation().ConfigureAwait(false);
            Refresh();
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LastError = exception.Message;
            NotifyChanged();
            throw;
        }
        finally
        {
            IsBusy = false;
            operationGate.Release();
            NotifyChanged();
        }
    }

    private void OnReaderStateChanged(object? sender, ReaderStateChangedEventArgs args)
    {
        Refresh();
    }

    private void OnInventoryLifecycleChanged(object? sender, InventoryLifecycleChangedEventArgs args)
    {
        Refresh();
    }

    private void OnTagObserved(object? sender, TagObservedEventArgs args)
    {
        if (Interlocked.Exchange(ref notificationPending, 1) == 0)
        {
            _ = NotifyTagsLaterAsync();
        }
    }

    private async Task NotifyTagsLaterAsync()
    {
        try
        {
            await Task.Delay(100).ConfigureAwait(false);
            NotifyChanged();
        }
        finally
        {
            Interlocked.Exchange(ref notificationPending, 0);
        }
    }

    private void NotifyChanged()
    {
        if (Volatile.Read(ref disposed) == 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
