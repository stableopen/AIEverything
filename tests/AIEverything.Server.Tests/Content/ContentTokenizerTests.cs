using AIEverything.Content.Errors;
using AIEverything.Content.Text;

namespace AIEverything.Server.Tests.Content;

public sealed class ContentTokenizerTests
{
    [Fact]
    public void Tokenize_for_index_lowercases_unicode_words_and_preserves_numbers()
    {
        var result = ContentTokenizer.TokenizeForIndex("Hello CAFÉ 2026 report_01");

        Assert.Equal("hello café 2026 report_01", result);
    }

    [Fact]
    public void Tokenize_for_index_emits_overlapping_cjk_bigrams()
    {
        var result = ContentTokenizer.TokenizeForIndex("人工智能");

        Assert.Equal("人工 工智 智能", result);
    }

    [Fact]
    public void Tokenize_for_index_keeps_common_technical_identifiers()
    {
        var result = ContentTokenizer.TokenizeForIndex("Qwen3-VL GGML_SYCL v2.1");

        Assert.Equal("qwen3-vl ggml_sycl v2.1", result);
    }

    [Fact]
    public void Get_query_terms_deduplicates_in_encounter_order()
    {
        var result = ContentTokenizer.GetQueryTerms("Report 报告 report 报告");

        Assert.Equal(["report", "报告"], result);
    }

    [Fact]
    public void Build_match_query_quotes_every_term_and_escapes_quotes()
    {
        var result = ContentTokenizer.BuildMatchQuery("alpha 人工智能");

        Assert.Equal("\"alpha\" AND \"人工\" AND \"工智\" AND \"智能\"", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("人")]
    public void Query_rejects_empty_or_single_cjk_character(string query)
    {
        var exception = Assert.Throws<ContentIndexException>(() =>
            ContentTokenizer.BuildMatchQuery(query));

        Assert.Equal(ContentErrorCodes.QueryTooBroad, exception.Code);
    }
}
