using AIEverything.Core;

namespace AIEverything.Everything;

public sealed record NativeSearchResult(
    uint TotalResults,
    IReadOnlyList<SearchItem> Items);

public sealed record EverythingRuntimeStatus(
    bool SdkLoaded,
    bool DatabaseLoaded,
    uint MajorVersion,
    uint MinorVersion,
    uint Revision,
    uint BuildNumber,
    uint LastError,
    string? LoadError);

public interface IEverythingNativeApi : IDisposable
{
    NativeSearchResult Query(CompiledEverythingQuery query);

    EverythingRuntimeStatus GetStatus();
}

public sealed class EverythingNativeException : Exception
{
    public EverythingNativeException(uint errorCode)
        : base($"Everything SDK query failed: {Describe(errorCode)} (error {errorCode}).")
    {
        ErrorCode = errorCode;
    }

    public uint ErrorCode { get; }

    private static string Describe(uint errorCode) => errorCode switch
    {
        0 => "no SDK error was reported",
        1 => "memory allocation failed",
        2 => "Everything IPC is unavailable; ensure Everything is running",
        3 => "window class registration failed",
        4 => "the SDK could not create its IPC window",
        5 => "the SDK could not create its query thread",
        6 => "an invalid result index was requested",
        7 => "the SDK call is invalid in the current state",
        8 => "the requested result field was not enabled",
        9 => "an SDK parameter is invalid",
        _ => "unknown SDK error"
    };
}
