using AIEverything.Content.Contracts;

namespace AIEverything.Content.MachineIndex;

public sealed record MachineDrive(string RootPath, bool IsReady, DriveType DriveType, string DriveFormat);

public sealed record CatalogEntry(
    string FullPath,
    string Name,
    string Extension,
    long Size,
    DateTimeOffset ModifiedAt,
    FileAttributes Attributes);

public sealed record CandidateDecision(
    bool Accepted,
    string Reason,
    long MaxBytes,
    int MaxCharacters,
    int Priority);

public sealed class MachineTextIndexPlan
{
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes UnsafeAttributes =
        FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint |
        FileAttributes.Offline | FileAttributes.Temporary | RecallOnDataAccess;

    private static readonly IReadOnlyDictionary<string, long> FormatLimits =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [".txt"] = 5 * 1024 * 1024,
            [".md"] = 5 * 1024 * 1024,
            [".markdown"] = 5 * 1024 * 1024,
            [".docx"] = 10 * 1024 * 1024
        };

    private static readonly HashSet<string> RepositoryMarkers =
        new(StringComparer.OrdinalIgnoreCase) { ".git", ".hg", ".svn", ".jj" };

    private static readonly HashSet<string> ExcludedDirectoryNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "$Recycle.Bin", "System Volume Information", "Recovery", "WindowsApps",
            "AppData", "Temp", "tmp", ".cache", "Cache", "Caches",
            "node_modules", "bower_components", "vendor", "packages", ".packages",
            ".nuget", ".npm", ".pnpm-store", ".yarn", ".gradle", ".m2",
            ".venv", "venv", "env", "__pycache__", ".tox", ".conda",
            "bin", "obj", "target", "dist", "build", "out", "artifacts",
            "Debug", "Release", ".next", ".nuxt", ".output", ".svelte-kit",
            ".turbo", ".vite", ".idea", ".vs", ".vscode", "coverage",
            "Installer", "Installers", "Setup", "Packages"
        };

    private readonly string _currentUserRoot;
    private readonly string _usersRoot;
    private readonly string[] _protectedPrefixes;
    private readonly string[] _priorityPrefixes;

    internal MachineTextIndexPlan(
        IReadOnlyList<string> driveRoots,
        string currentUserRoot,
        IEnumerable<string> protectedPrefixes)
    {
        DriveRoots = driveRoots;
        _currentUserRoot = NormalizeDirectory(currentUserRoot);
        _usersRoot = NormalizeDirectory(Path.GetDirectoryName(_currentUserRoot)!);
        _protectedPrefixes = protectedPrefixes
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _priorityPrefixes = new[]
        {
            Path.Combine(_currentUserRoot, "Documents"),
            Path.Combine(_currentUserRoot, "Desktop"),
            Path.Combine(_currentUserRoot, "Downloads")
        }.Select(NormalizeDirectory).ToArray();
    }

    public IReadOnlyList<string> DriveRoots { get; }

    public IReadOnlySet<string> SupportedExtensions { get; } =
        new HashSet<string>(FormatLimits.Keys, StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> MarkerNames { get; } =
        new HashSet<string>(RepositoryMarkers.Concat(ExcludedDirectoryNames), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> BuildDynamicExclusionPrefixes(IEnumerable<CatalogEntry> markers)
    {
        ArgumentNullException.ThrowIfNull(markers);
        var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var marker in markers)
        {
            if (RepositoryMarkers.Contains(marker.Name))
            {
                var parent = Path.GetDirectoryName(Path.GetFullPath(marker.FullPath));
                if (!string.IsNullOrWhiteSpace(parent)) prefixes.Add(NormalizeDirectory(parent));
            }
            else if (ExcludedDirectoryNames.Contains(marker.Name) &&
                     (marker.Attributes & FileAttributes.Directory) != 0)
            {
                prefixes.Add(NormalizeDirectory(marker.FullPath));
            }
        }
        return prefixes.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public CandidateDecision Evaluate(
        CatalogEntry entry,
        IReadOnlyList<string>? dynamicExclusionPrefixes = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string path;
        try { path = Path.GetFullPath(entry.FullPath); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        { return Reject("invalid path"); }

        var extension = NormalizeExtension(entry.Extension.Length == 0
            ? Path.GetExtension(path) : entry.Extension);
        if (!FormatLimits.TryGetValue(extension, out var maxBytes)) return Reject("unsupported format");
        if (entry.Size < 0 || entry.Size > maxBytes) return Reject("file size limit", maxBytes);
        if ((entry.Attributes & UnsafeAttributes) != 0) return Reject("unsafe file attributes", maxBytes);
        if (!DriveRoots.Any(root => IsSameOrDescendant(path, root))) return Reject("ineligible drive", maxBytes);
        if (_protectedPrefixes.Any(prefix => IsSameOrDescendant(path, prefix))) return Reject("protected path", maxBytes);
        if (IsOtherUserPath(path)) return Reject("other user profile", maxBytes);
        if (HasExcludedSegment(path)) return Reject("excluded directory", maxBytes);
        if (dynamicExclusionPrefixes?.Any(prefix => IsSameOrDescendant(path, prefix)) == true)
            return Reject("repository or excluded subtree", maxBytes);

        return new CandidateDecision(
            true, "accepted", maxBytes, 1_000_000, CalculatePriority(path, entry.ModifiedAt));
    }

    private bool IsOtherUserPath(string path) =>
        IsSameOrDescendant(path, _usersRoot) && !IsSameOrDescendant(path, _currentUserRoot);

    private static bool HasExcludedSegment(string path)
    {
        var root = Path.GetPathRoot(path) ?? string.Empty;
        var relative = path[root.Length..];
        return relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .SkipLast(1).Any(ExcludedDirectoryNames.Contains);
    }

    private int CalculatePriority(string path, DateTimeOffset modifiedAt)
    {
        if (_priorityPrefixes.Any(prefix => IsSameOrDescendant(path, prefix))) return 0;
        return modifiedAt >= DateTimeOffset.UtcNow.AddDays(-30) ? 1 : 2;
    }

    private static CandidateDecision Reject(string reason, long maxBytes = 0) =>
        new(false, reason, maxBytes, 1_000_000, int.MaxValue);

    private static string NormalizeExtension(string extension) =>
        extension.StartsWith('.') ? extension.ToLowerInvariant() : $".{extension.ToLowerInvariant()}";

    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsSameOrDescendant(string candidate, string prefix)
    {
        var normalizedPrefix = NormalizeDirectory(prefix);
        var normalizedCandidate = Path.GetFullPath(candidate);
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetPathRoot(normalizedPrefix) ?? string.Empty),
                normalizedPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return normalizedCandidate.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(Path.TrimEndingDirectorySeparator(normalizedCandidate), normalizedPrefix,
                   StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith(normalizedPrefix + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }
}

public static class MachineTextIndexPolicy
{
    public static MachineTextIndexPlan Build(
        IEnumerable<MachineDrive> drives,
        string currentUserRoot,
        string windowsPath,
        string programFilesPath,
        string programFilesX86Path,
        string programDataPath)
    {
        ArgumentNullException.ThrowIfNull(drives);
        var roots = drives
            .Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed &&
                            (drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ||
                             drive.DriveFormat.Equals("ReFS", StringComparison.OrdinalIgnoreCase)))
            .Select(drive => Path.GetPathRoot(Path.GetFullPath(drive.RootPath))!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var currentUser = Path.GetFullPath(currentUserRoot);
        return new MachineTextIndexPlan(roots, currentUser,
            new[] { windowsPath, programFilesPath, programFilesX86Path, programDataPath,
                Path.Combine(currentUser, "AppData") });
    }

    public static MachineTextIndexPlan BuildCurrentMachine()
    {
        var drives = DriveInfo.GetDrives().Select(drive =>
        {
            try
            {
                return new MachineDrive(drive.RootDirectory.FullName, drive.IsReady, drive.DriveType,
                    drive.IsReady ? drive.DriveFormat : string.Empty);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new MachineDrive(drive.Name, false, drive.DriveType, string.Empty);
            }
        });
        return Build(drives,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
    }
}
