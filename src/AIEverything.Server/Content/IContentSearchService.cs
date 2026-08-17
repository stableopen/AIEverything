using AIEverything.Content.Contracts;

namespace AIEverything.ContentClient;

public interface IContentSearchService
{
    Task<ContentSearchResponse> SearchAsync(
        ContentSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<ContentIndexStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ContentIndexFailure>> ListFailuresAsync(
        CancellationToken cancellationToken = default);

    Task<ContentIndexStatus> ConfigureAsync(
        bool disclosureAccepted,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<ContentIndexStatus> SetPausedAsync(
        bool paused,
        CancellationToken cancellationToken = default);

    Task<ContentIndexStatus> SynchronizeAsync(CancellationToken cancellationToken = default);
}
