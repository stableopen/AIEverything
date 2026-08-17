using System.Runtime.InteropServices;
using System.Text;
using AIEverything.Core;

namespace AIEverything.Everything;

public sealed class EverythingNativeApi : IEverythingNativeApi
{
    private const int InitialPathCapacity = 512;
    private static readonly object NativeLock = new();
    private bool _disposed;

    public NativeSearchResult Query(CompiledEverythingQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (NativeLock)
        {
            try
            {
                EverythingNativeMethods.Everything_SetSearchW(query.Query);
                EverythingNativeMethods.Everything_SetRequestFlags(
                    EverythingNativeMethods.RequestFileName |
                    EverythingNativeMethods.RequestPath |
                    EverythingNativeMethods.RequestExtension |
                    EverythingNativeMethods.RequestSize |
                    EverythingNativeMethods.RequestDateModified |
                    EverythingNativeMethods.RequestAttributes);
                EverythingNativeMethods.Everything_SetSort((uint)query.Sort);
                EverythingNativeMethods.Everything_SetMax(checked((uint)query.Limit));
                EverythingNativeMethods.Everything_SetOffset(checked((uint)query.Offset));

                if (!EverythingNativeMethods.Everything_QueryW(wait: true))
                {
                    throw new EverythingNativeException(EverythingNativeMethods.Everything_GetLastError());
                }

                var totalResults = EverythingNativeMethods.Everything_GetTotResults();
                var resultCount = EverythingNativeMethods.Everything_GetNumResults();
                var items = new List<SearchItem>(checked((int)resultCount));

                for (uint index = 0; index < resultCount; index++)
                {
                    items.Add(ReadResult(index));
                }

                return new NativeSearchResult(totalResults, items);
            }
            finally
            {
                EverythingNativeMethods.Everything_Reset();
            }
        }
    }

    public EverythingRuntimeStatus GetStatus()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (NativeLock)
        {
            try
            {
                return new EverythingRuntimeStatus(
                    SdkLoaded: true,
                    DatabaseLoaded: EverythingNativeMethods.Everything_IsDBLoaded(),
                    MajorVersion: EverythingNativeMethods.Everything_GetMajorVersion(),
                    MinorVersion: EverythingNativeMethods.Everything_GetMinorVersion(),
                    Revision: EverythingNativeMethods.Everything_GetRevision(),
                    BuildNumber: EverythingNativeMethods.Everything_GetBuildNumber(),
                    LastError: EverythingNativeMethods.Everything_GetLastError(),
                    LoadError: null);
            }
            catch (Exception exception) when (IsNativeLoadFailure(exception))
            {
                return new EverythingRuntimeStatus(
                    SdkLoaded: false,
                    DatabaseLoaded: false,
                    MajorVersion: 0,
                    MinorVersion: 0,
                    Revision: 0,
                    BuildNumber: 0,
                    LastError: 0,
                    LoadError: exception.Message);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (NativeLock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                EverythingNativeMethods.Everything_CleanUp();
            }
            catch (Exception exception) when (IsNativeLoadFailure(exception))
            {
                // There is no native state to clean up when the SDK could not load.
            }

            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private static SearchItem ReadResult(uint index)
    {
        var name = ReadString(EverythingNativeMethods.Everything_GetResultFileNameW(index));
        var parentPath = ReadString(EverythingNativeMethods.Everything_GetResultPathW(index));
        var extension = ReadString(EverythingNativeMethods.Everything_GetResultExtensionW(index));
        var kind = EverythingNativeMethods.Everything_IsFolderResult(index)
            ? SearchItemKind.Folder
            : SearchItemKind.File;

        long? size = EverythingNativeMethods.Everything_GetResultSize(index, out var rawSize)
            ? rawSize
            : null;
        DateTimeOffset? modifiedAt = EverythingNativeMethods.Everything_GetResultDateModified(index, out var fileTime)
            && fileTime > 0
            ? new DateTimeOffset(DateTime.FromFileTimeUtc(fileTime), TimeSpan.Zero)
            : null;

        return new SearchItem(
            name,
            ReadFullPath(index),
            parentPath,
            extension,
            kind,
            size,
            modifiedAt,
            (FileAttributes)EverythingNativeMethods.Everything_GetResultAttributes(index));
    }

    private static string ReadFullPath(uint index)
    {
        var buffer = new StringBuilder(InitialPathCapacity);
        var requiredLength = EverythingNativeMethods.Everything_GetResultFullPathNameW(
            index,
            buffer,
            checked((uint)buffer.Capacity));

        if (requiredLength >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)requiredLength + 1));
            EverythingNativeMethods.Everything_GetResultFullPathNameW(
                index,
                buffer,
                checked((uint)buffer.Capacity));
        }

        return buffer.ToString();
    }

    private static string ReadString(IntPtr pointer) =>
        pointer == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(pointer) ?? string.Empty;

    private static bool IsNativeLoadFailure(Exception exception) =>
        exception is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException;
}
