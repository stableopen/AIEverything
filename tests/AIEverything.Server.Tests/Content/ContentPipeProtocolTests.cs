using System.Buffers.Binary;
using System.Text;
using AIEverything.Content.Contracts;
using AIEverything.Content.Errors;
using AIEverything.Content.Ipc;

namespace AIEverything.Server.Tests.Content;

public sealed class ContentPipeProtocolTests
{
    [Fact]
    public async Task Round_trip_uses_four_byte_little_endian_camel_case_json()
    {
        await using var stream = new MemoryStream();
        var value = new ContentSearchRequest("正文搜索", Limit: 7);

        await ContentPipeProtocol.WriteAsync(stream, value, CancellationToken.None);
        var bytes = stream.ToArray();
        stream.Position = 0;
        var result = await ContentPipeProtocol.ReadAsync<ContentSearchRequest>(
            stream, CancellationToken.None);

        Assert.Equal(bytes.Length - 4, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4)));
        Assert.Contains("\"query\"", Encoding.UTF8.GetString(bytes.AsSpan(4)), StringComparison.Ordinal);
        Assert.Equal(value, result);
    }

    [Fact]
    public async Task Read_handles_partial_stream_reads()
    {
        await using var source = new MemoryStream();
        await ContentPipeProtocol.WriteAsync(
            source,
            new ContentSearchRequest("partial"),
            CancellationToken.None);
        await using var chunks = new ChunkedReadStream(source.ToArray(), 1);

        var result = await ContentPipeProtocol.ReadAsync<ContentSearchRequest>(
            chunks, CancellationToken.None);

        Assert.Equal("partial", result.Query);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ContentPipeProtocol.MaxMessageBytes + 1)]
    public async Task Read_rejects_invalid_lengths(int length)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
        await using var stream = new MemoryStream(prefix);

        var exception = await Assert.ThrowsAsync<ContentIndexException>(() =>
            ContentPipeProtocol.ReadAsync<ContentSearchRequest>(stream, CancellationToken.None));

        Assert.Equal(ContentErrorCodes.InvalidArguments, exception.Code);
    }

    [Fact]
    public async Task Write_rejects_payloads_above_one_megabyte()
    {
        await using var stream = new MemoryStream();
        var value = new ContentSearchRequest(new string('x', ContentPipeProtocol.MaxMessageBytes));

        var exception = await Assert.ThrowsAsync<ContentIndexException>(() =>
            ContentPipeProtocol.WriteAsync(stream, value, CancellationToken.None));

        Assert.Equal(ContentErrorCodes.InvalidArguments, exception.Code);
    }

    [Fact]
    public async Task Read_rejects_malformed_json()
    {
        var json = Encoding.UTF8.GetBytes("{bad json}");
        var bytes = new byte[json.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, 4), json.Length);
        json.CopyTo(bytes.AsSpan(4));
        await using var stream = new MemoryStream(bytes);

        var exception = await Assert.ThrowsAsync<ContentIndexException>(() =>
            ContentPipeProtocol.ReadAsync<ContentSearchRequest>(stream, CancellationToken.None));

        Assert.Equal(ContentErrorCodes.InvalidArguments, exception.Code);
    }

    [Fact]
    public void Pipe_name_is_deterministic_scoped_and_safe()
    {
        var first = ContentPipeNaming.ForCurrentUser();
        var second = ContentPipeNaming.ForCurrentUser();

        Assert.Equal(first, second);
        Assert.StartsWith("aieverything-content-", first, StringComparison.Ordinal);
        Assert.DoesNotContain('\\', first);
        Assert.DoesNotContain('/', first);
        Assert.InRange(first.Length, 30, 80);
    }

    private sealed class ChunkedReadStream(byte[] bytes, int chunkSize) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunkSize)], cancellationToken);
    }
}
