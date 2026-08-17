using AIEverything.Core;

namespace AIEverything.Server.Tests.Core;

public sealed class EverythingQueryBuilderTests
{
    [Fact]
    public void Build_combines_literal_path_extensions_kind_and_dates()
    {
        var request = new StructuredSearchRequest(
            Query: "quarterly report",
            Path: @"C:\Users\TestUser\Documents\SearchCorpus",
            Extensions: [".PDF", "docx"],
            Kind: SearchItemKind.File,
            ModifiedAfter: DateTimeOffset.Parse("2026-07-01T00:00:00+08:00"),
            ModifiedBefore: DateTimeOffset.Parse("2026-07-15T23:59:59+08:00"),
            SortBy: SearchSortBy.Modified,
            SortDirection: SearchSortDirection.Desc,
            Limit: 5,
            Offset: 2);

        var result = EverythingQueryBuilder.Build(request);

        Assert.Contains("\"quarterly report\"", result.Query);
        Assert.Contains("\"C:\\Users\\TestUser\\Documents\\SearchCorpus\\\"", result.Query);
        Assert.Contains("<ext:pdf|ext:docx>", result.Query);
        Assert.Contains("file:", result.Query);
        Assert.Contains("dm:>=20260701T000000", result.Query);
        Assert.Contains("dm:<=20260715T235959", result.Query);
        Assert.Equal(EverythingSort.DateModifiedDescending, result.Sort);
        Assert.Equal(5, result.Limit);
        Assert.Equal(2, result.Offset);
    }

    [Fact]
    public void Build_allows_filter_only_searches()
    {
        var result = EverythingQueryBuilder.Build(new StructuredSearchRequest(
            Extensions: ["pdf"],
            SortBy: SearchSortBy.Modified,
            SortDirection: SearchSortDirection.Desc));

        Assert.Equal("<ext:pdf>", result.Query);
    }

    [Fact]
    public void Build_escapes_embedded_quotes_for_everything_1_4()
    {
        var result = EverythingQueryBuilder.Build(new StructuredSearchRequest(
            Query: "report \"final\""));

        Assert.Equal("\"report quot:finalquot:\"", result.Query);
    }

    [Fact]
    public void Build_normalizes_and_deduplicates_extensions()
    {
        var result = EverythingQueryBuilder.Build(new StructuredSearchRequest(
            Extensions: [".PDF", "pdf", " DocX "]));

        Assert.Equal("<ext:pdf|ext:docx>", result.Query);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Build_rejects_out_of_range_limits(int limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => EverythingQueryBuilder.Build(
            new StructuredSearchRequest(Limit: limit)));

    [Fact]
    public void Build_rejects_negative_offsets() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => EverythingQueryBuilder.Build(
            new StructuredSearchRequest(Offset: -1)));

    [Fact]
    public void Build_rejects_relative_paths() =>
        Assert.Throws<ArgumentException>(() => EverythingQueryBuilder.Build(
            new StructuredSearchRequest(Path: @"relative\folder")));

    [Theory]
    [InlineData("p df")]
    [InlineData("p|df")]
    [InlineData(".")]
    public void Build_rejects_invalid_extensions(string extension) =>
        Assert.Throws<ArgumentException>(() => EverythingQueryBuilder.Build(
            new StructuredSearchRequest(Extensions: [extension])));

    [Fact]
    public void Build_rejects_inverted_date_ranges() =>
        Assert.Throws<ArgumentException>(() => EverythingQueryBuilder.Build(
            new StructuredSearchRequest(
                ModifiedAfter: DateTimeOffset.Parse("2026-07-15T00:00:00+08:00"),
                ModifiedBefore: DateTimeOffset.Parse("2026-07-01T00:00:00+08:00"))));

    [Theory]
    [InlineData(SearchSortBy.Name, SearchSortDirection.Asc, EverythingSort.NameAscending)]
    [InlineData(SearchSortBy.Name, SearchSortDirection.Desc, EverythingSort.NameDescending)]
    [InlineData(SearchSortBy.Path, SearchSortDirection.Asc, EverythingSort.PathAscending)]
    [InlineData(SearchSortBy.Path, SearchSortDirection.Desc, EverythingSort.PathDescending)]
    [InlineData(SearchSortBy.Size, SearchSortDirection.Asc, EverythingSort.SizeAscending)]
    [InlineData(SearchSortBy.Size, SearchSortDirection.Desc, EverythingSort.SizeDescending)]
    [InlineData(SearchSortBy.Modified, SearchSortDirection.Asc, EverythingSort.DateModifiedAscending)]
    [InlineData(SearchSortBy.Modified, SearchSortDirection.Desc, EverythingSort.DateModifiedDescending)]
    public void Build_maps_sort_order(
        SearchSortBy sortBy,
        SearchSortDirection direction,
        EverythingSort expected)
    {
        var result = EverythingQueryBuilder.Build(new StructuredSearchRequest(
            SortBy: sortBy,
            SortDirection: direction));

        Assert.Equal(expected, result.Sort);
    }
}
