using System.Runtime.InteropServices;

namespace SevenKeyboard;

/// <summary>An owned STGMEDIUM, or a borrowed view when passed to Capture.
/// Copies of this struct must not both be disposed.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NativeMedium : IDisposable
{
    public uint Tymed;
    public nint Handle;
    public nint ReleaseOwner;

    public void Dispose()
    {
        var owned = this;
        this = default;
        if (owned.Tymed != 0 || owned.ReleaseOwner != 0)
            NativePayloadNative.ReleaseStgMedium(ref owned);
    }
}
