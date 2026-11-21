using System.Diagnostics;

namespace CfSharp;

// A durable receipt records what happened, not a recovered caller's promise about other writers.
// Only a successful public call on this owner can activate that promise in memory.
internal sealed class CloudMetadataEchoAuthority
{
    private readonly object _gate = new();
    private readonly HashSet<Guid> _applying = [];
    private readonly Dictionary<Guid, HashSet<(Guid ItemId, CloudLocalFileBinding Binding)>> _authorized = [];
    private readonly PriorityQueue<Guid, long> _expirations = new();

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

    internal bool IsAuthorized(RemoteMetadataIntent intent)
    {
        if (intent.Binding is not { } binding || !intent.Coordinated || intent.Passthrough || intent.Superseded ||
            intent.Reconciled || intent.RequiresReconciliation || intent.HasUnconfirmedOutcome)
        {
            return false;
        }

        lock (_gate)
        {
            Prune();
            return _authorized.TryGetValue(intent.CallId, out HashSet<(Guid ItemId, CloudLocalFileBinding Binding)>? objects) &&
                objects.Contains((intent.ItemId, binding));
        }
    }

    private void Prune()
    {
        long now = Stopwatch.GetTimestamp();
        while (_expirations.TryPeek(out Guid id, out long expires) && expires <= now)
        {
            _expirations.Dequeue();
            _authorized.Remove(id);
        }
    }

    internal sealed class Call(CloudMetadataEchoAuthority owner, Guid id) : IDisposable
    {
        private readonly HashSet<(Guid ItemId, CloudLocalFileBinding Binding)> _objects = [];
        private readonly List<string> _committedOutcomes = [];
        internal Guid Id => id;
        internal bool HasMetadataPreparation { get; private set; }
        internal IReadOnlyList<string> CommittedOutcomes => _committedOutcomes;

        internal void TrackPreparation() => HasMetadataPreparation = true;

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
