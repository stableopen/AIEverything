using ModelContextProtocol.Client;

namespace AIEverything.Server.Tests.Mcp;

public sealed class McpStdioIntegrationTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Published_server_contract_lists_tools_and_returns_live_status()
    {
        var serverDll = Path.Combine(AppContext.BaseDirectory, "AIEverything.Server.dll");
        Assert.True(File.Exists(serverDll), $"Missing server assembly: {serverDll}");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await McpClient.CreateAsync(
            new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = "AIEverything integration test",
                Command = "dotnet",
                Arguments = [serverDll, "serve"]
            }),
            cancellationToken: timeout.Token);

        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Equal(
            [
                "aieverything_index_status",
                "aieverything_manage_roots",
                "aieverything_status",
                "search_everything_query",
                "search_local_content",
                "search_local_files",
                "search_local_hybrid"
            ],
            tools.Select(tool => tool.Name).Order(StringComparer.Ordinal));

        var status = await client.CallToolAsync(
            "aieverything_status",
            cancellationToken: timeout.Token);

        Assert.NotEqual(true, status.IsError);
        Assert.NotNull(status.StructuredContent);
        Assert.True(status.StructuredContent.Value.GetProperty("ready").GetBoolean());
        Assert.True(status.StructuredContent.Value.GetProperty("databaseLoaded").GetBoolean());

        var search = await client.CallToolAsync(
            "search_local_files",
            new Dictionary<string, object?>
            {
                ["query"] = "Everything.exe",
                ["kind"] = "file",
                ["limit"] = 20
            },
            cancellationToken: timeout.Token);

        Assert.NotEqual(true, search.IsError);
        Assert.NotNull(search.StructuredContent);
        Assert.Contains(
            search.StructuredContent.Value.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("name").GetString()!
                .Equals("Everything.exe", StringComparison.OrdinalIgnoreCase));
    }
}
