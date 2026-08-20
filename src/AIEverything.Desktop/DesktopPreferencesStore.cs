using System.Text.Json;
using AIEverything.Desktop.Ranking;

namespace AIEverything.Desktop;

public sealed record DesktopPreferences(
    double Width,
    double Height,
    bool Maximized,
    RankingOptions Ranking,
    bool BehaviorDisclosureAcknowledged = false)
{
    public DesktopPreferences(double width, double height, bool maximized)
        : this(width, height, maximized, RankingOptions.Default, false)
    {
    }

    public static DesktopPreferences Default { get; } = new(
        900, 560, false, RankingOptions.Default);
}

public sealed class DesktopPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public DesktopPreferencesStore(string path) => _path = Path.GetFullPath(
        string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("Preferences path is required.", nameof(path)) : path);

    public DesktopPreferences Load()
    {
        try
        {
            if (!File.Exists(_path)) return DesktopPreferences.Default;
            using var json = JsonDocument.Parse(File.ReadAllText(_path));
            var root = json.RootElement;
            return Normalize(new DesktopPreferences(
                root.TryGetProperty("Width", out var width) ? width.GetDouble() : DesktopPreferences.Default.Width,
                root.TryGetProperty("Height", out var height) ? height.GetDouble() : DesktopPreferences.Default.Height,
                root.TryGetProperty("Maximized", out var maximized) && maximized.GetBoolean(),
                ReadRanking(root),
                ReadBoolean(root, "BehaviorDisclosureAcknowledged", false)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { return DesktopPreferences.Default; }
    }

    public void Save(DesktopPreferences preferences)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(Normalize(preferences), JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static DesktopPreferences Normalize(DesktopPreferences value)
    {
        var ranking = value.Ranking ?? RankingOptions.Default;
        ranking = ranking with { DeepSeekDisclosureAccepted = true };
        return value with
        {
            Width = double.IsFinite(value.Width) && value.Width >= 820 ? Math.Min(value.Width, 10000) : 900,
            Height = double.IsFinite(value.Height) && value.Height >= 520 ? Math.Min(value.Height, 10000) : 560,
            Ranking = ranking
        };
    }

    private static RankingOptions ReadRanking(JsonElement root)
    {
        if (!root.TryGetProperty("Ranking", out var ranking) ||
            ranking.ValueKind != JsonValueKind.Object)
        {
            return RankingOptions.Default;
        }

        var defaults = RankingOptions.Default;
        return new RankingOptions(
            ReadBoolean(ranking, "BehaviorEnabled", defaults.BehaviorEnabled),
            ReadBoolean(ranking, "LocalModelEnabled", defaults.LocalModelEnabled),
            ReadBoolean(ranking, "DeepSeekEnabled", defaults.DeepSeekEnabled),
            true);
    }

    private static bool ReadBoolean(JsonElement value, string name, bool fallback) =>
        value.TryGetProperty(name, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : fallback;
}
