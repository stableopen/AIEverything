using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using AIEverything.Core;
using AIEverything.Everything;

namespace AIEverything.Desktop;

public interface IEverythingEngineProcessHost
{
    void Prepare();

    bool HasPrivilegedIndexer();

    int StartPrivilegedIndexer();

    int StartClient();
}

public sealed record EverythingEngineLaunchResult(
    bool Ready,
    bool AlreadyRunning,
    bool StartedClient,
    bool RequestedElevation,
    int? ClientProcessId,
    string Message);

public sealed class EverythingEngineManager
{
    private const int ElevationCancelled = 1223;
    private readonly IEverythingEngineProcessHost _processHost;
    private readonly TimeSpan _existingClientGrace;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _pollInterval;

    public EverythingEngineManager(IEverythingEngineProcessHost processHost)
        : this(
            processHost,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(25),
            TimeSpan.FromMilliseconds(250))
    {
    }

    public EverythingEngineManager(
        IEverythingEngineProcessHost processHost,
        TimeSpan existingClientGrace,
        TimeSpan startupTimeout,
        TimeSpan pollInterval)
    {
        _processHost = processHost ?? throw new ArgumentNullException(nameof(processHost));
        _existingClientGrace = ValidateDuration(existingClientGrace, nameof(existingClientGrace), allowZero: true);
        _startupTimeout = ValidateDuration(startupTimeout, nameof(startupTimeout), allowZero: false);
        _pollInterval = ValidateDuration(pollInterval, nameof(pollInterval), allowZero: false);
    }

    public async Task<EverythingEngineLaunchResult> EnsureReadyAsync(
        Func<AIEverythingStatus> getStatus,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(getStatus);

        if (await WaitForReadyAsync(getStatus, _existingClientGrace, cancellationToken))
        {
            return new EverythingEngineLaunchResult(
                true, true, false, false, null, "全盘文件名搜索已就绪。");
        }

        _processHost.Prepare();
        var requestedElevation = false;
        if (!_processHost.HasPrivilegedIndexer())
        {
            try
            {
                _processHost.StartPrivilegedIndexer();
                requestedElevation = true;
            }
            catch (Win32Exception exception) when (exception.NativeErrorCode == ElevationCancelled)
            {
                return new EverythingEngineLaunchResult(
                    false,
                    false,
                    false,
                    false,
                    null,
                    "未获得 Windows 权限；全盘文件名搜索未启用，已建立的本机正文索引仍可搜索。");
            }
        }

        var processId = _processHost.StartClient();
        var ready = await WaitForReadyAsync(getStatus, _startupTimeout, cancellationToken);
        return new EverythingEngineLaunchResult(
            ready,
            false,
            true,
            requestedElevation,
            processId,
            ready
                ? requestedElevation
                    ? "全盘文件名搜索已启用；正文索引会在后台同步符合规则的本机文本。"
                    : "全盘文件名搜索已自动启动。"
                : "全盘文件名引擎仍在建立首次索引；稍后会自动可用。");
    }

    private async Task<bool> WaitForReadyAsync(
        Func<AIEverythingStatus> getStatus,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (getStatus().Ready)
            {
                return true;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(_pollInterval, cancellationToken);
        } while (true);
    }

    private static TimeSpan ValidateDuration(TimeSpan value, string name, bool allowZero)
    {
        if (value < TimeSpan.Zero || (!allowZero && value == TimeSpan.Zero))
        {
            throw new ArgumentOutOfRangeException(name);
        }

        return value;
    }
}

public sealed class SystemEverythingEngineProcessHost : IEverythingEngineProcessHost
{
    private static readonly string[] PayloadFiles =
        ["Everything.exe", "Everything.ini", "LICENSE.txt"];
    private readonly string _payloadDirectory;
    private readonly string _runtimeDirectory;

    public SystemEverythingEngineProcessHost(string payloadDirectory, string runtimeDirectory)
    {
        _payloadDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(payloadDirectory)
                ? throw new ArgumentException("Engine payload directory is required.", nameof(payloadDirectory))
                : payloadDirectory);
        _runtimeDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(runtimeDirectory)
                ? throw new ArgumentException("Engine runtime directory is required.", nameof(runtimeDirectory))
                : runtimeDirectory);
    }

    public void Prepare()
    {
        Directory.CreateDirectory(_runtimeDirectory);
        foreach (var fileName in PayloadFiles)
        {
            var source = Path.Combine(_payloadDirectory, fileName);
            if (!File.Exists(source))
            {
                throw new FileNotFoundException("找不到随包附带的全盘文件名引擎。", source);
            }

            var destination = Path.Combine(_runtimeDirectory, fileName);
            if (FilesMatch(source, destination))
            {
                continue;
            }

            var temporary = destination + ".new";
            File.Copy(source, temporary, overwrite: true);
            File.Move(temporary, destination, overwrite: true);
        }
    }

    public bool HasPrivilegedIndexer()
    {
        foreach (var process in Process.GetProcessesByName("Everything"))
        {
            try
            {
                if (process.SessionId == 0)
                {
                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited while it was being inspected.
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    public int StartPrivilegedIndexer() => Start(["-svc"], elevated: true);

    public int StartClient() => Start(["-startup"], elevated: false);

    private int Start(IReadOnlyList<string> arguments, bool elevated)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(_runtimeDirectory, "Everything.exe"),
            WorkingDirectory = _runtimeDirectory,
            UseShellExecute = elevated,
            CreateNoWindow = !elevated,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (elevated)
        {
            startInfo.Verb = "runas";
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException("无法启动全盘文件名引擎。");
        return process.Id;
    }

    private static bool FilesMatch(string source, string destination)
    {
        if (!File.Exists(destination))
        {
            return false;
        }

        var sourceInfo = new FileInfo(source);
        var destinationInfo = new FileInfo(destination);
        if (sourceInfo.Length != destinationInfo.Length)
        {
            return false;
        }

        using var sourceStream = File.OpenRead(source);
        using var destinationStream = File.OpenRead(destination);
        return SHA256.HashData(sourceStream).SequenceEqual(SHA256.HashData(destinationStream));
    }
}
