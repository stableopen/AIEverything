using AIEverything.Content.Text;
using AIEverything.Content.Extraction;

namespace AIEverything.Server.Tests.Content;

public sealed class SourceLocationResolverTests
{
    [Fact]
    public void Docx_returns_structured_heading_and_location_label()
    {
        var blocks = new[]
        {
            new ExtractedTextBlock(1, "Sales Plan", "Sales Plan · heading 1", "Sales Plan"),
            new ExtractedTextBlock(2, "regional target is rising", "Sales Plan · 第 4 段", "Sales Plan")
        };

        var hit = Assert.Single(SourceLocationResolver.Resolve(
            "Sales Plan\nregional target is rising", "docx", ["regional", "target"], blocks));

        Assert.Equal("Sales Plan · 第 4 段", hit.LocationLabel);
        Assert.Equal("Sales Plan", hit.HeadingPath);
        Assert.Contains("regional target", hit.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public void Txt_returns_accurate_line_range_and_multiple_hits()
    {
        var hits = SourceLocationResolver.Resolve("one\nneedle first\nthree\nneedle second", "txt", ["needle"]);
        Assert.Equal(2, hits.Count);
        Assert.Equal(2, hits[0].StartLine);
        Assert.Equal(4, hits[1].StartLine);
    }

    [Fact]
    public void Markdown_returns_nearest_atx_heading_path()
    {
        var hit = Assert.Single(SourceLocationResolver.Resolve(
            "# Project\nintro\n## Decision\nuse needle here", "md", ["needle"]));
        Assert.Equal("Project > Decision", hit.HeadingPath);
        Assert.Equal(4, hit.StartLine);
    }

    [Fact]
    public void Markdown_long_extension_returns_nearest_atx_heading_path()
    {
        var hit = Assert.Single(SourceLocationResolver.Resolve(
            "# Project\nintro\n## Decision\nuse needle here", "markdown", ["needle"]));
        Assert.Equal("Project > Decision", hit.HeadingPath);
        Assert.Equal(4, hit.StartLine);
    }

    [Fact]
    public void Json_returns_json_path_and_line()
    {
        var hit = Assert.Single(SourceLocationResolver.Resolve(
            "{\n  \"users\": [{\"name\": \"needle\"}]\n}", "json", ["needle"]));
        Assert.Equal("$.users[0].name", hit.JsonPath);
        Assert.Equal(2, hit.StartLine);
    }

    [Fact]
    public void Json_array_of_objects_increments_second_object_index()
    {
        var hit = Assert.Single(SourceLocationResolver.Resolve(
            "{\"users\":[{\"name\":\"first\"},{\"name\":\"needle\"}]}",
            "json", ["needle"]));
        Assert.Equal("$.users[1].name", hit.JsonPath);
    }

    [Fact]
    public void Multi_word_query_returns_compact_context_covering_all_terms()
    {
        var hit = Assert.Single(SourceLocationResolver.Resolve(
            "before\nalpha starts\nbridge\nbeta ends\nafter", "txt", ["alpha", "beta"]));
        Assert.Equal(2, hit.StartLine);
        Assert.Equal(4, hit.EndLine);
        Assert.Contains("alpha starts", hit.Snippet);
        Assert.Contains("beta ends", hit.Snippet);
    }

    [Fact]
    public void Invalid_json_is_visibly_downgraded_to_text()
    {
        var hit = Assert.Single(SourceLocationResolver.Resolve("{\n\"name\": \"needle\"", "json", ["needle"]));
        Assert.Equal("Invalid JSON · text fallback", hit.LocationLabel);
        Assert.Null(hit.JsonPath);
    }
}
