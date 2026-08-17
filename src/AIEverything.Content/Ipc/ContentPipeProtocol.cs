using System.Buffers.Binary;
using System.Text.Json;
using AIEverything.Content.Errors;

namespace AIEverything.Content.Ipc;

public static class ContentPipeProtocol
{
    public const int MaxMessageBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static async Task WriteAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        ValidateLength(payload.Length);
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(
        Stream stream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var prefix = new byte[4];
        try
        {
            await stream.ReadExactlyAsync(prefix, cancellationToken);
        }
        catch (EndOfStreamException exception)
        {
            throw InvalidMessage("Message length prefix is incomplete.", exception);
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        ValidateLength(length);
        var payload = new byte[length];
        try
        {
            await stream.ReadExactlyAsync(payload, cancellationToken);
            return JsonSerializer.Deserialize<T>(payload, JsonOptions) ??
                   throw InvalidMessage("Message JSON was null.");
        }
        catch (EndOfStreamException exception)
        {
            throw InvalidMessage("Message payload is incomplete.", exception);
        }
        catch (JsonException exception)
        {
            throw InvalidMessage("Message JSON is malformed.", exception);
        }
    }

    private static void ValidateLength(int length)
    {
        if (length is < 1 or > MaxMessageBytes)
        {
            throw InvalidMessage(
                $"Message length must be between 1 and {MaxMessageBytes} bytes; received {length}.");
        }
    }

    private static ContentIndexException InvalidMessage(
        string message,
        Exception? innerException = null) => new(
            ContentErrorCodes.InvalidArguments,
            message,
            "Send one length-prefixed UTF-8 JSON message no larger than 1 MiB.",
            innerException);
}
