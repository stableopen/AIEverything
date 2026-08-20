namespace AIEverything.Server.Tests;

public sealed class PluginContractTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void Optional_plugin_manifest_and_mcp_config_remain_present()
    {
        Assert.True(File.Exists(Path.Combine(Root, ".codex-plugin", "plugin.json")));
        Assert.True(File.Exists(Path.Combine(Root, ".mcp.json")));
        Assert.True(File.Exists(Path.Combine(Root, "scripts", "build-agent-connector.ps1")));
    }

    [Fact]
    public void Readme_discloses_v100_local_text_index_and_optional_cloud_boundary()
    {
        var readme = File.ReadAllText(Path.Combine(Root, "README.md"));
        Assert.Contains("1.0.1", readme);
        Assert.Contains("固定 NTFS/ReFS", readme);
        Assert.Contains(".markdown", readme);
        Assert.Contains("未单独加密", readme);
        Assert.Contains("5 MiB", readme);
        Assert.Contains("DeepSeek 默认启用", readme);
        Assert.Contains("不是 voidtools 官方产品", readme);

        using var manifest = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(Path.Combine(Root, ".codex-plugin", "plugin.json")));
        Assert.Equal("1.0.1", manifest.RootElement.GetProperty("version").GetString());
        Assert.Equal(
            new[] { "Read" },
            manifest.RootElement.GetProperty("interface").GetProperty("capabilities")
                .EnumerateArray().Select(value => value.GetString()!).ToArray());
    }

    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "AIEverything.sln"))) current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
