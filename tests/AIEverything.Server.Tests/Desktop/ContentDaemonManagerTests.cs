using AIEverything.Content.Contracts;
using AIEverything.Desktop;
using AIEverything.Server.Tests.TestDoubles;

namespace AIEverything.Server.Tests.Desktop;

public sealed class ContentDaemonManagerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "aieverything-daemon-manager-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Compatible_ready_daemon_is_reused()
    {
        var daemonPath = CreateDaemonPlaceholder();
        var processHost = new FakeDaemonProcessHost();
        var manager = new ContentDaemonManager(daemonPath, processHost);

        var result = await manager.EnsureRunningAsync(new FakeContentSearchService());

        Assert.True(result.AlreadyRunning);
        Assert.False(result.Started);
        Assert.Equal(0, processHost.StopAllCalls);
        Assert.Null(processHost.StartedPath);
    }

    [Fact]
    public async Task Ready_legacy_daemon_is_stopped_before_current_daemon_starts()
    {
        var daemonPath = CreateDaemonPlaceholder();
        var service = new FakeContentSearchService
        {
            Status = new ContentIndexStatus(
                true, false, 1, 1, 0, 0, null, "legacy.db")
        };
        var processHost = new FakeDaemonProcessHost
        {
            StoppedProcessCount = 1,
            StartedProcessId = 731
        };
        var manager = new ContentDaemonManager(daemonPath, processHost);

        var result = await manager.EnsureRunningAsync(service);

        Assert.Equal(1, processHost.StopAllCalls);
        Assert.Equal(daemonPath, processHost.StartedPath);
        Assert.True(result.Started);
        Assert.False(result.AlreadyRunning);
        Assert.Equal(731, result.ProcessId);
        Assert.Contains("新版", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legacy_daemon_that_cannot_be_stopped_is_reported_instead_of_silently_reused()
    {
        var daemonPath = CreateDaemonPlaceholder();
        var service = new FakeContentSearchService
        {
            Status = new ContentIndexStatus(
                true, false, 1, 1, 0, 0, null, "legacy.db")
        };
        var manager = new ContentDaemonManager(daemonPath, new FakeDaemonProcessHost());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.EnsureRunningAsync(service));

        Assert.Contains("旧版", exception.Message, StringComparison.Ordinal);
    }

    private string CreateDaemonPlaceholder()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "AIEverything.Daemon.exe");
        File.WriteAllBytes(path, [0]);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeDaemonProcessHost : IContentDaemonProcessHost
    {
        public int StopAllCalls { get; private set; }
        public int StoppedProcessCount { get; init; }
        public int StartedProcessId { get; init; } = 1;
        public string? StartedPath { get; private set; }

        public int? FindByExecutablePath(string executablePath) => null;

        public int StopAll()
        {
            StopAllCalls++;
            return StoppedProcessCount;
        }

        public int Start(string executablePath)
        {
            StartedPath = executablePath;
            return StartedProcessId;
        }
    }
}
