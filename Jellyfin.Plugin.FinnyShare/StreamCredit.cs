namespace Jellyfin.Plugin.FinnyShare;

/// <summary>
/// One stream's flow-control window, as the sender sees it.
///
/// The relay buffers only what it has granted and never blocks its shared read loop, so a
/// viewer who stops reading simply stops being granted credit. That confines the slowdown to
/// that viewer's own stream instead of backing up the socket and stalling everyone else
/// behind the plugin's single send lock.
///
/// One waiter at a time is all this needs: a stream has exactly one producer, the task
/// pumping that response.
/// </summary>
public sealed class StreamCredit
{
    private readonly object _gate = new();
    private long _available;
    private TaskCompletionSource? _waiter;

    /// <summary>Records room the relay has opened for this stream.</summary>
    public void Grant(int bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        TaskCompletionSource? wake;
        lock (_gate)
        {
            _available += bytes;
            wake = _waiter;
            _waiter = null;
        }

        // Outside the lock: a continuation must never run while we hold it.
        wake?.TrySetResult();
    }

    /// <summary>
    /// Waits until the relay has room for <paramref name="bytes"/>, consuming it. Returns
    /// false if the stream was cancelled first, which is the caller's cue to stop reading
    /// Jellyfin rather than to send anyway.
    /// </summary>
    public async ValueTask<bool> WaitAsync(int bytes, CancellationToken ct)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_available >= bytes)
                {
                    _available -= bytes;
                    return true;
                }

                // RunContinuationsAsynchronously: without it Grant's caller - the tunnel's
                // single receive loop - would run this stream's send inline and stop
                // reading frames for every other stream.
                _waiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _waiter.Task;
            }

            try
            {
                await wait.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }
}
