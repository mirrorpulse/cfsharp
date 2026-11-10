namespace CfSharp;

public sealed partial class CloudFileSystem
{
    // Metadata-only protocols need disposal admission without a namespace path lease or
    // ancestor handle. SQLite/custom transaction isolation protects their durable state;
    // they cannot use this lease to issue a native namespace mutation.
    private async ValueTask<CloudFileSystemOperationLease> AcquireStateOperationAsync(CancellationToken cancellationToken)
    {
        RejectProtectedLocalReentry();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureStarted();
            ICloudStateStore store = _stateStore ?? throw new InvalidOperationException("The cloud file system has no open state store.");
            if (Volatile.Read(ref _activeOperations) == 0)
            {
                Volatile.Write(ref _operationsDrained, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }
            Interlocked.Increment(ref _activeOperations);
            return new(this, store, null, null, []);
        }
        finally { _lifecycleGate.Release(); }
    }
}
