namespace AIEverything.Everything;

public sealed class AIEverythingException : Exception
{
    public AIEverythingException(
        string code,
        string message,
        string correctiveAction,
        uint nativeErrorCode = 0,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        CorrectiveAction = correctiveAction;
        NativeErrorCode = nativeErrorCode;
    }

    public string Code { get; }

    public string CorrectiveAction { get; }

    public uint NativeErrorCode { get; }
}

internal static class AIEverythingErrors
{
    internal const string NotRunning = "EVERYTHING_NOT_RUNNING";
    internal const string DatabaseNotLoaded = "EVERYTHING_DATABASE_NOT_LOADED";
    internal const string SdkLoadFailed = "EVERYTHING_SDK_LOAD_FAILED";
    internal const string InvalidQuery = "INVALID_QUERY";
    internal const string InvalidPath = "INVALID_PATH";
    internal const string QueryFailed = "QUERY_FAILED";
    internal const string UnsupportedPlatform = "UNSUPPORTED_PLATFORM";

    internal const string StartEverything = "Start Everything and retry.";
    internal const string WaitForDatabase = "Wait for Everything indexing to finish and retry.";
    internal const string ReinstallSdk = "Reinstall AIEverything to restore Everything64.dll.";
    internal const string CorrectQuery = "Correct the search text or structured filter.";
    internal const string ProvideAbsolutePath = "Provide an absolute Windows path.";
    internal const string RunDoctor = "Run AIEverything.Server.exe doctor and inspect the native error code.";
    internal const string RunOnWindows = "Run AIEverything on Windows x64.";
}
