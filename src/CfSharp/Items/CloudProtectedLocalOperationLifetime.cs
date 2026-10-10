using System.Diagnostics;

namespace CfSharp;

public sealed partial class CloudProtectedLocalOperationRequest
{
    /// <summary>Creates an immutable copy with a short cooperative operation budget.</summary>
    /// <param name="budget">Positive duration no greater than one minute, including path admission.</param>
    /// <returns>A request preserving its original binding, mode and optional preparation.</returns>
    /// <remarks>
    /// Expiry stops new scope steps and signals the callback, but does not forcibly abort native
    /// calls or application code. The library still drains actual work before releasing resources.
    /// Keep callbacks local; the budget cannot guarantee immediate return from an uncooperative
    /// callback or a stalled native driver. Query the context's CurrentStage and IsDraining while
    /// awaiting completion to observe retained protection during cancellation.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The budget is nonpositive or exceeds one minute.</exception>
    public CloudProtectedLocalOperationRequest WithBudget(TimeSpan budget)
    {
        if (budget <= TimeSpan.Zero || budget > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(budget), "Use a positive short budget of at most one minute.");
        }
        return new(ExpectedBinding, Mode) { PreparationIdentity = PreparationIdentity, Budget = budget };
    }

    /// <summary>Gets the cooperative admission and callback budget; the default is ten seconds.</summary>
    /// <remarks>Expiry never releases a native object still in use by admitted work.</remarks>
    public TimeSpan Budget { get; private init; } = TimeSpan.FromSeconds(10);
}

internal sealed class CloudProtectedLocalOperationLifetime : IDisposable
{
    private readonly object _cancellation = new();
    private readonly CancellationTokenSource _callback = new();
    private readonly CancellationTokenSource _deadline = new();
    private readonly CancellationTokenSource _signals;
    private readonly CancellationTokenRegistration _registration;
    private readonly CancellationToken _caller;
    private readonly CancellationToken _owner;
    private Task _canceling = Task.CompletedTask;

    internal CloudProtectedLocalOperationLifetime(CloudProtectedLocalOperationRequest request, long started,
        CancellationToken caller, CancellationToken owner)
    {
        _caller = caller;
        _owner = owner;
        TimeSpan remaining = request.Budget - Stopwatch.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero)
        {
            _deadline.Cancel();
        }
        else
        {
            _deadline.CancelAfter(remaining);
        }
        _signals = CancellationTokenSource.CreateLinkedTokenSource(caller, owner, _deadline.Token);
        // Application cancellation registrations must not run under the owner's lifecycle
        // lock or throw through its stopping source. CancelAsync still marks the exposed token
        // immediately, while its registered handlers execute separately and are drained below.
        _registration = _signals.Token.UnsafeRegister(static state => ((CloudProtectedLocalOperationLifetime)state!).RequestStop(), this);
    }

    internal CancellationToken Token => _callback.Token;
    internal bool IsCancellationRequested => _callback.IsCancellationRequested || _caller.IsCancellationRequested ||
        _owner.IsCancellationRequested || _deadline.IsCancellationRequested;
    internal bool TimedOut => _deadline.IsCancellationRequested && !_caller.IsCancellationRequested && !_owner.IsCancellationRequested;
    internal bool BudgetExpired => _deadline.IsCancellationRequested;
    internal Exception? CancellationCallbackError { get; private set; }

    internal void ThrowIfCancellationRequested()
    {
        _caller.ThrowIfCancellationRequested();
        _owner.ThrowIfCancellationRequested();
        _deadline.Token.ThrowIfCancellationRequested();
        _callback.Token.ThrowIfCancellationRequested();
    }

    private void RequestStop()
    {
        lock (_cancellation)
        {
            _canceling = _callback.CancelAsync();
        }
    }

    internal async ValueTask SealAndDrainCancellationAsync()
    {
        // Stop forwarding new signals once callback/scope admission is closed. Dispose waits
        // only for our short bridge, then await real registered application handlers while the
        // native object and store lease are still alive.
        _registration.Dispose();
        Task pending;
        lock (_cancellation)
        {
            pending = _canceling;
        }
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            CancellationCallbackError = error;
        }
    }

    public void Dispose()
    {
        _registration.Dispose();
        _signals.Dispose();
        _deadline.Dispose();
        _callback.Dispose();
    }
}
