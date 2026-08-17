using System.Collections.Concurrent;
using AIEverything.Desktop.Ranking;

namespace AIEverything.Server.Tests.Desktop;

public sealed class LatestPendingWorkSchedulerTests
{
    [Fact]
    public async Task Running_work_and_only_the_latest_pending_work_execute()
    {
        using var releaseFirst = new ManualResetEventSlim();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new ConcurrentQueue<string>();
        await using var scheduler = new LatestPendingWorkScheduler<string, SchedulerResult>(
            request =>
            {
                executed.Enqueue(request);
                if (request == "first")
                {
                    firstStarted.TrySetResult();
                    Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(5)));
                }

                return new SchedulerResult(request, null);
            },
            TimeSpan.FromSeconds(2),
            (_, detail) => new SchedulerResult(null, detail));

        var first = scheduler.EnqueueAsync("first");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var replaced = scheduler.EnqueueAsync("replaced");
        var latest = scheduler.EnqueueAsync("latest");

        var replacedResult = await replaced.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Contains("Superseded", replacedResult.FallbackDetail, StringComparison.Ordinal);
        releaseFirst.Set();

        Assert.Equal("first", (await first).Value);
        Assert.Equal("latest", (await latest).Value);
        Assert.Equal(["first", "latest"], executed);
    }

    [Fact]
    public async Task Queue_wait_counts_toward_the_deadline_and_expired_pending_work_never_executes()
    {
        using var releaseFirst = new ManualResetEventSlim();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new ConcurrentQueue<string>();
        var timeout = TimeSpan.FromMilliseconds(80);
        await using var scheduler = new LatestPendingWorkScheduler<string, SchedulerResult>(
            request =>
            {
                executed.Enqueue(request);
                if (request == "first")
                {
                    firstStarted.TrySetResult();
                    Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(5)));
                }

                return new SchedulerResult(request, null);
            },
            timeout,
            (_, detail) => new SchedulerResult(null, detail));

        var first = scheduler.EnqueueAsync("first");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var pending = scheduler.EnqueueAsync("expired-pending");

        var pendingResult = await pending.WaitAsync(TimeSpan.FromSeconds(1));
        stopwatch.Stop();
        Assert.Contains("deadline", pendingResult.FallbackDetail, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(stopwatch.ElapsedMilliseconds, 40, 500);
        Assert.DoesNotContain("expired-pending", executed);

        releaseFirst.Set();
        Assert.Contains("deadline", (await first).FallbackDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancellation_removes_pending_work_without_invoking_the_executor()
    {
        using var releaseFirst = new ManualResetEventSlim();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executed = new ConcurrentQueue<string>();
        await using var scheduler = new LatestPendingWorkScheduler<string, SchedulerResult>(
            request =>
            {
                executed.Enqueue(request);
                if (request == "first")
                {
                    firstStarted.TrySetResult();
                    Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(5)));
                }

                return new SchedulerResult(request, null);
            },
            TimeSpan.FromSeconds(2),
            (_, detail) => new SchedulerResult(null, detail));

        var first = scheduler.EnqueueAsync("first");
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var pending = scheduler.EnqueueAsync("canceled", cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        releaseFirst.Set();
        Assert.Equal("first", (await first).Value);
        Assert.Equal(["first"], executed);
    }

    private sealed record SchedulerResult(string? Value, string? FallbackDetail);
}
