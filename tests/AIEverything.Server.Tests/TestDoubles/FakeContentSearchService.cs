using AIEverything.Content.Contracts;
using AIEverything.Content.Errors;
using AIEverything.ContentClient;

namespace AIEverything.Server.Tests.TestDoubles;

internal sealed class FakeContentSearchService : IContentSearchService
{
    public ContentSearchResponse NextResponse { get; set; } = new(
        string.Empty, 0, 0, 0, 20, 0, []);

    public ContentIndexStatus Status { get; set; } = new(
        true,
        false,
        1,
        0,
        0,
        0,
        null,
        "test.db",
        ServiceProtocolVersion: ContentServiceCompatibility.ProtocolVersion,
        TextExtractionRevision: ContentServiceCompatibility.TextExtractionRevision);

    public ContentIndexException? Exception { get; set; }

    public ContentSearchRequest? LastSearch { get; private set; }

    public Task<ContentSearchResponse> SearchAsync(
        ContentSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        LastSearch = request;
        if (Exception is not null)
        {
            throw Exception;
        }

        return Task.FromResult(NextResponse with
        {
            Query = request.Query,
            Limit = request.Limit,
            Offset = request.Offset
        });
    }

    public Task<ContentIndexStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Status);

    public Task<IReadOnlyList<ContentIndexFailure>> ListFailuresAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ContentIndexFailure>>([]);

    public Task<ContentIndexStatus> ConfigureAsync(
        bool disclosureAccepted,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Status with { DisclosureAccepted = disclosureAccepted, Enabled = enabled });

    public Task<ContentIndexStatus> SetPausedAsync(
        bool paused,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Status with { Paused = paused });

    public Task<ContentIndexStatus> SynchronizeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Status);
}
