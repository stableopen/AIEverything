using System.IO.Pipes;
using System.Text.Json;
using AIEverything.Content.Errors;
using AIEverything.Content.Ipc;

namespace AIEverything.Daemon;

public sealed class ContentPipeServer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _pipeName;
    private readonly Func<ContentDaemonRequest, CancellationToken, Task<object>> _handler;

    public ContentPipeServer(
        string pipeName,
        Func<ContentDaemonRequest, CancellationToken, Task<object>> handler)
    {
        _pipeName = pipeName;
        _handler = handler;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                await HandleOneAsync(pipe, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // A desktop client can time out or close while a request is still
                // finishing. Keep the per-user server alive for the next request.
            }
        }
    }

    private async Task HandleOneAsync(Stream pipe, CancellationToken cancellationToken)
    {
        ContentDaemonResponse response;
        try
        {
            var request = await ContentPipeProtocol.ReadAsync<ContentDaemonRequest>(
                pipe, cancellationToken);
            if (string.IsNullOrWhiteSpace(request.Operation))
            {
                throw new ContentIndexException(
                    ContentErrorCodes.InvalidArguments,
                    "Daemon operation is required.",
                    "Send a documented daemon operation.");
            }

            var result = await _handler(request, cancellationToken);
            response = new ContentDaemonResponse(
                true,
                JsonSerializer.SerializeToElement(result, JsonOptions),
                null);
        }
        catch (ContentIndexException exception)
        {
            response = Failure(exception.Code, exception.Message, exception.CorrectiveAction);
        }
        catch (JsonException exception)
        {
            response = Failure(
                ContentErrorCodes.InvalidArguments,
                exception.Message,
                "Send valid camelCase JSON payload fields.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            response = Failure(
                ContentErrorCodes.ServiceUnavailable,
                exception.Message,
                "Check daemon status and retry.");
        }

        await ContentPipeProtocol.WriteAsync(pipe, response, cancellationToken);
    }

    private static ContentDaemonResponse Failure(
        string code,
        string message,
        string correctiveAction) =>
        new(false, null, new ContentDaemonError(code, message, correctiveAction));
}
