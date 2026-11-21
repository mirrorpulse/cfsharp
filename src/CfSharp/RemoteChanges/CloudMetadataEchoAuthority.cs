using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace CfSharp;

// A durable receipt records what happened, not a recovered caller's promise about other writers.
// Only a successful public call on this owner can activate that promise in memory.
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The async semaphore never creates a wait handle. Feed callers can still be releasing it when the owner shuts down.")]
internal sealed class CloudMetadataEchoAuthority
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _publicationGate = new(1, 1);
    private readonly HashSet<Guid> _applying = [];
    private readonly Dictionary<Guid, HashSet<(Guid ItemId, CloudLocalFileBinding Binding)>> _authorized = [];
    private readonly PriorityQueue<Guid, long> _expirations = new();
    private long _reconciliationGeneration;

    internal long ReconciliationGeneration => Volatile.Read(ref _reconciliationGeneration);

    // Called while the publication lease orders the metadata change against acknowledgement.
    internal void TrackReconciliationChange()
    {
        lock (_gate) { Interlocked.Increment(ref _reconciliationGeneration); }
    }

    internal Call Begin()
    {
        lock (_gate)
        {
            Prune();
            Guid id = Guid.NewGuid();
            _applying.Add(id);
            return new(this, id);
        }
    }

    internal bool IsApplying(Guid id)
    {
        lock (_gate) { return _applying.Contains(id); }
    }

    // Custom stores may allow overlapping transactions. Order journal publication
    // with protected mutations and metadata receipt writes on this owner.
    internal async ValueTask<IDisposable> AcquirePublicationAsync(CancellationToken cancellationToken)
    {
        await _publicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new PublicationLease(_publicationGate);
    }

    internal long? GetNextExpirationTimestamp()
    {
        lock (_gate)
        {
            Prune();
            return _expirations.TryPeek(out _, out long expires) ? expires : null;
        }
    }

    internal bool IsAuthorized(RemoteMetadataIntent intent)
    {
        if (!CanAuthorize(intent)) { return false; }
        lock (_gate)
        {
            Prune();
            return Covers(_authorized, intent);
        }
    }

    internal (long ReconciliationGeneration, Func<RemoteMetadataIntent, bool> RetainReceipt) CaptureReconciliationRetention()
    {
        lock (_gate)
        {
            Prune();
            // The sets are copied from the call on authorization and never subsequently changed.
            Dictionary<Guid, HashSet<(Guid ItemId, CloudLocalFileBinding Binding)>> grants = new(_authorized);
            // A call can publish an outcome while the acknowledgement waits for publication.
            HashSet<Guid> applying = new(_applying);
            return (_reconciliationGeneration, intent => applying.Contains(intent.CallId) || CanAuthorize(intent) && Covers(grants, intent));
        }
    }

    private static bool CanAuthorize(RemoteMetadataIntent intent) => intent.Binding is not null &&
        intent.Coordinated && !intent.Passthrough && !intent.Superseded && !intent.Reconciled &&
        !intent.RequiresReconciliation && !intent.HasUnconfirmedOutcome;

    private static bool Covers(Dictionary<Guid, HashSet<(Guid ItemId, CloudLocalFileBinding Binding)>> grants,
        RemoteMetadataIntent intent) =>
        grants.TryGetValue(intent.CallId, out HashSet<(Guid ItemId, CloudLocalFileBinding Binding)>? objects) &&
        objects.Contains((intent.ItemId, intent.Binding!));

    private void Prune()
    {
        long now = Stopwatch.GetTimestamp();
        while (_expirations.TryPeek(out Guid id, out long expires) && expires <= now)
        {
            _expirations.Dequeue();
            _authorized.Remove(id);
        }
    }

    private sealed class PublicationLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    internal sealed class Call(CloudMetadataEchoAuthority owner, Guid id) : IDisposable
    {
        private readonly HashSet<(Guid ItemId, CloudLocalFileBinding Binding)> _objects = [];
        private readonly List<string> _committedOutcomes = [];
        internal Guid Id => id;
        internal bool HasMetadataPreparation { get; private set; }
        internal IReadOnlyList<string> CommittedOutcomes => _committedOutcomes;

        internal void TrackPreparation()
        {
            HasMetadataPreparation = true;
            owner.TrackReconciliationChange();
        }

        internal void RecordCommittedOutcome(RemoteMetadataIntent? intent)
        {
            if (intent is not null) { _committedOutcomes.Add(intent.Name); }
        }

        internal void Record(Guid itemId, CloudLocalFileBinding binding) => _objects.Add((itemId, binding));
        internal void Authorize(CloudRemoteApplyOptions options)
        {
            if (_objects.Count == 0 || !options.SuppressLocalEcho || options.AttributeEchoPolicy != CloudRemoteAttributeEchoPolicy.CallerCoordinated) { return; }
            lock (owner._gate)
            {
                // A wall-clock adjustment must never prolong discard authority beyond the
                // duration for which the caller promised to exclude competing writers.
                long expires = checked(Stopwatch.GetTimestamp() +
                    (long)(options.EchoSuppressionLifetime.TotalSeconds * Stopwatch.Frequency));
                owner._authorized.Add(id, new(_objects));
                owner._expirations.Enqueue(id, expires);
            }
        }
        public void Dispose()
        {
            lock (owner._gate) { owner._applying.Remove(id); }
        }
    }
}
