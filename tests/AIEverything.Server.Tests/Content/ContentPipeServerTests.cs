using System.IO.Pipes;
using System.Text.Json;
using AIEverything.Content.Ipc;
using AIEverything.Daemon;

namespace AIEverything.Server.Tests.Content;

public sealed class ContentPipeServerTests
{
    [Fact]
    public async Task Disconnected_client_does_not_stop_the_accept_loop()
    {
        var pipeName = $"aieverything-pipe-server-{Guid.NewGuid():N}";
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new ContentPipeServer(pipeName, async (request, _) =>
        {
            if (request.Operation == "slow")
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
            }

            return new { operation = request.Operation };
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = server.RunAsync(cancellation.Token);

        await using (var first = new NamedPipeClientStream(
                         ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await first.ConnectAsync(3000, cancellation.Token);
            await ContentPipeProtocol.WriteAsync(
                first,
                new ContentDaemonRequest("slow", JsonSerializer.SerializeToElement(new { })),
                cancellation.Token);
            await firstStarted.Task.WaitAsync(cancellation.Token);
        }

        releaseFirst.TrySetResult();
        await Task.Delay(100, cancellation.Token);

        await using (var second = new NamedPipeClientStream(
                         ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await second.ConnectAsync(3000, cancellation.Token);
            await ContentPipeProtocol.WriteAsync(
                second,
                new ContentDaemonRequest("status", JsonSerializer.SerializeToElement(new { })),
                cancellation.Token);
            var response = await ContentPipeProtocol.ReadAsync<ContentDaemonResponse>(
                second, cancellation.Token);

            Assert.True(response.Success);
            Assert.Equal(
                "status",
                response.Result?.GetProperty("operation").GetString());
        }

        cancellation.Cancel();
        await serverTask;
    }
}
