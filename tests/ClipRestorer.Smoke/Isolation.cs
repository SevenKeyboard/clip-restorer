namespace ClipRestorerVerification;

/// <summary>Parent creates private objects; only a child launched on them may use the clipboard.</summary>
internal sealed class Isolation
{
    private static Isolation? current;
    private readonly Mutex? testLock;
    private readonly nint station;
    internal nint Desktop { get; }
    internal string StationName { get; }
    internal string DesktopName { get; }
    internal string OriginalName { get; }
    internal string DesktopPath => StationName + "\\" + DesktopName;

    // Parent phase. Never publish current, open a clipboard, or start the library.
    internal Isolation()
    {
        if (current is not null)
            throw new InvalidOperationException("Isolation is already established.");

        testLock = new Mutex(false, @"Local\SevenKeyboard.ClipboardText.IsolatedTests");
        try
        {
            if (!testLock.WaitOne(0))
                throw new InvalidOperationException("Another isolated clipboard test is running.");
        }
        catch (AbandonedMutexException)
        {
            // CREATE_ONLY still independently rejects any surviving station.
        }

        OriginalName = Native.ObjectName(Native.GetProcessWindowStation());
        StationName = "ClipRestorer_" + Guid.NewGuid().ToString("N");
        DesktopName = "ClipRestorer_" + Guid.NewGuid().ToString("N");
        // Named creation requires administrator membership. Never reuse a station.
        station = Native.CreateWindowStationW(StationName, 1, 0x0000037F, 0);
        Native.Check(station != 0, "CreateWindowStationW; isolation unavailable, aborting");
        VerifyStation(station);
        Native.Check(Native.SetProcessWindowStation(station) != 0, "SetProcessWindowStation");
        Desktop = Native.CreateDesktopW(DesktopName, 0, 0, 0, 0x000001FF, 0);
        Native.Check(Desktop != 0, "CreateDesktopW");
        VerifyParent();
    }

    private Isolation(string stationName, string desktopName, string originalName)
    {
        ValidatePrivateName(stationName);
        ValidatePrivateName(desktopName);
        if (string.IsNullOrWhiteSpace(originalName))
            throw new ArgumentException("The parent's original station name is required.");
        StationName = stationName;
        DesktopName = desktopName;
        OriginalName = originalName;
        // Borrow the objects Windows selected via STARTUPINFO.lpDesktop before
        // the CLR initialized. The child must never switch station or desktop.
        station = Native.GetProcessWindowStation();
        Desktop = Native.GetThreadDesktop(Native.GetCurrentThreadId());
    }

    internal void VerifyParent()
    {
        if (testLock is null)
            throw new InvalidOperationException("Only the creating parent may launch the isolated child.");
        VerifyStation(station);
        VerifyStation(Native.GetProcessWindowStation());
        VerifyDesktop(Desktop);
        // Deliberately no verification/switch of the parent's thread desktop,
        // and no clipboard operation. It may already have runtime-owned windows.
    }

    internal static Isolation VerifyChild(string stationName, string desktopName, string originalName)
    {
        if (Volatile.Read(ref current) is not null)
            throw new InvalidOperationException("Child isolation is already established.");
        var isolation = new Isolation(stationName, desktopName, originalName);
        isolation.Verify();
        // Publish only after proving the inherited process station and thread desktop.
        Volatile.Write(ref current, isolation);
        try
        {
            Native.Check(Native.OpenClipboard(0), "OpenClipboard for isolated child empty check");
            try
            {
                if (Native.Formats().Count != 0)
                    throw new InvalidOperationException(
                        "The child station contains clipboard data; aborting without modification.");
            }
            finally { Native.Check(Native.CloseClipboard(), "CloseClipboard after child isolation check"); }
            return isolation;
        }
        catch
        {
            Volatile.Write(ref current, null);
            throw;
        }
    }

    private static void ValidatePrivateName(string name)
    {
        const string prefix = "ClipRestorer_";
        if (!name.StartsWith(prefix, StringComparison.Ordinal)
            || !Guid.TryParseExact(name.AsSpan(prefix.Length), "N", out _))
            throw new ArgumentException("Invalid isolated station/desktop name.");
    }

    private void VerifyStation(nint handle)
    {
        string name = Native.ObjectName(handle);
        if (!name.Equals(StationName, StringComparison.OrdinalIgnoreCase)
            || name.Equals(OriginalName, StringComparison.OrdinalIgnoreCase)
            || name.Equals("WinSta0", StringComparison.OrdinalIgnoreCase)
            || Native.ObjectType(handle) != "WindowStation"
            || Native.Visible(handle))
            throw new InvalidOperationException("ISOLATION FAILURE: refusing clipboard access.");
    }

    private void VerifyDesktop(nint handle)
    {
        if (Native.ObjectType(handle) != "Desktop"
            || !Native.ObjectName(handle).Equals(DesktopName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ISOLATION FAILURE: unexpected desktop.");
    }

    private void Verify()
    {
        VerifyStation(station);
        VerifyStation(Native.GetProcessWindowStation());
        VerifyDesktop(Desktop);
        VerifyDesktop(Native.GetThreadDesktop(Native.GetCurrentThreadId()));
    }

    internal static void Require()
    {
        (Volatile.Read(ref current) ?? throw new InvalidOperationException(
            "Child isolation proof has not completed; refusing clipboard access.")).Verify();
    }

    // Keep parent station/desktop handles and mutex until the child exits. Neither
    // process returns to WinSta0; Windows releases user-object handles at exit.
}