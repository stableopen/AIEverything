using System.IO.Pipes;
using System.Text.Json;
using AIEverything.Content.Contracts;
using AIEverything.Content.Errors;
using AIEverything.Content.Ipc;

namespace AIEverything.ContentClient;

public sealed class ContentDaemonClient : IContentSearchService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;

    public ContentDaemonClient(string pipeName, TimeSpan connectTimeout)
    {
        _pipeName = pipeName;
        _connectTimeout = connectTimeout > TimeSpan.Zero
            ? connectTimeout
            : throw new ArgumentOutOfRangeException(nameof(connectTimeout));
    }

    public Task<ContentSearchResponse> SearchAsync(
        ContentSearchRequest request,
        CancellationToken cancellationToken = default) =>
        SendAsync<ContentSearchRequest, ContentSearchResponse>(
            "content.search", request, cancellationToken);

    public Task<ContentIndexStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        SendAsync<object, ContentIndexStatus>("index.status", new { }, cancellationToken);

    public Task<IReadOnlyList<ContentIndexFailure>> ListFailuresAsync(
        CancellationToken cancellationToken = default) =>
        SendAsync<object, IReadOnlyList<ContentIndexFailure>>(
            "index.failures", new { }, cancellationToken);

    public Task<ContentIndexStatus> ConfigureAsync(
        bool disclosureAccepted,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        SendAsync<IndexConfigurationRequest, ContentIndexStatus>(
            "index.configure", new IndexConfigurationRequest(disclosureAccepted, enabled), cancellationToken);

    public Task<ContentIndexStatus> SetPausedAsync(
        bool paused,
        CancellationToken cancellationToken = default) =>
        SendAsync<IndexControlRequest, ContentIndexStatus>(
            paused ? "index.pause" : "index.resume",
            new IndexControlRequest(),
            cancellationToken);

    public Task<ContentIndexStatus> SynchronizeAsync(CancellationToken cancellationToken = default) =>
        SendAsync<IndexControlRequest, ContentIndexStatus>(
            "index.sync", new IndexControlRequest(), cancellationToken);

    private async Task<TResult> SendAsync<TPayload, TResult>(
        string operation,
        TPayload payload,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            var timeoutMilliseconds = checked((int)Math.Min(
                int.MaxValue, Math.Ceiling(_connectTimeout.TotalMilliseconds)));
            await pipe.ConnectAsync(timeoutMilliseconds, cancellationToken);
            await ContentPipeProtocol.WriteAsync(
                pipe,
                new ContentDaemonRequest(
                    operation,
                    JsonSerializer.SerializeToElement(payload)),
                cancellationToken);
            var response = await ContentPipeProtocol.ReadAsync<ContentDaemonResponse>(
                pipe, cancellationToken);
            if (!response.Success || response.Result is null)
            {
                throw new ContentIndexException(
                    response.Error?.Code ?? ContentErrorCodes.ServiceUnavailable,
                    response.Error?.Message ?? "Content daemon request failed.",
                    response.Error?.CorrectiveAction ?? "Check daemon status and retry.");
            }

            return response.Result.Value.Deserialize<TResult>(JsonOptions) ??
                   throw new ContentIndexException(
                       ContentErrorCodes.ServiceUnavailable,
                       "Content daemon returned an empty result.",
                       "Restart the content daemon and retry.");
        }
        catch (ContentIndexException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            throw new ContentIndexException(
                ContentErrorCodes.ServiceUnavailable,
                "AIEverything content daemon is unavailable.",
                "Start AIEverything.Daemon.exe run and retry.",
                exception);
        }
    }
}
