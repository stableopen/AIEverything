using AIEverything.Content.Errors;
using AIEverything.Content.Text;

namespace AIEverything.Desktop;

public static class SearchPreviewTerms
{
    public static IReadOnlyList<string> Get(string query)
    {
        try
        {
            return ContentTokenizer.GetQueryTerms(query)
                .OrderByDescending(term => term.Length)
                .ToArray();
        }
        catch (ContentIndexException)
        {
            return query.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }
    }
}
