using System.Diagnostics;

namespace AIEverything.Desktop;

public interface IContentDaemonProcessHost
{
    int? FindByExecutablePath(string executablePath);

    int StopAll();

    int Start(string executablePath);
}

public sealed class SystemContentDaemonProcessHost : IContentDaemonProcessHost
{
    public int? FindByExecutablePath(string executablePath)
    {
        var expectedPath = Path.GetFullPath(executablePath);
        foreach (var process in Process.GetProcessesByName("AIEverything.Daemon"))
        {
            try
            {
                var runningPath = process.MainModule?.FileName;
                if (runningPath is not null &&
                    Path.GetFullPath(runningPath).Equals(
                        expectedPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return process.Id;
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Ignore processes whose executable path cannot be inspected.
            }
            finally
            {
                process.Dispose();
            }
        }

        return null;
    }

    public int StopAll()
    {
        var stopped = 0;
        foreach (var process in Process.GetProcessesByName("AIEverything.Daemon"))
        {
            try
            {
                var runningPath = process.MainModule?.FileName;
                if (runningPath is null ||
                    !Path.GetFileName(runningPath).Equals(
                        "AIEverything.Daemon.exe",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.Kill(entireProcessTree: false);
                if (process.WaitForExit(3000))
                {
                    stopped++;
                }
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or System.ComponentModel.Win32Exception or
                NotSupportedException)
            {
                // The manager will surface a useful error if no stale process can be stopped.
            }
            finally
            {
                process.Dispose();
            }
        }

        return stopped;
    }

    public int Start(string executablePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!
        };
        startInfo.ArgumentList.Add("run");
        using var process = Process.Start(startInfo) ??
                            throw new InvalidOperationException("无法启动 AIEverything 索引服务。");
        return process.Id;
    }
}
