using AIEverything.Core;
using AIEverything.Everything;

namespace AIEverything.Server.Tests.TestDoubles;

internal sealed class FakeEverythingNativeApi : IEverythingNativeApi
{
    public NativeSearchResult NextResult { get; set; } = new(0, []);

    public EverythingRuntimeStatus Status { get; set; } = new(
        SdkLoaded: true,
        DatabaseLoaded: true,
        MajorVersion: 1,
        MinorVersion: 4,
        Revision: 1,
        BuildNumber: 1028,
        LastError: 0,
        LoadError: null);

    public Exception? QueryException { get; set; }

    public CompiledEverythingQuery? LastQuery { get; private set; }

    public int QueryCalls { get; private set; }

    public int StatusCalls { get; private set; }

    public NativeSearchResult Query(CompiledEverythingQuery query)
    {
        QueryCalls++;
        LastQuery = query;
        if (QueryException is not null)
        {
            throw QueryException;
        }

        return NextResult;
    }

    public EverythingRuntimeStatus GetStatus()
    {
        StatusCalls++;
        return Status;
    }

    public void Dispose()
    {
    }
}
