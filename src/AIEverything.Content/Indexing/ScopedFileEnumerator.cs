using System.Security.Cryptography;
using System.Text;
using AIEverything.Content.Contracts;

namespace AIEverything.Content.Indexing;

public static class ScopedFileEnumerator
{
    public static string CreateFingerprint(string fullPath, long size, DateTimeOffset modifiedAt)
    {
        var normalizedPath = Path.GetFullPath(fullPath).ToUpperInvariant();
        var value = $"{normalizedPath}|{size}|{modifiedAt.UtcTicks}|{ContentServiceCompatibility.TextExtractionRevision}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
