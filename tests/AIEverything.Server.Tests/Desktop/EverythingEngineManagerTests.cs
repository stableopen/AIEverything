using AIEverything.Core;
using AIEverything.Desktop;
using AIEverything.Everything;

namespace AIEverything.Server.Tests.Desktop;

public sealed class EverythingEngineManagerTests
{
    [Fact]
    public async Task Ready_client_is_reused_without_touching_payload()
    {
        var host = new FakeEngineHost();
        var manager = CreateManager(host);

        var result = await manager.EnsureReadyAsync(() => ReadyStatus());

        Assert.True(result.Ready);
        Assert.True(result.AlreadyRunning);
        Assert.False(host.Prepared);
        Assert.Equal(0, host.ClientStarts);
    }

    [Fact]
    public async Task Existing_privileged_indexer_starts_packaged_client_without_elevation()
    {
        var host = new FakeEngineHost { PrivilegedIndexer = true };
        var manager = CreateManager(host);
        var probes = 0;

        var result = await manager.EnsureReadyAsync(() =>
            ++probes >= 3 ? ReadyStatus() : OfflineStatus());

        Assert.True(result.Ready);
        Assert.True(host.Prepared);
        Assert.Equal(1, host.ClientStarts);
        Assert.Equal(0, host.IndexerStarts);
        Assert.False(result.RequestedElevation);
    }

    [Fact]
    public async Task Missing_privileged_indexer_is_started_before_client()
    {
        var host = new FakeEngineHost();
        var manager = CreateManager(host);
        var probes = 0;

        var result = await manager.EnsureReadyAsync(() =>
            ++probes >= 3 ? ReadyStatus() : OfflineStatus());

        Assert.True(result.Ready);
        Assert.Equal(["prepare", "indexer", "client"], host.Actions);
        Assert.True(result.RequestedElevation);
    }

    private static EverythingEngineManager CreateManager(FakeEngineHost host) =>
        new(
            host,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(1));

    private static AIEverythingStatus ReadyStatus() => new(
        true, true, true, "1.4.1.1032", 0, null, "ready", null);

    private static AIEverythingStatus OfflineStatus() => new(
        false, true, false, "0.0.0.0", 2, "EVERYTHING_NOT_RUNNING", "offline", "start");

    private sealed class FakeEngineHost : IEverythingEngineProcessHost
    {
        internal bool PrivilegedIndexer { get; set; }
        internal bool Prepared { get; private set; }
        internal int ClientStarts { get; private set; }
        internal int IndexerStarts { get; private set; }
        internal List<string> Actions { get; } = [];

        public void Prepare()
        {
            Prepared = true;
            Actions.Add("prepare");
        }

        public bool HasPrivilegedIndexer() => PrivilegedIndexer;

        public int StartPrivilegedIndexer()
        {
            IndexerStarts++;
            Actions.Add("indexer");
            return 10;
        }

        public int StartClient()
        {
            ClientStarts++;
            Actions.Add("client");
            return 20;
        }
    }
}
