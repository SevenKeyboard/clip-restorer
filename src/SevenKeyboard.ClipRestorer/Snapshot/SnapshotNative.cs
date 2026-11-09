using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SevenKeyboard;

[StructLayout(LayoutKind.Sequential)]
internal struct SnapshotFormat
{
    internal ushort Format;
    internal nint TargetDevice;
    internal uint Aspect;
    internal int Index;
    internal uint Tymed;
}

internal static unsafe class SnapshotNative
{
    internal const int S_OK = 0;
    internal const int S_FALSE = 1;
    internal const int E_NOINTERFACE = unchecked((int)0x80004002);
    internal const int E_POINTER = unchecked((int)0x80004003);
    internal const int E_NOTIMPL = unchecked((int)0x80004001);
    internal const int E_FAIL = unchecked((int)0x80004005);
    internal const int E_INVALIDARG = unchecked((int)0x80070057);
    internal const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    internal const int DV_E_FORMATETC = unchecked((int)0x80040064);
    internal const int DV_E_DVTARGETDEVICE = unchecked((int)0x80040065);
    internal const int DV_E_LINDEX = unchecked((int)0x80040068);
    internal const int DV_E_TYMED = unchecked((int)0x80040069);
    internal const int DV_E_DVASPECT = unchecked((int)0x8004006B);
    internal const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    internal const int CLIPBRD_E_CANT_OPEN = unchecked((int)0x800401D0);
    internal const uint ContentAspect = 1;
    internal const uint SupportedMedia = 1 | 4 | 8 | 16 | 32 | 64;
    internal static readonly Guid IUnknownId = new("00000000-0000-0000-C000-000000000046");
    internal static readonly Guid IDataObjectId = new("0000010e-0000-0000-C000-000000000046");
    internal static readonly Guid IEnumFormatEtcId = new("00000103-0000-0000-C000-000000000046");

    internal static TimeSpan ValidateTimeout(TimeSpan? timeout)
    {
        TimeSpan result = timeout ?? TimeSpan.FromMilliseconds(500);
        if (result < TimeSpan.Zero || result.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        return result;
    }

    internal static int Error(Exception exception) =>
        exception.HResult < 0 ? exception.HResult : E_FAIL;

    internal static bool IsAllocationFailure(int hr) =>
        hr == E_OUTOFMEMORY || hr == unchecked((int)0x80070008) || hr == unchecked((int)0x80030008);

    internal static void ThrowIfAllocationFailure(int hr)
    {
        if (IsAllocationFailure(hr))
            throw new OutOfMemoryException("Native clipboard allocation failed.");
    }

    internal static void Check(int hr)
    {
        ThrowIfAllocationFailure(hr);
        if (hr < 0)
            throw new ExternalException("Clipboard/OLE operation failed.", hr);
    }

    internal static void Open(TimeSpan budget)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!OpenClipboard(0))
        {
            if (watch.Elapsed >= budget)
                throw new ExternalException("The clipboard is busy.", CLIPBRD_E_CANT_OPEN);
            Thread.Sleep((int)Math.Min(50, Math.Max(1, (budget - watch.Elapsed).TotalMilliseconds)));
        }
    }

    internal static string? FormatName(uint format)
    {
        if (format < 0xC000)
            return null;
        char* name = stackalloc char[256];
        int length = GetClipboardFormatNameW(format, name, 256);
        return length > 0 ? new string(name, 0, length) : null;
    }

    internal static uint Register(string name)
    {
        uint format = RegisterClipboardFormatW(name);
        if (format == 0)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        return format;
    }

    internal static uint AddRef(nint pointer) =>
        ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)pointer)[1])(pointer);

    internal static uint Release(nint pointer) => pointer == 0 ? 0 :
        ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)pointer)[2])(pointer);

    internal static int GetData(nint pointer, SnapshotFormat* format, NativeMedium* medium) =>
        ((delegate* unmanaged[Stdcall]<nint, SnapshotFormat*, NativeMedium*, int>)
            (*(nint**)pointer)[3])(pointer, format, medium);

    internal static int Enumerate(nint pointer, nint* enumerator) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)
            (*(nint**)pointer)[8])(pointer, 1, enumerator);

    internal static int Next(nint pointer, SnapshotFormat* format, uint* fetched) =>
        ((delegate* unmanaged[Stdcall]<nint, uint, SnapshotFormat*, uint*, int>)
            (*(nint**)pointer)[3])(pointer, 1, format, fetched);

    internal static byte[]? CopyTargetDevice(nint pointer)
    {
        if (pointer == 0) return null;
        uint size = *(uint*)pointer;
        if (size < 12 || size > 1024 * 1024)
            throw new ExternalException("Invalid target-device descriptor.", DV_E_DVTARGETDEVICE);
        return new ReadOnlySpan<byte>((void*)pointer, checked((int)size)).ToArray();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenClipboard(nint window);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")]
    internal static extern nint GetClipboardOwner();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterClipboardFormatW(string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClipboardFormatNameW(uint format, char* name, int maximum);
    [DllImport("ole32.dll")]
    internal static extern int OleGetClipboard(out nint dataObject);
    [DllImport("ole32.dll")]
    internal static extern int OleSetClipboard(nint dataObject);
    [DllImport("ole32.dll")]
    internal static extern int OleIsCurrentClipboard(nint dataObject);
    [DllImport("ole32.dll")]
    internal static extern int OleFlushClipboard();
    [DllImport("ole32.dll")]
    internal static extern void ReleaseStgMedium(ref NativeMedium medium);
    [DllImport("ole32.dll")]
    internal static extern void CoTaskMemFree(nint pointer);
    [DllImport("ole32.dll")]
    internal static extern nint CoTaskMemAlloc(nuint size);
}
