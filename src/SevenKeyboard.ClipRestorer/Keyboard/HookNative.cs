using System.Runtime.InteropServices;

namespace SevenKeyboard;

internal static partial class HookNative
{
    internal const ushort VkV = 0x56, VkApps = 0x5D, VkControl = 0x11, VkShift = 0x10, VkAlt = 0x12;
    internal const ushort VkInsert = 0x2D, VkF10 = 0x79, VkLShift = 0xA0, VkRButton = 0x02;
    internal const ushort VkLControl = 0xA2, VkRControl = 0xA3, VkLWin = 0x5B, VkRWin = 0x5C;
    internal const uint KeyUp = 2, ExtendedKey = 1;
    internal const nuint InjectionMarker = 0x534B4352; // SKCR; valid at both pointer widths.

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardEvent
    {
        internal uint VirtualKey, ScanCode, Flags, Time;
        internal nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseEvent
    {
        internal int X, Y;
        internal uint Data, Flags, Time;
        internal nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int X, Y;
        internal uint Private;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        internal ushort VirtualKey, ScanCode;
        internal uint Flags, Time;
        internal nuint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        internal int X, Y;
        internal uint Data, Flags, Time;
        internal nuint ExtraInfo;
    }
    // INPUT includes the larger MOUSEINPUT even when only sending keys. Natural
    // pointer alignment makes sizeof(INPUT) 40 on x64 and 28 on x86.
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] internal KeyboardInput Keyboard;
        [FieldOffset(0)] internal MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        internal uint Type;
        internal InputUnion Data;
    }

    [LibraryImport("kernel32.dll")] internal static partial uint GetCurrentThreadId();
    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint GetModuleHandleW(string? name);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial nint GetThreadDesktop(uint thread);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial int SetThreadDesktop(nint desktop);
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetWindowsHookExW(int type, nint callback, nint module, uint thread);
    [LibraryImport("user32.dll", SetLastError = true)] internal static partial int UnhookWindowsHookEx(nint hook);
    [LibraryImport("user32.dll")] internal static partial nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [LibraryImport("user32.dll")] internal static partial short GetAsyncKeyState(int virtualKey);
    [LibraryImport("user32.dll")]
    internal static partial int PeekMessageW(out Message message, nint window, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")] internal static partial int TranslateMessage(in Message message);
    [LibraryImport("user32.dll")] internal static partial nint DispatchMessageW(in Message message);
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static unsafe partial uint MsgWaitForMultipleObjectsEx(uint count, nint* handles, uint milliseconds, uint wakeMask, uint flags);
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static unsafe partial uint SendInput(uint count, Input* inputs, int size);
}
