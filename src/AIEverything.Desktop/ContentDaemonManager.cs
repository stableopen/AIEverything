using AIEverything.Content.Contracts;
using AIEverything.ContentClient;

namespace AIEverything.Desktop;

public sealed record DaemonLaunchResult(
    bool AlreadyRunning,
    bool Started,
    int? ProcessId,
    string Message);

public sealed class ContentDaemonManager
{
    private readonly string _daemonPath;
    private readonly IContentDaemonProcessHost _processHost;

    public ContentDaemonManager(string daemonPath)
        : this(daemonPath, new SystemContentDaemonProcessHost())
    {
    }

    public ContentDaemonManager(
        string daemonPath,
        IContentDaemonProcessHost processHost)
    {
        _daemonPath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(daemonPath)
                ? throw new ArgumentException("Daemon path is required.", nameof(daemonPath))
                : daemonPath);
        _processHost = processHost ?? throw new ArgumentNullException(nameof(processHost));
    }

    public async Task<DaemonLaunchResult> EnsureRunningAsync(
        IContentSearchService contentService,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contentService);
        var probe = await ProbeAsync(
            contentService,
            TimeSpan.FromMilliseconds(350),
            cancellationToken);
        if (probe is { Ready: true, Compatible: true })
        {
            return new DaemonLaunchResult(true, false, null, "索引服务已运行。");
        }

        var replacedLegacyDaemon = false;
        if (probe is { Reachable: true, Compatible: false })
        {
            if (!File.Exists(_daemonPath))
            {
                throw new FileNotFoundException("找不到新版 AIEverything 索引服务。", _daemonPath);
            }

            if (_processHost.StopAll() == 0)
            {
                throw new InvalidOperationException(
                    "检测到旧版索引服务，但无法安全停止。请关闭所有 AIEverything 窗口后重试。");
            }

            replacedLegacyDaemon = true;
        }

        var existingProcessId = _processHost.FindByExecutablePath(_daemonPath);
        if (existingProcessId is not null)
        {
            return new DaemonLaunchResult(
                true,
                false,
                existingProcessId,
                "索引服务正在启动。");
        }

        if (!File.Exists(_daemonPath))
        {
            throw new FileNotFoundException("找不到 AIEverything 索引服务。", _daemonPath);
        }

        var id = _processHost.Start(_daemonPath);
        return new DaemonLaunchResult(
            false,
            true,
            id,
            replacedLegacyDaemon
                ? "已切换到新版索引服务，正在重新整理正文间隔。"
                : "索引服务已启动，正在载入目录。");
    }

    public async Task<bool> WaitUntilReadyAsync(
        IContentSearchService contentService,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contentService);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var probe = await ProbeAsync(
                contentService,
                TimeSpan.FromMilliseconds(500),
                cancellationToken);
            if (probe is { Ready: true, Compatible: true })
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        } while (DateTimeOffset.UtcNow < deadline);

        return false;
    }

    private static async Task<DaemonProbe> ProbeAsync(
        IContentSearchService contentService,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var status = await contentService.GetStatusAsync(timeoutSource.Token);
            return new DaemonProbe(
                true,
                status.Ready,
                ContentServiceCompatibility.IsCompatible(status));
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException or
            Content.Errors.ContentIndexException)
        {
            return new DaemonProbe(false, false, false);
        }
    }

    private sealed record DaemonProbe(bool Reachable, bool Ready, bool Compatible);
}
