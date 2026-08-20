using AIEverything.Content.Contracts;
using AIEverything.ContentClient;
using AIEverything.Core;
using AIEverything.Desktop.Ranking;
using AIEverything.Desktop.Mail;
using AIEverything.Everything;
using System.Security.Cryptography;
using System.Text;

namespace AIEverything.Desktop;

public sealed class StandaloneSearchService
{
    private readonly IEverythingSearchService _everything;
    private readonly IContentSearchService _content;
    private readonly HybridSearchService _hybrid;
    private readonly IMailSearch _mail;

    public StandaloneSearchService(
        IEverythingSearchService everything,
        IContentSearchService content,
        HybridSearchService hybrid,
        IMailSearch? mail = null)
    {
        _everything = everything ?? throw new ArgumentNullException(nameof(everything));
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _hybrid = hybrid ?? throw new ArgumentNullException(nameof(hybrid));
        _mail = mail ?? EmptyMailSearch.Instance;
    }

    public AIEverythingStatus GetEverythingStatus() => _everything.GetStatus();

    public Task<ContentIndexStatus> GetIndexStatusAsync(
        CancellationToken cancellationToken = default) =>
        _content.GetStatusAsync(cancellationToken);

    public Task<IReadOnlyList<ContentIndexFailure>> ListIndexFailuresAsync(
        CancellationToken cancellationToken = default) =>
        _content.ListFailuresAsync(cancellationToken);

    public Task<ContentIndexStatus> ConfigureIndexAsync(bool disclosureAccepted, bool enabled,
        CancellationToken cancellationToken = default) =>
        _content.ConfigureAsync(disclosureAccepted, enabled, cancellationToken);

    public Task<ContentIndexStatus> SetPausedAsync(
        bool paused,
        CancellationToken cancellationToken = default) =>
        _content.SetPausedAsync(paused, cancellationToken);

    public Task<ContentIndexStatus> SynchronizeAsync(CancellationToken cancellationToken = default) =>
        _content.SynchronizeAsync(cancellationToken);

    public Task<ContentIndexStatus> RetryFailuresAsync(CancellationToken cancellationToken = default) =>
        _content is ContentDaemonClient client
            ? client.RetryFailuresAsync(cancellationToken)
            : throw new NotSupportedException("Retrying index failures requires the local content daemon.");

    public async Task<DesktopSearchResponse> SearchAsync(
        DesktopSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        return request.Mode switch
        {
            DesktopSearchMode.FileName => await SearchFileNamesAsync(request, cancellationToken),
            DesktopSearchMode.Content => await SearchContentAsync(request, cancellationToken),
            DesktopSearchMode.Hybrid => await SearchHybridAsync(request, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request), "Unknown search mode.")
        };
    }

    private async Task<DesktopSearchResponse> SearchFileNamesAsync(
        DesktopSearchRequest request,
        CancellationToken cancellationToken)
    {
        SearchResponse response;
        try
        {
            response = await Task.Run(
                () => NoiseAwareEverythingSearch.Search(_everything, new StructuredSearchRequest(
                    Query: request.Query,
                    Path: null,
                    Extensions: null,
                    Kind: SearchItemKind.Any,
                    ModifiedAfter: null,
                    ModifiedBefore: null,
                    Limit: request.Limit)),
                cancellationToken);
        }
        catch (AIEverythingException)
        {
            return await SearchIndexedFileNamesAsync(request, cancellationToken);
        }

        var items = response.Items.Select((item, index) => new DesktopSearchItem(
            item.Name,
            item.FullPath,
            item.Extension,
            item.Kind,
            item.Kind == SearchItemKind.Folder ? null : item.Size,
            item.ModifiedAt,
            null,
            "name",
            RankingTier: GetFileNameTier(request.Query, item),
            BaselineIndex: index)).ToArray();
        return new DesktopSearchResponse(
            response.Query,
            DesktopSearchMode.FileName,
            checked((int)Math.Min(int.MaxValue, response.TotalResults)),
            response.ReturnedResults,
            response.QueryDurationMs,
            items);
    }

    private async Task<DesktopSearchResponse> SearchIndexedFileNamesAsync(
        DesktopSearchRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _content.SearchAsync(new ContentSearchRequest(
            request.Query,
            null,
            null,
            null,
            null,
            Limit: request.Limit,
            Field: ContentSearchField.Title), cancellationToken);
        var items = response.Items.Select((item, index) => new DesktopSearchItem(
            item.Name,
            item.FullPath,
            item.Extension,
            SearchItemKind.File,
            item.Size,
            item.ModifiedAt,
            null,
            "name",
            RankingTier: SearchResultRanking.IsExactQuery(
                request.Query, item.Name, item.FullPath)
                ? RankingProtectedTier.Exact
                : RankingProtectedTier.Eligible,
            BaselineIndex: index)).ToArray();
        return new DesktopSearchResponse(
            response.Query,
            DesktopSearchMode.FileName,
            response.TotalResults,
            response.ReturnedResults,
            response.QueryDurationMs,
            items);
    }

    private async Task<DesktopSearchResponse> SearchContentAsync(
        DesktopSearchRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _content.SearchAsync(new ContentSearchRequest(
            request.Query,
            null,
            null,
            null,
            null,
            Limit: request.Limit,
            Field: ContentSearchField.Body), cancellationToken);
        var fileItems = CollapseFileMatches(response.Items.Select(item => new DesktopSearchItem(
            item.Name,
            item.FullPath,
            item.Extension,
            SearchItemKind.File,
            item.Size,
            item.ModifiedAt,
            item.Snippet,
            "content",
            StartLine: item.StartLine,
            EndLine: item.EndLine,
            HeadingPath: item.HeadingPath,
            JsonPath: item.JsonPath,
            LocationLabel: item.LocationLabel,
            Imported: item.Imported)));
        var mailItems = MapMailItems(await SearchMailAsync(
            request.Query, request.Limit, cancellationToken));
        var items = MergeRanked(fileItems, mailItems, request.Limit);
        return new DesktopSearchResponse(
            response.Query,
            DesktopSearchMode.Content,
            items.Length,
            items.Length,
            response.QueryDurationMs,
            items);
    }

    private async Task<DesktopSearchResponse> SearchHybridAsync(
        DesktopSearchRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _hybrid.SearchAsync(new HybridSearchRequest(
            request.Query,
            null,
            null,
            null,
            null,
            Limit: request.Limit), cancellationToken);
        var fileItems = CollapseFileMatches(response.Items.Select((item, index) => new DesktopSearchItem(
            item.Name,
            item.FullPath,
            item.Extension,
            item.Kind,
            item.Size,
            item.ModifiedAt,
            item.Snippet,
            item.MatchSource,
            StartLine: item.StartLine,
            EndLine: item.EndLine,
            HeadingPath: item.HeadingPath,
            JsonPath: item.JsonPath,
            LocationLabel: item.LocationLabel,
            Imported: item.Imported,
            RankingTier: GetHybridTier(request.Query, item),
            BaselineIndex: index,
            BaselineScore: item.Score)));
        var mailItems = MapMailItems(await SearchMailAsync(
            request.Query, request.Limit, cancellationToken));
        var items = MergeRanked(fileItems, mailItems, request.Limit);
        return new DesktopSearchResponse(
            response.Query,
            DesktopSearchMode.Hybrid,
            items.Length,
            items.Length,
            response.QueryDurationMs,
            items);
    }

    private static DesktopSearchItem[] CollapseFileMatches(
        IEnumerable<DesktopSearchItem> items)
    {
        return items
            .GroupBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select((group, index) =>
            {
                var matches = group.ToArray();
                var primary = matches[0];
                if (matches.Length == 1)
                {
                    return primary with { BaselineIndex = index };
                }

                var hasNameMatch = matches.Any(item =>
                    item.MatchSource is "name" or "both");
                var hasContentMatch = matches.Any(item =>
                    item.MatchSource is "content" or "both");
                var matchSource = hasNameMatch && hasContentMatch
                    ? "both"
                    : hasNameMatch ? "name" : "content";
                var primaryLocation = primary.LocationLabel
                    ?? primary.HeadingPath
                    ?? (primary.StartLine is { } line ? $"lines {line}-{primary.EndLine ?? line}" : null);
                var location = string.IsNullOrWhiteSpace(primaryLocation)
                    ? $"{matches.Length} 处命中"
                    : $"{matches.Length} 处命中 · {primaryLocation}";

                return primary with
                {
                    MatchSource = matchSource,
                    LocationLabel = location,
                    RankingTier = matches.Min(item => item.RankingTier),
                    BaselineIndex = index,
                    BaselineScore = matches
                        .Where(item => item.BaselineScore.HasValue)
                        .Select(item => item.BaselineScore)
                        .Max()
                };
            })
            .ToArray();
    }

    private async Task<IReadOnlyList<MailSearchHit>> SearchMailAsync(
        string query,
        int limit,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _mail.SearchAsync(query, limit, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private static DesktopSearchItem[] MapMailItems(IReadOnlyList<MailSearchHit> hits) =>
        hits.Select((mail, index) => new DesktopSearchItem(
            string.IsNullOrWhiteSpace(mail.Subject) ? "(无主题)" : mail.Subject,
            BuildMailIdentityPath(mail.Identity),
            "mail",
            SearchItemKind.File,
            null,
            mail.Timestamp,
            mail.Snippet,
            "mail",
            Detail: BuildMailDetail(mail),
            CopyText: BuildMailReference(mail),
            LocationLabel: $"邮件 · {mail.Folder}",
            RankingTier: RankingProtectedTier.Eligible,
            BaselineIndex: index,
            BaselineScore: -mail.Score,
            MailIdentity: mail.Identity)).ToArray();

    private static DesktopSearchItem[] MergeRanked(
        IReadOnlyList<DesktopSearchItem> files,
        IReadOnlyList<DesktopSearchItem> mail,
        int limit)
    {
        const double reciprocalRankOffset = 60;
        return files.Select((item, index) => (Item: item, Rank: index,
                Score: 1d / (reciprocalRankOffset + index + 1), Source: 0))
            .Concat(mail.Select((item, index) => (Item: item, Rank: index,
                Score: 1d / (reciprocalRankOffset + index + 1), Source: 1)))
            .OrderByDescending(value => value.Score)
            .ThenBy(value => value.Source)
            .ThenBy(value => value.Rank)
            .Take(limit)
            .Select((value, index) => value.Item with { BaselineIndex = index })
            .ToArray();
    }

    private static string BuildMailIdentityPath(MailIdentity identity)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            identity.StoreId + "\n" + identity.EntryId));
        return "outlook://mail/" + Convert.ToHexString(bytes)[..24].ToLowerInvariant();
    }

    private static string BuildMailDetail(MailSearchHit mail)
    {
        var people = string.IsNullOrWhiteSpace(mail.Recipients)
            ? mail.Sender
            : $"{mail.Sender} → {mail.Recipients}";
        return $"{people} · {mail.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm}";
    }

    private static string BuildMailReference(MailSearchHit mail)
    {
        var builder = new StringBuilder()
            .Append("邮件：").AppendLine(string.IsNullOrWhiteSpace(mail.Subject) ? "(无主题)" : mail.Subject)
            .Append("发件人：").AppendLine(mail.Sender)
            .Append("收件人：").AppendLine(mail.Recipients)
            .Append("时间：").AppendLine(mail.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
            .Append("文件夹：").AppendLine(mail.Folder);
        if (!string.IsNullOrWhiteSpace(mail.AttachmentNames))
        {
            builder.Append("附件：").AppendLine(mail.AttachmentNames);
        }
        builder.Append("正文片段：").Append(mail.Snippet);
        return builder.ToString();
    }

    private sealed class EmptyMailSearch : IMailSearch
    {
        public static EmptyMailSearch Instance { get; } = new();

        public Task<IReadOnlyList<MailSearchHit>> SearchAsync(
            string query, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MailSearchHit>>([]);
    }

    private static void Validate(DesktopSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            throw new ArgumentException("Search text is required.", nameof(request));
        }

        if (request.Limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Limit must be between 1 and 100.");
        }

    }

    private static RankingProtectedTier GetFileNameTier(string query, SearchItem item)
    {
        if (SearchResultRanking.IsExactQuery(query, item.Name, item.FullPath))
        {
            return RankingProtectedTier.Exact;
        }

        return SearchResultRanking.ClassifyNoise(query, item) == SearchNoiseLevel.SoftRanked
            ? RankingProtectedTier.Soft
            : RankingProtectedTier.Eligible;
    }

    private static RankingProtectedTier GetHybridTier(string query, HybridSearchItem item)
    {
        if (SearchResultRanking.IsExactQuery(query, item.Name, item.FullPath))
        {
            return RankingProtectedTier.Exact;
        }

        return item.MatchSource == "name" && item.NameNoise == SearchNoiseLevel.SoftRanked
            ? RankingProtectedTier.Soft
            : RankingProtectedTier.Eligible;
    }
}
