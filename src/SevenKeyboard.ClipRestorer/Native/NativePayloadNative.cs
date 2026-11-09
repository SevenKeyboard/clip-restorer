using System.Runtime.InteropServices;

namespace SevenKeyboard;

[StructLayout(LayoutKind.Sequential)]
internal struct NativePayloadFileTime
{
    public uint Low;
    public uint High;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePayloadStat
{
    public nint Name;
    public uint Type;
    public ulong Size;
    public NativePayloadFileTime Modified;
    public NativePayloadFileTime Created;
    public NativePayloadFileTime Accessed;
    public uint Mode;
    public uint LocksSupported;
    public Guid ClassId;
    public uint StateBits;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativePayloadMetafilePicture
{
    public int MappingMode;
    public int XExtent;
    public int YExtent;
    public nint Metafile;
}

// Raw vtables and source-generated P/Invoke keep these paths usable in NativeAOT.
// Windows owns no payload here until the caller explicitly transfers a returned medium.
internal static unsafe partial class NativePayloadNative
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalFree(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nuint GlobalSize(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint GlobalFlags(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalLock(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int GlobalUnlock(nint handle);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint CopyImage(nint handle, uint type, int width, int height, uint flags);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial uint GetObjectType(nint handle);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial uint GetPaletteEntries(nint palette, uint start, uint count, void* entries);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial nint CreatePalette(void* palette);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial int DeleteObject(nint handle);

    [LibraryImport("gdi32.dll", EntryPoint = "CopyMetaFileW", SetLastError = true)]
    internal static partial nint CopyMetaFile(nint metafile, nint fileName);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    internal static partial int DeleteMetaFile(nint metafile);

    [LibraryImport("gdi32.dll", EntryPoint = "CopyEnhMetaFileW", SetLastError = true)]
    internal static partial nint CopyEnhMetaFile(nint metafile, nint fileName);

    [LibraryImport("ole32.dll")]
    internal static partial void ReleaseStgMedium(ref NativeMedium medium);

    [LibraryImport("ole32.dll")]
    internal static partial int CreateStreamOnHGlobal(nint global, int deleteOnRelease, out nint stream);

    [LibraryImport("ole32.dll")]
    internal static partial int GetHGlobalFromStream(nint stream, out nint global);

    [LibraryImport("ole32.dll")]
    internal static partial int GetHGlobalFromILockBytes(nint lockBytes, out nint global);
    [LibraryImport("ole32.dll")]
    internal static partial int CreateILockBytesOnHGlobal(nint global, int deleteOnRelease, out nint lockBytes);

    [LibraryImport("ole32.dll")]
    internal static partial int StgCreateDocfileOnILockBytes(nint lockBytes, uint mode, uint reserved, out nint storage);

    [LibraryImport("ole32.dll")]
    internal static partial void CoTaskMemFree(nint memory);

    internal static uint Release(nint instance) =>
        ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)instance)[2])(instance);

    internal static int StreamRead(nint stream, void* buffer, uint count, out uint read)
    {
        uint actual = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, void*, uint, uint*, int>)
            (*(nint**)stream)[3])(stream, buffer, count, &actual);
        read = actual;
        return hr;
    }

    internal static int StreamWrite(nint stream, void* buffer, uint count, out uint written)
    {
        uint actual = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, void*, uint, uint*, int>)
            (*(nint**)stream)[4])(stream, buffer, count, &actual);
        written = actual;
        return hr;
    }

    internal static int StreamSeek(nint stream, long offset, uint origin, out ulong position)
    {
        ulong actual = 0;
        int hr = ((delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)
            (*(nint**)stream)[5])(stream, offset, origin, &actual);
        position = actual;
        return hr;
    }

    internal static int StreamSetSize(nint stream, ulong size) =>
        ((delegate* unmanaged[Stdcall]<nint, ulong, int>)
            (*(nint**)stream)[6])(stream, size);

    internal static int StreamStat(nint stream, out NativePayloadStat stat)
    {
        NativePayloadStat value = default;
        int hr = ((delegate* unmanaged[Stdcall]<nint, NativePayloadStat*, uint, int>)
            (*(nint**)stream)[12])(stream, &value, 1);
        stat = value;
        return hr;
    }

    internal static int StorageStat(nint storage, out NativePayloadStat stat)
    {
        NativePayloadStat value = default;
        int hr = ((delegate* unmanaged[Stdcall]<nint, NativePayloadStat*, uint, int>)
            (*(nint**)storage)[17])(storage, &value, 1);
        stat = value;
        return hr;
    }

    internal static int StorageCopyTo(nint source, nint destination) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nint, int>)
            (*(nint**)source)[7])(source, 0, 0, 0, destination);

    internal static int StorageSetClass(nint storage, Guid classId) =>
        ((delegate* unmanaged[Stdcall]<nint, Guid*, int>)
            (*(nint**)storage)[15])(storage, &classId);

    internal static int StorageSetStateBits(nint storage, uint stateBits) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)
            (*(nint**)storage)[16])(storage, stateBits, uint.MaxValue);

    internal static int StorageCommit(nint storage) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, int>)
            (*(nint**)storage)[9])(storage, 0);
}
