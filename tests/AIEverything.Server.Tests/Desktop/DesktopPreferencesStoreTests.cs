using System.Text;
using AIEverything.Desktop;
using AIEverything.Desktop.Ranking;

namespace AIEverything.Server.Tests.Desktop;

public sealed class DesktopPreferencesStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"aieverything-settings-{Guid.NewGuid():N}");

    [Fact]
    public void Missing_and_v0201_json_use_safe_v021_ranking_defaults()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new DesktopPreferencesStore(path);
        Assert.Equal(RankingOptions.Default, store.Load().Ranking);
        Assert.False(store.Load().BehaviorDisclosureAcknowledged);

        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, """
            {"Width":960,"Height":620,"Maximized":true}
            """, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var migrated = store.Load();
        Assert.Equal(960, migrated.Width);
        Assert.Equal(620, migrated.Height);
        Assert.True(migrated.Maximized);
        Assert.Equal(RankingOptions.Default, migrated.Ranking);
        Assert.False(migrated.BehaviorDisclosureAcknowledged);
    }

    [Fact]
    public void DeepSeek_default_enablement_does_not_require_a_homepage_disclosure_gate()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new DesktopPreferencesStore(path);
        store.Save(new DesktopPreferences(900, 560, false, new RankingOptions(
            BehaviorEnabled: false,
            LocalModelEnabled: false,
            DeepSeekEnabled: true,
            DeepSeekDisclosureAccepted: false)));

        var loaded = store.Load();

        Assert.False(loaded.Ranking.BehaviorEnabled);
        Assert.False(loaded.Ranking.LocalModelEnabled);
        Assert.True(loaded.Ranking.DeepSeekDisclosureAccepted);
        Assert.True(loaded.Ranking.DeepSeekEnabled);
    }

    [Fact]
    public void Accepted_ranking_settings_round_trip_without_query_or_credentials()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new DesktopPreferencesStore(path);
        var expected = new DesktopPreferences(1024, 700, true, new RankingOptions(
            BehaviorEnabled: true,
            LocalModelEnabled: true,
            DeepSeekEnabled: true,
            DeepSeekDisclosureAccepted: true),
            BehaviorDisclosureAcknowledged: true);

        store.Save(expected);

        Assert.Equal(expected, store.Load());
        var json = File.ReadAllText(path);
        Assert.DoesNotContain("query", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api_key", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Corrupt_json_falls_back_to_all_defaults()
    {
        var path = Path.Combine(_directory, "settings.json");
        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{ broken", Encoding.UTF8);

        Assert.Equal(DesktopPreferences.Default, new DesktopPreferencesStore(path).Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
