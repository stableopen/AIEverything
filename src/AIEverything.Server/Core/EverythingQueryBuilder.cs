using System.Globalization;
using System.Text.RegularExpressions;

namespace AIEverything.Core;

public static partial class EverythingQueryBuilder
{
    public static CompiledEverythingQuery Build(StructuredSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.Limit), request.Limit, "Limit must be between 1 and 100.");
        }

        if (request.Offset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.Offset), request.Offset, "Offset must be non-negative.");
        }

        if (request.ModifiedAfter > request.ModifiedBefore)
        {
            throw new ArgumentException(
                "ModifiedAfter must not be later than ModifiedBefore.", nameof(request));
        }

        var terms = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            terms.Add(QuoteLiteral(request.Query.Trim()));
        }

        if (request.Path is not null)
        {
            terms.Add(BuildPathTerm(request.Path));
        }

        if (request.Extensions is { Count: > 0 })
        {
            terms.Add(BuildExtensionTerm(request.Extensions));
        }

        switch (request.Kind)
        {
            case SearchItemKind.File:
                terms.Add("file:");
                break;
            case SearchItemKind.Folder:
                terms.Add("folder:");
                break;
            case SearchItemKind.Any:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request.Kind));
        }

        if (request.ModifiedAfter is { } modifiedAfter)
        {
            terms.Add($"dm:>={FormatDate(modifiedAfter)}");
        }

        if (request.ModifiedBefore is { } modifiedBefore)
        {
            terms.Add($"dm:<={FormatDate(modifiedBefore)}");
        }

        return new CompiledEverythingQuery(
            string.Join(' ', terms),
            MapSort(request.SortBy, request.SortDirection),
            request.Limit,
            request.Offset);
    }

    private static string QuoteLiteral(string value) =>
        $"\"{value.Replace("\"", "quot:", StringComparison.Ordinal)}\"";

    private static string BuildPathTerm(string value)
    {
        if (!System.IO.Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("Path must be an absolute Windows path.", nameof(value));
        }

        var fullPath = System.IO.Path.GetFullPath(value);
        if (!System.IO.Path.EndsInDirectorySeparator(fullPath))
        {
            fullPath += System.IO.Path.DirectorySeparatorChar;
        }

        return QuoteLiteral(fullPath);
    }

    private static string BuildExtensionTerm(IReadOnlyList<string> extensions)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var extension in extensions)
        {
            var candidate = extension.Trim().TrimStart('.').ToLowerInvariant();
            if (!ExtensionPattern().IsMatch(candidate))
            {
                throw new ArgumentException(
                    $"Invalid extension: {extension}", nameof(extensions));
            }

            if (seen.Add(candidate))
            {
                normalized.Add(candidate);
            }
        }

        return $"<{string.Join('|', normalized.Select(extension => $"ext:{extension}"))}>";
    }

    private static string FormatDate(DateTimeOffset value) =>
        value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);

    private static EverythingSort MapSort(
        SearchSortBy sortBy,
        SearchSortDirection direction) => (sortBy, direction) switch
        {
            (SearchSortBy.Name, SearchSortDirection.Asc) => EverythingSort.NameAscending,
            (SearchSortBy.Name, SearchSortDirection.Desc) => EverythingSort.NameDescending,
            (SearchSortBy.Path, SearchSortDirection.Asc) => EverythingSort.PathAscending,
            (SearchSortBy.Path, SearchSortDirection.Desc) => EverythingSort.PathDescending,
            (SearchSortBy.Size, SearchSortDirection.Asc) => EverythingSort.SizeAscending,
            (SearchSortBy.Size, SearchSortDirection.Desc) => EverythingSort.SizeDescending,
            (SearchSortBy.Modified, SearchSortDirection.Asc) => EverythingSort.DateModifiedAscending,
            (SearchSortBy.Modified, SearchSortDirection.Desc) => EverythingSort.DateModifiedDescending,
            _ => throw new ArgumentOutOfRangeException(nameof(sortBy))
        };

    [GeneratedRegex("^[a-z0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionPattern();
}
