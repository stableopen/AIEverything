using System.Diagnostics;

namespace AIEverything.Desktop.Ranking;

internal sealed class LatestPendingWorkScheduler<TRequest, TResult> : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly Func<TRequest, TResult> _execute;
    private readonly TimeSpan _deadline;
    private readonly Func<TimeSpan, string, TResult> _fallback;
    private WorkItem? _pending;
    private TaskCompletionSource? _idle;
    private bool _workerRunning;
    private bool _disposed;

    internal LatestPendingWorkScheduler(
        Func<TRequest, TResult> execute,
        TimeSpan deadline,
        Func<TimeSpan, string, TResult> fallback)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        if (deadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(deadline));
        }

        _deadline = deadline;
    }

    internal Task<TResult> EnqueueAsync(
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = new WorkItem(request);
        WorkItem? replaced = null;
        var startWorker = false;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_workerRunning)
            {
                _workerRunning = true;
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                startWorker = true;
            }
            else
            {
                replaced = _pending;
                _pending = item;
            }
        }

        if (replaced is not null)
        {
            CompleteFallback(replaced, "Superseded by a newer local rerank request.");
        }

        _ = ExpireAsync(item);
        if (startWorker)
        {
            _ = Task.Run(() => ProcessLoop(item));
        }

        return AwaitWithCancellationAsync(item, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        WorkItem? pending;
        Task idle;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = _pending;
            _pending = null;
            idle = _idle?.Task ?? Task.CompletedTask;
        }

        pending?.Completion.TrySetException(new ObjectDisposedException(GetType().Name));
        await idle.ConfigureAwait(false);
    }

    private async Task<TResult> AwaitWithCancellationAsync(
        WorkItem item,
        CancellationToken cancellationToken)
    {
        using var registration = cancellationToken.Register(() => Cancel(item, cancellationToken));
        return await item.Completion.Task.ConfigureAwait(false);
    }

    private void Cancel(WorkItem item, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (ReferenceEquals(_pending, item))
            {
                _pending = null;
            }
        }

        item.Completion.TrySetCanceled(cancellationToken);
    }

    private async Task ExpireAsync(WorkItem item)
    {
        await Task.Delay(_deadline).ConfigureAwait(false);
        lock (_sync)
        {
            if (ReferenceEquals(_pending, item))
            {
                _pending = null;
            }
        }

        CompleteFallback(item, "Local model rerank deadline expired while queued or running.");
    }

    private void ProcessLoop(WorkItem current)
    {
        while (true)
        {
            if (!current.Completion.Task.IsCompleted)
            {
                try
                {
                    current.Completion.TrySetResult(_execute(current.Request));
                }
                catch (Exception exception)
                {
                    current.Completion.TrySetException(exception);
                }
            }

            TaskCompletionSource? idle = null;
            lock (_sync)
            {
                if (_pending is { } next)
                {
                    _pending = null;
                    current = next;
                }
                else
                {
                    _workerRunning = false;
                    idle = _idle;
                    _idle = null;
                }
            }

            if (idle is not null)
            {
                idle.TrySetResult();
                return;
            }
        }
    }

    private void CompleteFallback(WorkItem item, string detail)
    {
        if (item.Completion.Task.IsCompleted)
        {
            return;
        }

        try
        {
            item.Completion.TrySetResult(
                _fallback(Stopwatch.GetElapsedTime(item.EnqueuedTimestamp), detail));
        }
        catch (Exception exception)
        {
            item.Completion.TrySetException(exception);
        }
    }

    private sealed class WorkItem(TRequest request)
    {
        internal TRequest Request { get; } = request;
        internal long EnqueuedTimestamp { get; } = Stopwatch.GetTimestamp();
        internal TaskCompletionSource<TResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
