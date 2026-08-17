namespace AIEverything.Core;

public enum SearchNoiseLevel
{
    Normal,
    SoftRanked,
    HardFiltered
}

public static class SearchResultRanking
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes Pinned = (FileAttributes)0x00080000;
    private const FileAttributes Unpinned = (FileAttributes)0x00100000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes SoftAttributes =
        FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint |
        FileAttributes.Offline | RecallOnOpen | Pinned | Unpinned | RecallOnDataAccess;

    private static readonly HashSet<string> HardPathComponents =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "$Recycle.Bin", "System Volume Information"
        };

    private static readonly HashSet<string> HardLeafNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "desktop.ini", "Thumbs.db", "ehthumbs.db", ".DS_Store",
            "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log.tmp"
        };

    private static readonly HashSet<string> SoftPathComponents =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Windows", "Program Files", "Program Files (x86)", "ProgramData", "AppData",
            "Temp", "tmp", "cache", ".cache", "Caches", "CrashDumps",
            ".git", ".hg", ".svn", ".jj", "CVS",
            "node_modules", "bower_components", "vendor", "packages", ".packages",
            ".nuget", ".npm", ".pnpm-store", ".yarn", ".gradle", ".m2",
            ".venv", "venv", "env", "__pycache__", ".tox", ".conda",
            "bin", "obj", "target", "dist", "build", "out", "artifacts",
            "Debug", "Release", ".next", ".nuxt", ".output", ".svelte-kit",
            ".turbo", ".vite", ".idea", ".vs", ".vscode", "coverage",
            "coverage-reports", "Installer", "Installers", "Setup", "Package Cache"
        };

    private static readonly HashSet<string> SoftExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".part", ".partial", ".crdownload", ".download", ".cache",
            ".bak", ".backup", ".old", ".orig", ".swp", ".swo",
            ".log", ".dmp", ".etl",
            ".obj", ".o", ".a", ".lib", ".dll", ".exe", ".pdb", ".ilk",
            ".idb", ".pch", ".class", ".jar", ".pyc", ".pyo", ".nupkg",
            ".snupkg", ".whl", ".msi", ".msix", ".appx", ".cab", ".vsix",
            ".map"
        };

    private static readonly string[] SoftCompoundSuffixes =
    {
        ".min.js", ".min.css", ".deps.json", ".runtimeconfig.json"
    };

    private static readonly string[] TemporaryRoots = BuildTemporaryRoots();

    private static readonly string[] SystemPathPrefixes = BuildSystemPathPrefixes();

    public static SearchNoiseLevel ClassifyNoise(string query, SearchItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var fullPath = NormalizePath(item.FullPath);
        var leafName = string.IsNullOrWhiteSpace(item.Name)
            ? Path.GetFileName(fullPath)
            : item.Name;
        var components = SplitPathComponents(fullPath);
        var extension = NormalizeExtension(item.Extension, leafName);

        if ((item.Attributes & FileAttributes.Temporary) != 0 ||
            TemporaryRoots.Any(root => IsSameOrDescendant(fullPath, root)) ||
            components.Any(HardPathComponents.Contains) ||
            extension is ".tmp" or ".temp" ||
            leafName.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
            leafName.Equals(".temp", StringComparison.OrdinalIgnoreCase) ||
            HardLeafNames.Contains(leafName) ||
            leafName.StartsWith("~$", StringComparison.OrdinalIgnoreCase) ||
            leafName.StartsWith(".~lock.", StringComparison.OrdinalIgnoreCase))
        {
            return SearchNoiseLevel.HardFiltered;
        }

        if (IsExactQuery(query, leafName, fullPath))
        {
            return SearchNoiseLevel.Normal;
        }

        if ((item.Attributes & SoftAttributes) != 0 ||
            SystemPathPrefixes.Any(prefix => IsSameOrDescendant(fullPath, prefix)) ||
            components.Any(SoftPathComponents.Contains) ||
            leafName.StartsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            SoftExtensions.Contains(extension) ||
            SoftCompoundSuffixes.Any(suffix =>
                leafName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return SearchNoiseLevel.SoftRanked;
        }

        return SearchNoiseLevel.Normal;
    }

    public static bool IsSystemPath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        var normalized = NormalizePath(fullPath);
        return SystemPathPrefixes.Any(prefix => IsSameOrDescendant(normalized, prefix));
    }

    public static int NameMatchRank(string query, string name)
    {
        var normalizedQuery = query.Trim();
        if (name.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (name.StartsWith(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ? 2 : 3;
    }

    public static bool IsExactQuery(string query, string leafName, string fullPath)
    {
        var normalizedQuery = (query ?? string.Empty).Trim();
        if (normalizedQuery.Length >= 2 && normalizedQuery[0] == '"' &&
            normalizedQuery[^1] == '"')
        {
            normalizedQuery = normalizedQuery[1..^1].Trim();
        }

        if (leafName.Equals(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Path.IsPathFullyQualified(normalizedQuery) &&
               NormalizePath(normalizedQuery).Equals(fullPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] BuildTemporaryRoots()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return new[]
            {
                Path.GetTempPath(),
                Environment.GetEnvironmentVariable("TEMP"),
                Environment.GetEnvironmentVariable("TMP"),
                string.IsNullOrWhiteSpace(localAppData) ? null : Path.Combine(localAppData, "Temp"),
                string.IsNullOrWhiteSpace(windows) ? null : Path.Combine(windows, "Temp")
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => NormalizePath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] BuildSystemPathPrefixes() =>
        new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> SplitPathComponents(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        return fullPath[root.Length..]
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
    }

    private static string NormalizeExtension(string extension, string leafName)
    {
        var value = string.IsNullOrWhiteSpace(extension) ? Path.GetExtension(leafName) : extension;
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.StartsWith('.') ? value.ToLowerInvariant() : $".{value.ToLowerInvariant()}";
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar);
        }
    }

    private static bool IsSameOrDescendant(string candidate, string prefix)
    {
        if (candidate.Equals(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var root = Path.GetPathRoot(prefix);
        if (root is not null && Path.TrimEndingDirectorySeparator(root)
                .Equals(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return candidate.StartsWith(prefix + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }
}
