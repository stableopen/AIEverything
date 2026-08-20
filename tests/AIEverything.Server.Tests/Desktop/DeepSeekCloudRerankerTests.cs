using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIEverything.Desktop.Ranking;

namespace AIEverything.Server.Tests.Desktop;

public sealed class DeepSeekCloudRerankerTests
{
    [Fact]
    public async Task Missing_credentials_never_send_an_http_request()
    {
        var handler = new CapturingHandler(_ => throw new InvalidOperationException("Must not send."));
        using var client = new HttpClient(handler);
        var reranker = new DeepSeekCloudReranker(client, new FakeCredentials(null));

        var result = await reranker.RerankAsync(Request());

        Assert.Null(result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Sends_only_the_bounded_candidate_payload_and_parses_strict_ids()
    {
        JsonDocument? sent = null;
        AuthenticationHeaderValue? authorization = null;
        var handler = new CapturingHandler(async request =>
        {
            authorization = request.Headers.Authorization;
            sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            return JsonResponse("""
                {"choices":[{"message":{"content":"{\"top5_ids\":[\"c2\",\"c0\"]}"}}]}
                """);
        });
        using var client = new HttpClient(handler);
        var reranker = new DeepSeekCloudReranker(client, new FakeCredentials("unit-test-key"));

        var result = await reranker.RerankAsync(Request());

        Assert.Equal(["c2", "c0"], Assert.IsType<CloudRerankResult>(result).TopFiveIds);
        Assert.Equal("Bearer", authorization?.Scheme);
        Assert.Equal("unit-test-key", authorization?.Parameter);
        using (sent)
        {
            var root = sent!.RootElement;
            Assert.Equal("deepseek-v4-flash", root.GetProperty("model").GetString());
            Assert.False(root.GetProperty("stream").GetBoolean());
            Assert.Equal(0, root.GetProperty("temperature").GetDouble());
            Assert.Equal("disabled", root.GetProperty("thinking").GetProperty("type").GetString());
            Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
            Assert.Equal(256, root.GetProperty("max_tokens").GetInt32());
            var messages = root.GetProperty("messages");
            Assert.Equal(2, messages.GetArrayLength());
            Assert.Contains("untrusted data", messages[0].GetProperty("content").GetString(),
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("match_source", messages[0].GetProperty("content").GetString(),
                StringComparison.Ordinal);
            Assert.Contains("top5_ids", messages[0].GetProperty("content").GetString(),
                StringComparison.Ordinal);
            using var userPayload = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
            Assert.Equal("needle", userPayload.RootElement.GetProperty("query").GetString());
            var candidates = userPayload.RootElement.GetProperty("candidates");
            Assert.Equal(3, candidates.GetArrayLength());
            Assert.Equal("c0", candidates[0].GetProperty("id").GetString());
            Assert.Equal("one.md", candidates[0].GetProperty("name").GetString());
            Assert.Equal(@"D:\docs\one.md", candidates[0].GetProperty("full_path").GetString());
            Assert.Equal("short snippet", candidates[0].GetProperty("snippet").GetString());
            Assert.False(candidates[0].TryGetProperty("match_source", out _));
            Assert.False(candidates[1].TryGetProperty("match_source", out _));
            Assert.False(candidates[2].TryGetProperty("match_source", out _));
            Assert.False(candidates[0].TryGetProperty("protected_tier", out _));
        }
    }

    [Theory]
    [InlineData("{\"top5_ids\":[\"unknown\"]}")]
    [InlineData("{\"top5_ids\":[\"c0\",\"c0\"]}")]
    public async Task Unknown_or_duplicate_ids_reject_the_entire_cloud_result(string content)
    {
        var handler = new CapturingHandler(_ => Task.FromResult(JsonResponse(
            JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content } } }
            }))));
        using var client = new HttpClient(handler);
        var reranker = new DeepSeekCloudReranker(client, new FakeCredentials("unit-test-key"));

        Assert.Null(await reranker.RerankAsync(Request()));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Utf8_request_larger_than_24_kib_is_rejected_before_credentials_or_network()
    {
        var handler = new CapturingHandler(_ => throw new InvalidOperationException("Must not send."));
        using var client = new HttpClient(handler);
        var credentials = new FakeCredentials("unit-test-key");
        var reranker = new DeepSeekCloudReranker(client, credentials);
        var oversized = Request() with { Query = new string('界', 9000) };

        Assert.Null(await reranker.RerankAsync(oversized));
        Assert.Equal(0, credentials.CallCount);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "{}")]
    [InlineData(HttpStatusCode.OK, "not-json")]
    [InlineData(HttpStatusCode.OK, "{\"choices\":[]}")]
    public async Task Http_and_response_failures_return_no_cloud_order(
        HttpStatusCode status,
        string body)
    {
        var handler = new CapturingHandler(_ => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        }));
        using var client = new HttpClient(handler);
        var reranker = new DeepSeekCloudReranker(client, new FakeCredentials("unit-test-key"));

        Assert.Null(await reranker.RerankAsync(Request()));
    }

    [Fact]
    public async Task Network_failures_fall_back_but_user_cancellation_propagates()
    {
        using var failingClient = new HttpClient(new CapturingHandler(
            _ => throw new HttpRequestException("offline")));
        var failing = new DeepSeekCloudReranker(
            failingClient, new FakeCredentials("unit-test-key"));
        Assert.Null(await failing.RerankAsync(Request()));

        using var cancelledClient = new HttpClient(new CapturingHandler(async request =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, request.GetCancellationToken());
            return JsonResponse("{}");
        }));
        var cancelled = new DeepSeekCloudReranker(
            cancelledClient, new FakeCredentials("unit-test-key"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cancelled.RerankAsync(Request(), cancellation.Token));
    }

    [Fact]
    public async Task Total_deadline_cancels_the_http_request_within_one_budget()
    {
        var handler = new CapturingHandler(async request =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, request.GetCancellationToken());
            return ValidResponse();
        });
        using var client = new HttpClient(handler);
        var reranker = new DeepSeekCloudReranker(
            client,
            new FakeCredentials("unit-test-key"),
            TimeSpan.FromMilliseconds(80));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        Assert.Null(await reranker.RerankAsync(Request()));

        stopwatch.Stop();
        Assert.InRange(stopwatch.ElapsedMilliseconds, 40, 500);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Requests_are_strictly_single_concurrency()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        var observedCalls = 0;
        var handler = new CapturingHandler(async _ =>
        {
            var current = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, current);
            try
            {
                if (current == 1 && handlerCallNumber() == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }

                return ValidResponse();
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        int handlerCallNumber() => Interlocked.Increment(ref observedCalls);
        using var client = new HttpClient(handler);
        var reranker = new DeepSeekCloudReranker(
            client,
            new FakeCredentials("unit-test-key"),
            TimeSpan.FromSeconds(2));

        var first = reranker.RerankAsync(Request("first query")).AsTask();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = reranker.RerankAsync(Request("second query")).AsTask();
        await Task.Delay(50);
        Assert.Equal(1, handler.CallCount);
        releaseFirst.TrySetResult();

        Assert.NotNull(await first);
        Assert.NotNull(await second);
        Assert.Equal(2, handler.CallCount);
        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task Ten_requests_per_rolling_minute_are_allowed_and_the_eleventh_is_blocked()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-14T09:00:00Z"));
        var handler = new CapturingHandler(_ => Task.FromResult(ValidResponse()));
        using var client = new HttpClient(handler);
        var reranker = new DeepSeekCloudReranker(
            client,
            new FakeCredentials("unit-test-key"),
            timeProvider: clock);

        for (var index = 0; index < 10; index++)
        {
            Assert.NotNull(await reranker.RerankAsync(Request($"query {index}")));
        }

        Assert.Null(await reranker.RerankAsync(Request("query 10")));
        Assert.Equal(10, handler.CallCount);
    }

    [Fact]
    public async Task Successful_results_are_cached_in_session_for_ten_minutes()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-14T09:00:00Z"));
        var handler = new CapturingHandler(_ => Task.FromResult(ValidResponse()));
        using var client = new HttpClient(handler);
        var reranker = new DeepSeekCloudReranker(
            client,
            new FakeCredentials("unit-test-key"),
            timeProvider: clock);

        Assert.NotNull(await reranker.RerankAsync(Request()));
        Assert.NotNull(await reranker.RerankAsync(Request()));
        Assert.Equal(1, handler.CallCount);

        clock.Advance(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(1));
        Assert.NotNull(await reranker.RerankAsync(Request()));
        Assert.Equal(2, handler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Authentication_failure_disables_cloud_for_the_remainder_of_the_session(
        HttpStatusCode status)
    {
        var credentials = new FakeCredentials("unit-test-key");
        var handler = new CapturingHandler(_ => Task.FromResult(new HttpResponseMessage(status)));
        using var client = new HttpClient(handler);
        var reranker = new DeepSeekCloudReranker(client, credentials);

        Assert.Null(await reranker.RerankAsync(Request("first")));
        Assert.Null(await reranker.RerankAsync(Request("second")));
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(1, credentials.CallCount);
    }

    [Fact]
    public async Task Rate_limit_and_consecutive_service_errors_open_a_short_circuit_without_retry()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-14T09:00:00Z"));
        var statuses = new Queue<HttpStatusCode>(
            [HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError,
                HttpStatusCode.BadGateway, HttpStatusCode.OK]);
        var handler = new CapturingHandler(_ => Task.FromResult(
            statuses.Peek() == HttpStatusCode.OK
                ? ConsumeValidResponse(statuses)
                : new HttpResponseMessage(statuses.Dequeue())));
        using var client = new HttpClient(handler);
        var reranker = new DeepSeekCloudReranker(
            client,
            new FakeCredentials("unit-test-key"),
            timeProvider: clock);

        Assert.Null(await reranker.RerankAsync(Request("rate limited")));
        Assert.Null(await reranker.RerankAsync(Request("blocked after 429")));
        Assert.Equal(1, handler.CallCount);
        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.Null(await reranker.RerankAsync(Request("service error one")));
        Assert.Null(await reranker.RerankAsync(Request("service error two")));
        Assert.Null(await reranker.RerankAsync(Request("blocked after errors")));
        Assert.Equal(3, handler.CallCount);
        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.NotNull(await reranker.RerankAsync(Request("recovered")));
        Assert.Equal(4, handler.CallCount);
    }

    [Fact]
    public async Task Ordinary_client_error_breaks_a_consecutive_service_failure_sequence()
    {
        var statuses = new Queue<HttpStatusCode>(
            [HttpStatusCode.InternalServerError, HttpStatusCode.BadRequest,
                HttpStatusCode.BadGateway, HttpStatusCode.OK]);
        var handler = new CapturingHandler(_ => Task.FromResult(
            statuses.Peek() == HttpStatusCode.OK
                ? ConsumeValidResponse(statuses)
                : new HttpResponseMessage(statuses.Dequeue())));
        using var client = new HttpClient(handler);
        var reranker = new DeepSeekCloudReranker(
            client,
            new FakeCredentials("unit-test-key"));

        Assert.Null(await reranker.RerankAsync(Request("service failure one")));
        Assert.Null(await reranker.RerankAsync(Request("ordinary client error")));
        Assert.Null(await reranker.RerankAsync(Request("service failure after reset")));
        Assert.NotNull(await reranker.RerankAsync(Request("still available")));
        Assert.Equal(4, handler.CallCount);
    }

    [Fact]
    public async Task Credential_manager_value_precedes_the_environment_fallback()
    {
        var provider = new WindowsDeepSeekCredentialProvider(
            () => "credential-manager-key",
            _ => "environment-key");
        Assert.Equal("credential-manager-key", await provider.GetApiKeyAsync());

        provider = new WindowsDeepSeekCredentialProvider(
            () => null,
            name => name == WindowsDeepSeekCredentialProvider.EnvironmentVariableName
                ? "environment-key"
                : null);
        Assert.Equal("environment-key", await provider.GetApiKeyAsync());
    }

    [Fact]
    public async Task Credential_save_normalizes_and_overwrites_through_the_injected_writer()
    {
        var written = new List<string>();
        var provider = new WindowsDeepSeekCredentialProvider(
            () => null,
            value =>
            {
                written.Add(value);
                return true;
            },
            _ => null);

        Assert.True(await provider.SaveApiKeyAsync("  synthetic-first-key  "));
        Assert.True(await provider.SaveApiKeyAsync("synthetic-updated-key"));
        Assert.Equal(["synthetic-first-key", "synthetic-updated-key"], written);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("synthetic key with spaces")]
    [InlineData("synthetic\nkey")]
    public async Task Invalid_credential_is_rejected_before_the_writer(string value)
    {
        var writeCount = 0;
        var provider = new WindowsDeepSeekCredentialProvider(
            () => null,
            _ =>
            {
                writeCount++;
                return true;
            },
            _ => null);

        Assert.False(await provider.SaveApiKeyAsync(value));
        Assert.Equal(0, writeCount);
    }

    private static CloudRerankRequest Request(string query = "needle") => new(
        query,
        [
            Candidate("c0", "one.md", @"D:\docs\one.md", "short snippet", matchSource: "name"),
            Candidate("c1", "two.md", @"D:\docs\two.md", null, matchSource: "content"),
            Candidate("c2", "three.dll", @"D:\build\three.dll", null,
                RankingProtectedTier.Soft, matchSource: "both")
        ]);

    private static CloudRerankCandidate Candidate(
        string id,
        string name,
        string path,
        string? snippet,
        RankingProtectedTier tier = RankingProtectedTier.Eligible,
        string matchSource = "name") =>
        new(id, name, path, snippet, matchSource, tier);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage ValidResponse() => JsonResponse(
        """
        {"choices":[{"message":{"content":"{\"top5_ids\":[\"c0\"]}"}}]}
        """);

    private static HttpResponseMessage ConsumeValidResponse(Queue<HttpStatusCode> statuses)
    {
        _ = statuses.Dequeue();
        return ValidResponse();
    }

    private sealed class FakeCredentials(string? key) : IDeepSeekCredentialProvider
    {
        internal int CallCount { get; private set; }

        public ValueTask<string?> GetApiKeyAsync(
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(key);
        }
    }

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        private int _callCount;
        internal int CallCount => _callCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            request.Options.Set(CancellationTokenKey, cancellationToken);
            return response(request);
        }

        private static readonly HttpRequestOptionsKey<CancellationToken> CancellationTokenKey =
            new("test-cancellation-token");
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void Advance(TimeSpan value) => _now += value;
    }
}

file static class HttpRequestMessageTestExtensions
{
    private static readonly HttpRequestOptionsKey<CancellationToken> CancellationTokenKey =
        new("test-cancellation-token");

    internal static CancellationToken GetCancellationToken(this HttpRequestMessage request) =>
        request.Options.TryGetValue(CancellationTokenKey, out var token)
            ? token
            : CancellationToken.None;
}
