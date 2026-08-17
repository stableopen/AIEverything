using System.Diagnostics;
using System.Runtime.InteropServices;
using AIEverything.Core;

namespace AIEverything.Everything;

public sealed class EverythingSearchService : IEverythingSearchService
{
    private readonly IEverythingNativeApi _nativeApi;
    private readonly Func<bool> _isSupportedPlatform;

    public EverythingSearchService(
        IEverythingNativeApi nativeApi,
        Func<bool>? isSupportedPlatform = null)
    {
        _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
        _isSupportedPlatform = isSupportedPlatform ?? IsWindowsX64;
    }

    public SearchResponse Search(StructuredSearchRequest request)
    {
        EnsureSupportedPlatform();
        ArgumentNullException.ThrowIfNull(request);

        CompiledEverythingQuery compiledQuery;
        try
        {
            compiledQuery = EverythingQueryBuilder.Build(request);
        }
        catch (ArgumentException exception)
        {
            if (request.Path is not null && !Path.IsPathFullyQualified(request.Path))
            {
                throw Error(
                    AIEverythingErrors.InvalidPath,
                    exception.Message,
                    AIEverythingErrors.ProvideAbsolutePath,
                    innerException: exception);
            }

            throw Error(
                AIEverythingErrors.InvalidQuery,
                exception.Message,
                AIEverythingErrors.CorrectQuery,
                innerException: exception);
        }

        return Execute(compiledQuery);
    }

    public SearchResponse SearchRaw(
        string query,
        int limit = 20,
        int offset = 0,
        SearchSortBy sortBy = SearchSortBy.Name,
        SearchSortDirection direction = SearchSortDirection.Asc)
    {
        EnsureSupportedPlatform();

        try
        {
            ArgumentNullException.ThrowIfNull(query);
            var validated = EverythingQueryBuilder.Build(new StructuredSearchRequest(
                SortBy: sortBy,
                SortDirection: direction,
                Limit: limit,
                Offset: offset));
            return Execute(validated with { Query = query });
        }
        catch (ArgumentException exception)
        {
            throw Error(
                AIEverythingErrors.InvalidQuery,
                exception.Message,
                AIEverythingErrors.CorrectQuery,
                innerException: exception);
        }
    }

    public AIEverythingStatus GetStatus()
    {
        if (!_isSupportedPlatform())
        {
            return FailedStatus(
                sdkLoaded: false,
                databaseLoaded: false,
                version: "0.0.0.0",
                nativeErrorCode: 0,
                AIEverythingErrors.UnsupportedPlatform,
                "AIEverything requires Windows x64.",
                AIEverythingErrors.RunOnWindows);
        }

        EverythingRuntimeStatus nativeStatus;
        try
        {
            nativeStatus = _nativeApi.GetStatus();
        }
        catch (Exception exception) when (IsNativeLoadFailure(exception))
        {
            return FailedStatus(
                sdkLoaded: false,
                databaseLoaded: false,
                version: "0.0.0.0",
                nativeErrorCode: 0,
                AIEverythingErrors.SdkLoadFailed,
                exception.Message,
                AIEverythingErrors.ReinstallSdk);
        }

        var version = FormatVersion(nativeStatus);
        if (!nativeStatus.SdkLoaded)
        {
            return FailedStatus(
                sdkLoaded: false,
                databaseLoaded: false,
                version,
                nativeStatus.LastError,
                AIEverythingErrors.SdkLoadFailed,
                nativeStatus.LoadError ?? "The Everything SDK could not be loaded.",
                AIEverythingErrors.ReinstallSdk);
        }

        if (!nativeStatus.DatabaseLoaded)
        {
            var isNotRunning = nativeStatus.LastError == 2 || nativeStatus.MajorVersion == 0;
            return FailedStatus(
                sdkLoaded: true,
                databaseLoaded: false,
                version,
                nativeStatus.LastError,
                isNotRunning ? AIEverythingErrors.NotRunning : AIEverythingErrors.DatabaseNotLoaded,
                isNotRunning
                    ? "Everything IPC is unavailable."
                    : "The Everything database is not loaded.",
                isNotRunning ? AIEverythingErrors.StartEverything : AIEverythingErrors.WaitForDatabase);
        }

        return new AIEverythingStatus(
            Ready: true,
            SdkLoaded: true,
            DatabaseLoaded: true,
            EverythingVersion: version,
            NativeErrorCode: nativeStatus.LastError,
            ErrorCode: null,
            Message: "Everything SDK and database are ready.",
            CorrectiveAction: null);
    }

    private SearchResponse Execute(CompiledEverythingQuery query)
    {
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var result = _nativeApi.Query(query);
            var elapsed = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            return new SearchResponse(
                query.Query,
                result.TotalResults,
                result.Items.Count,
                query.Offset,
                query.Limit,
                elapsed,
                result.Items);
        }
        catch (EverythingNativeException exception) when (exception.ErrorCode == 2)
        {
            throw Error(
                AIEverythingErrors.NotRunning,
                exception.Message,
                AIEverythingErrors.StartEverything,
                exception.ErrorCode,
                exception);
        }
        catch (EverythingNativeException exception)
        {
            throw Error(
                AIEverythingErrors.QueryFailed,
                exception.Message,
                AIEverythingErrors.RunDoctor,
                exception.ErrorCode,
                exception);
        }
        catch (Exception exception) when (IsNativeLoadFailure(exception))
        {
            throw Error(
                AIEverythingErrors.SdkLoadFailed,
                exception.Message,
                AIEverythingErrors.ReinstallSdk,
                innerException: exception);
        }
    }

    private void EnsureSupportedPlatform()
    {
        if (!_isSupportedPlatform())
        {
            throw Error(
                AIEverythingErrors.UnsupportedPlatform,
                "AIEverything requires Windows x64.",
                AIEverythingErrors.RunOnWindows);
        }
    }

    private static AIEverythingStatus FailedStatus(
        bool sdkLoaded,
        bool databaseLoaded,
        string version,
        uint nativeErrorCode,
        string errorCode,
        string message,
        string correctiveAction) => new(
            Ready: false,
            SdkLoaded: sdkLoaded,
            DatabaseLoaded: databaseLoaded,
            EverythingVersion: version,
            NativeErrorCode: nativeErrorCode,
            ErrorCode: errorCode,
            Message: message,
            CorrectiveAction: correctiveAction);

    private static AIEverythingException Error(
        string code,
        string message,
        string correctiveAction,
        uint nativeErrorCode = 0,
        Exception? innerException = null) =>
        new(code, message, correctiveAction, nativeErrorCode, innerException);

    private static string FormatVersion(EverythingRuntimeStatus status) =>
        $"{status.MajorVersion}.{status.MinorVersion}.{status.Revision}.{status.BuildNumber}";

    private static bool IsWindowsX64() =>
        OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private static bool IsNativeLoadFailure(Exception exception) =>
        exception is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException;
}
