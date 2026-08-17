namespace AIEverything.Daemon;

public sealed record ContentDaemonOptions(
    string DatabasePath,
    string WorkerPath,
    string PipeName,
    TimeSpan WatcherDebounce,
    TimeSpan ReconcileInterval,
    TimeSpan IdleDelay)
{
    public static ContentDaemonOptions CreateDefault()
    {
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AIEverything");
        return new ContentDaemonOptions(
            Path.Combine(dataRoot, "content.db"),
            Path.Combine(AppContext.BaseDirectory, "AIEverything.ExtractorWorker.exe"),
            Content.Ipc.ContentPipeNaming.ForCurrentUser(),
            TimeSpan.FromMilliseconds(750),
            TimeSpan.FromSeconds(60),
            TimeSpan.FromMilliseconds(100));
    }
}
