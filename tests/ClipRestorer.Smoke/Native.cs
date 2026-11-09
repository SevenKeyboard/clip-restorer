using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClipRestorerVerification;

/// <summary>Guarded fixture APIs. No clipboard access is allowed before isolation.</summary>
internal static partial class Native
{
    internal const uint CF_UNICODETEXT = 13;
    private const int MaximumFixtureBytes = 64 * 1024 * 1024;
    [ThreadStatic] private static bool clipboardOpen;
    [ThreadStatic] private static nint ownerWindow;

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateWindowStationW(string? name, uint flags, uint access, nint attributes);
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int SetProcessWindowStation(nint station);
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint GetProcessWindowStation();
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateDesktopW(string name, nint device, nint mode, uint flags, uint access, nint attributes);
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial int SetThreadDesktop(nint desktop);
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint GetThreadDesktop(uint thread);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int GetUserObjectInformationW(nint handle, int index, nint data, uint length, out uint needed);
    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial int DestroyWindow(nint window);
    [LibraryImport("user32.dll", EntryPoint = "OpenClipboard", SetLastError = true)]
    private static partial int OpenClipboardCore(nint owner);
    [LibraryImport("user32.dll", EntryPoint = "CloseClipboard", SetLastError = true)]
    private static partial int CloseClipboardCore();
    [LibraryImport("user32.dll", EntryPoint = "EmptyClipboard", SetLastError = true)]
    private static partial int EmptyClipboardCore();
    [LibraryImport("user32.dll", EntryPoint = "EnumClipboardFormats", SetLastError = true)]
    private static partial uint EnumClipboardFormatsCore(uint previous);
    [LibraryImport("user32.dll", EntryPoint = "GetClipboardData", SetLastError = true)]
    private static partial nint GetClipboardDataCore(uint format);
    [LibraryImport("user32.dll", EntryPoint = "SetClipboardData", SetLastError = true)]
    private static partial nint SetClipboardDataCore(uint format, nint memory);
    [LibraryImport("user32.dll", EntryPoint = "IsClipboardFormatAvailable")]
    private static partial int IsClipboardFormatAvailableCore(uint format);
    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint RegisterClipboardFormatCore(string name);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalAlloc(uint flags, nuint size);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalLock(nint memory);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial int GlobalUnlock(nint memory);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GlobalFree(nint memory);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nuint GlobalSize(nint memory);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static unsafe partial int CreateProcessW(string application, char* commandLine,
        nint processSecurity, nint threadSecurity, int inheritHandles, uint flags,
        nint environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int GetExitCodeProcess(nint process, out uint exitCode);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int TerminateProcess(nint process, uint exitCode);
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        internal uint Size;
        internal nint Reserved, Desktop, Title;
        internal uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        internal ushort ShowWindow, ReservedBytes;
        internal nint ReservedPointer, StandardInput, StandardOutput, StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        internal nint Process, Thread;
        internal uint ProcessId, ThreadId;
    }

    // Parent-only process creation. No clipboard APIs, shell, or alternate runtime.
    internal static unsafe int RunIsolatedChild(string executable, IReadOnlyList<string> arguments, string desktopPath)
    {
        var command = new System.Text.StringBuilder(QuoteArgument(executable));
        foreach (string argument in arguments) command.Append(' ').Append(QuoteArgument(argument));
        if (command.Length >= 32767)
            throw new ArgumentException("Child command line is too long.");
        char[] mutableCommand = (command.ToString() + "\0").ToCharArray();
        ProcessInfo child = default;
        try
        {
            fixed (char* desktop = desktopPath)
            fixed (char* commandLine = mutableCommand)
            {
                var startup = new StartupInfo
                {
                    Size = (uint)sizeof(StartupInfo),
                    Desktop = (nint)desktop,
                    Flags = 1, // STARTF_USESHOWWINDOW
                    ShowWindow = 0
                };
                Check(CreateProcessW(executable, commandLine, 0, 0, 0,
                    0x08000000, // CREATE_NO_WINDOW; output travels through the child report.
                    0, Path.GetDirectoryName(executable)!, ref startup, out child) != 0,
                    "CreateProcessW for signed isolated child");
            }

            uint wait = WaitForSingleObject(child.Process, 180_000);
            if (wait == 0x102)
                throw new TimeoutException("Isolated smoke child exceeded three minutes.");
            Check(wait == 0, "WaitForSingleObject for isolated child");
            Check(GetExitCodeProcess(child.Process, out uint exitCode) != 0, "GetExitCodeProcess for isolated child");
            return unchecked((int)exitCode);
        }
        finally
        {
            try
            {
                if (child.Process != 0 && WaitForSingleObject(child.Process, 0) == 0x102)
                {
                    // Terminate only the exact child created above, never another test.
                    Check(TerminateProcess(child.Process, 1) != 0, "Terminate timed-out isolated child");
                    Check(WaitForSingleObject(child.Process, 10_000) == 0, "Wait for terminated isolated child");
                }
            }
            finally
            {
                if (child.Thread != 0) CloseHandle(child.Thread);
                if (child.Process != 0) CloseHandle(child.Process);
            }
        }
    }

    // Windows argv quoting, including quotes and trailing backslashes in paths.
    private static string QuoteArgument(string argument)
    {
        if (argument.Contains('\0')) throw new ArgumentException("Command arguments cannot contain NUL.");
        var quoted = new System.Text.StringBuilder("\"");
        int slashes = 0;
        foreach (char character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            quoted.Append(character);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    internal static void Check(bool success, string operation)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastPInvokeError(), operation);
    }

    internal static string ObjectName(nint handle) => ObjectText(handle, 2);
    internal static string ObjectType(nint handle) => ObjectText(handle, 3);

    private static unsafe string ObjectText(nint handle, int index)
    {
        Check(handle != 0, "Get user object handle");
        int success = GetUserObjectInformationW(handle, index, 0, 0, out uint needed);
        int error = Marshal.GetLastPInvokeError();
        if (success != 0 || error != 122 || needed < 2 || needed > 65536 || (needed & 1) != 0)
            throw new Win32Exception(error, "GetUserObjectInformationW(size)");
        char[] buffer = new char[checked((int)needed / 2)];
        fixed (char* pointer = buffer)
        {
            Check(GetUserObjectInformationW(handle, index, (nint)pointer, needed, out uint actual) != 0,
                "GetUserObjectInformationW(text)");
            if (actual < 2 || actual > needed || (actual & 1) != 0 || buffer[actual / 2 - 1] != '\0')
                throw new InvalidDataException("Invalid user object name/type.");
            return new string(buffer, 0, checked((int)actual / 2 - 1));
        }
    }

    internal static unsafe bool Visible(nint station)
    {
        UserObjectFlags flags = default;
        Check(GetUserObjectInformationW(station, 1, (nint)(&flags),
            (uint)sizeof(UserObjectFlags), out uint needed) != 0, "GetUserObjectInformationW(UOI_FLAGS)");
        if (needed != sizeof(UserObjectFlags))
            throw new InvalidDataException("Invalid window station flags.");
        return (flags.Flags & 1) != 0; // WSF_VISIBLE
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserObjectFlags
    {
        internal int Inherit, Reserved;
        internal uint Flags;
    }

    internal static bool OpenClipboard(nint owner)
    {
        Isolation.Require();
        if (clipboardOpen) throw new InvalidOperationException("Nested fixture clipboard open.");
        bool opened = OpenClipboardCore(owner) != 0;
        if (opened) clipboardOpen = true;
        return opened;
    }

    internal static bool CloseClipboard()
    {
        RequireOpen();
        bool closed = CloseClipboardCore() != 0;
        if (closed) clipboardOpen = false;
        return closed;
    }

    internal static bool EmptyClipboard()
    {
        RequireOpen();
        return EmptyClipboardCore() != 0;
    }

    internal static uint RegisterClipboardFormatW(string name)
    {
        Isolation.Require();
        uint format = RegisterClipboardFormatCore(name);
        Check(format != 0, "RegisterClipboardFormatW");
        return format;
    }

    internal static bool IsClipboardFormatAvailable(uint format)
    {
        Isolation.Require();
        return IsClipboardFormatAvailableCore(format) != 0;
    }

    internal static nint GetClipboardData(uint format)
    {
        RequireOpen();
        return GetClipboardDataCore(format);
    }

    internal static nint SetClipboardData(uint format, nint memory)
    {
        RequireOpen();
        return SetClipboardDataCore(format, memory);
    }

    private static void RequireOpen()
    {
        Isolation.Require();
        if (!clipboardOpen)
            throw new InvalidOperationException("This fixture thread has not opened the isolated clipboard.");
    }

    internal static void WithClipboard(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Isolation.Require();
        if (ownerWindow == 0)
        {
            // HWND_MESSAGE; the built-in STATIC class needs no callback/delegate.
            ownerWindow = CreateWindowExW(0, "STATIC", "ClipRestorer isolated fixture", 0,
                0, 0, 0, 0, -3, 0, 0, 0);
            Check(ownerWindow != 0, "CreateWindowExW for isolated clipboard owner");
        }
        var timer = Stopwatch.StartNew();
        while (!OpenClipboard(ownerWindow))
        {
            if (timer.Elapsed >= TimeSpan.FromSeconds(1))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Timed out opening isolated fixture clipboard");
            Thread.Sleep(10);
        }
        try { action(); }
        finally { Check(CloseClipboard(), "CloseClipboard for fixture"); }
    }

    internal static T WithClipboard<T>(Func<T> action)
    {
        T result = default!;
        WithClipboard(() => { result = action(); });
        return result;
    }

    internal static void Clear() => WithClipboard(() => Check(EmptyClipboard(), "EmptyClipboard for fixture"));

    internal static void DestroyOwnerWindow()
    {
        Isolation.Require();
        if (clipboardOpen) throw new InvalidOperationException("Cannot destroy an open clipboard fixture.");
        if (ownerWindow == 0) return;
        Check(DestroyWindow(ownerWindow) != 0, "DestroyWindow for fixture");
        ownerWindow = 0;
    }

    internal static void Put(uint format, byte[]? bytes)
    {
        RequireOpen();
        if (bytes is null)
        {
            // A deliberately unrendered format, useful to additional failure tests.
            SetClipboardData(format, 0);
            Check(Formats().Contains(format), "Advertise delayed clipboard format");
            return;
        }
        if (bytes.Length == 0 || bytes.Length > MaximumFixtureBytes)
            throw new ArgumentOutOfRangeException(nameof(bytes), "Use a nonempty bounded memory fixture.");
        nint memory = GlobalAlloc(0x42, (nuint)bytes.Length); // GMEM_MOVEABLE | GMEM_ZEROINIT
        Check(memory != 0, "GlobalAlloc for fixture");
        try
        {
            nint pointer = GlobalLock(memory);
            Check(pointer != 0, "GlobalLock for fixture");
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { GlobalUnlock(memory); }
            Check(SetClipboardData(format, memory) != 0, "SetClipboardData for fixture");
            memory = 0; // Windows owns successful clipboard transfers.
        }
        finally
        {
            if (memory != 0) Check(GlobalFree(memory) == 0, "GlobalFree for unpublished fixture");
        }
    }

    internal static List<uint> Formats()
    {
        RequireOpen();
        List<uint> result = [];
        HashSet<uint> seen = [];
        uint previous = 0;
        while (true)
        {
            // EnumClipboardFormats returns zero for both completion and failure.
            Marshal.SetLastPInvokeError(0);
            uint next = EnumClipboardFormatsCore(previous);
            int error = Marshal.GetLastPInvokeError();
            if (next == 0)
            {
                if (error != 0) throw new Win32Exception(error, "EnumClipboardFormats");
                return result;
            }
            if (!seen.Add(next) || result.Count >= 65535)
                throw new InvalidDataException("Clipboard enumeration cycle/overflow.");
            result.Add(next);
            previous = next;
        }
    }

    internal static byte[]? Read(uint format)
    {
        RequireOpen();
        if (!IsClipboardFormatAvailable(format)) return null;
        nint memory = GetClipboardData(format);
        Check(memory != 0, "GetClipboardData for memory fixture");
        nuint size = GlobalSize(memory);
        if (size == 0) throw new InvalidDataException("The memory fixture has no readable allocation.");
        if (size > MaximumFixtureBytes) throw new InvalidDataException("The memory fixture exceeds the safety bound.");
        nint pointer = GlobalLock(memory);
        Check(pointer != 0, "GlobalLock for native memory read");
        try
        {
            byte[] bytes = new byte[checked((int)size)];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { GlobalUnlock(memory); }
    }
}