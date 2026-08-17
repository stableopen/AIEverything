using System.Runtime.InteropServices;
using System.Text;

namespace AIEverything.Everything;

internal static class EverythingNativeMethods
{
    internal const uint RequestFileName = 0x00000001;
    internal const uint RequestPath = 0x00000002;
    internal const uint RequestExtension = 0x00000008;
    internal const uint RequestSize = 0x00000010;
    internal const uint RequestDateModified = 0x00000040;
    internal const uint RequestAttributes = 0x00000100;

    private const string LibraryName = "Everything64.dll";

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern void Everything_SetSearchW(string searchString);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern void Everything_SetRequestFlags(uint requestFlags);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern void Everything_SetSort(uint sortType);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern void Everything_SetMax(uint maximumResults);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern void Everything_SetOffset(uint offset);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Everything_QueryW([MarshalAs(UnmanagedType.Bool)] bool wait);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint Everything_GetLastError();

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint Everything_GetNumResults();

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint Everything_GetTotResults();

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Everything_IsFolderResult(uint index);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern IntPtr Everything_GetResultFileNameW(uint index);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern IntPtr Everything_GetResultPathW(uint index);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern IntPtr Everything_GetResultExtensionW(uint index);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint Everything_GetResultFullPathNameW(
        uint index,
        StringBuilder buffer,
        uint bufferSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Everything_GetResultSize(uint index, out long size);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Everything_GetResultDateModified(uint index, out long fileTime);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint Everything_GetResultAttributes(uint index);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern void Everything_Reset();

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern void Everything_CleanUp();

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Everything_IsDBLoaded();

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint Everything_GetMajorVersion();

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint Everything_GetMinorVersion();

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint Everything_GetRevision();

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall, ExactSpelling = true)]
    internal static extern uint Everything_GetBuildNumber();
}
