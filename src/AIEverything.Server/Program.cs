using AIEverything.Cli;
using AIEverything.Content.Ipc;
using AIEverything.ContentClient;
using AIEverything.Everything;
using AIEverything.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

if (args is ["serve", ..])
{
    await RunMcpServerAsync(args[1..]);
    return 0;
}

using (var nativeApi = new EverythingNativeApi())
{
    var searchService = new EverythingSearchService(nativeApi);
    var contentService = new ContentDaemonClient(
        ContentPipeNaming.ForCurrentUser(), TimeSpan.FromSeconds(2));
    var runner = new CliCommandRunner(
        searchService,
        contentService,
        new HybridSearchService(searchService, contentService));
    return await runner.RunAsync(args, Console.Out, Console.Error, CancellationToken.None);
}

static async Task RunMcpServerAsync(string[] hostArgs)
{
    var builder = Host.CreateApplicationBuilder(hostArgs);
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options =>
        options.LogToStandardErrorThreshold = LogLevel.Trace);

    builder.Services.AddSingleton<IEverythingNativeApi, EverythingNativeApi>();
    builder.Services.AddSingleton<IEverythingSearchService, EverythingSearchService>();
    builder.Services.AddSingleton<IContentSearchService>(_ => new ContentDaemonClient(
        ContentPipeNaming.ForCurrentUser(), TimeSpan.FromSeconds(2)));
    builder.Services.AddSingleton<HybridSearchService>();
    builder.Services
        .AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation
            {
                Name = "aieverything",
                Version = "1.0.4",
                Title = "AIEverything"
            };
            options.ServerInstructions =
                "Use search_local_files for filenames and paths, search_local_content for document bodies " +
                "inside explicitly authorized roots, and search_local_hybrid when either match source is useful. " +
                "Never treat a missing body result as proof for directories not configured in the content index. " +
                "Keep limits small and do not replace scoped indexing with recursive whole-drive scans.";
        })
        .WithStdioServerTransport()
        .WithTools<AIEverythingTools>();

    await builder.Build().RunAsync();
}
