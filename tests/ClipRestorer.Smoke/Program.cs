using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using SevenKeyboard;

namespace ClipRestorerVerification;

internal static class Program
{
    private static int checks;
    private static readonly List<string> output = [];

    // The child process's desktop is chosen before CLR startup, not changed in Main.
    private static int Main(string[] args)
    {
        string? report = null;
        int exitCode;
        try
        {
            if (args.Length == 1 && args[0] is "--help" or "-h")
            {
                Console.WriteLine("ClipRestorer.Smoke [--report <path>]");
                return 0;
            }
            bool child = args.Length != 0 && args[0] == "--isolated-child";
            int offset = child ? 4 : 0;
            if (child && args.Length < offset)
                throw new ArgumentException("Internal child arguments: station, desktop, original station.");
            if (args.Length != offset)
            {
                if (args.Length != offset + 2 || args[offset] != "--report"
                    || string.IsNullOrWhiteSpace(args[offset + 1]))
                    throw new ArgumentException("Usage: ClipRestorer.Smoke [--report <path>]");
                report = Path.GetFullPath(args[offset + 1]);
            }

            if (child)
            {
                var isolation = Isolation.VerifyChild(args[1], args[2], args[3]);
                Note($"ISOLATION PASS: station={isolation.StationName}; desktop={isolation.DesktopName}; nonvisible=true.");
                RunAfterIsolation();
                Note($"PASS: {checks} checks; {RuntimeInformation.ProcessArchitecture}.");
                GC.KeepAlive(isolation);
                exitCode = 0;
            }
            else
            {
                // Parent never calls VerifyChild, Require, or any library/test code.
                exitCode = RunIsolatedChild();
            }
        }
        catch (Exception error)
        {
            Note($"FAIL: {error}");
            exitCode = 1;
        }

        if (report is not null)
        {
            try
            {
                File.WriteAllLines(report, output, new UTF8Encoding(false));
                Console.WriteLine($"Report: {report}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"FAIL: Could not write report: {error.Message}");
                exitCode = 1;
            }
        }
        return exitCode;
    }

    private static int RunIsolatedChild()
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot locate the current signed smoke executable.");
        if (!Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Launch the signed smoke executable directly, not through dotnet.");

        var isolation = new Isolation();
        string childReport = Path.Combine(Path.GetTempPath(), "clip-restorer-child-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            isolation.VerifyParent();
            int code = Native.RunIsolatedChild(executable,
                ["--isolated-child", isolation.StationName, isolation.DesktopName, isolation.OriginalName,
                    "--report", childReport],
                isolation.DesktopPath);
            if (!File.Exists(childReport))
                throw new InvalidOperationException($"Isolated child exited with code {code} without a report.");
            string[] lines = File.ReadAllLines(childReport);
            foreach (string line in lines) Note(line);
            if (code == 0 && (!lines.Any(line => line.StartsWith("ISOLATION PASS:", StringComparison.Ordinal))
                || !lines.Any(line => line.StartsWith("PASS: ", StringComparison.Ordinal) && line.Contains(" checks; ", StringComparison.Ordinal))))
                throw new InvalidOperationException("The child exited successfully without completed isolation/test proof.");
            if (code != 0) Note($"FAIL: Isolated child exit code {code}.");
            return code;
        }
        finally
        {
            // RunIsolatedChild waits for exit (or terminates its timed-out child)
            // before returning. Keep all parent handles and the mutex until then.
            GC.KeepAlive(isolation);
            if (File.Exists(childReport)) File.Delete(childReport);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunAfterIsolation() => IsolatedTests.Run();

    internal static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        Interlocked.Increment(ref checks);
        Note("PASS: " + description);
    }

    internal static void Throws<TException>(Action action, string description) where TException : Exception
    {
        try { action(); }
        catch (TException) { Check(true, description); return; }
        throw new InvalidOperationException(description + $": expected {typeof(TException).Name}.");
    }

    internal static void Note(string message)
    {
        lock (output)
        {
            output.Add(message);
            Console.WriteLine(message);
        }
    }
}

/// <summary>All references that initialize the library stay behind the bootstrap.</summary>
internal static class IsolatedTests
{
    // Explicit initializer prevents beforefieldinit initialization before isolation.
    static IsolatedTests() { Isolation.Require(); }

    internal static void Run()
    {
        Isolation.Require();
        // Parent actor must bind this desktop before its own COM/window setup.
        ClipboardThread.Invoke(Isolation.Require);
        try
        {
            Program.Check(ClipboardThread.Invoke(() => ClipboardThread.IsCurrent),
                "fixture executes on the shared STA actor");
            Program.Check(ClipboardThread.Invoke(() => Thread.CurrentThread.GetApartmentState() == ApartmentState.STA),
                "clipboard actor apartment is STA");
            List<Exception> failures = [];
            void Case(Action action, string name)
            {
                try { action(); }
                catch (Exception error) { failures.Add(error); Program.Note($"CASE FAIL: {name}: {error}"); }
                finally { ClipboardThread.Invoke(() => { ClipRestorer.DeleteBackup(); Native.Clear(); }); }
            }
            Case(MemoryRoundTrip, nameof(MemoryRoundTrip));
            Case(EmptyRoundTrip, nameof(EmptyRoundTrip));
            Case(ManualBackupRoundTrip, nameof(ManualBackupRoundTrip));
            Program.Note($"PASS: {PasteKeyboardPolicyTests.Run()} pure keyboard policy assertions (no input injection).");
            Case(RestorerTests.Run, nameof(RestorerTests));
#if SNAPSHOT_TESTS
            Case(() => RunAdditionalSuite(SnapshotTests.Run, nameof(SnapshotTests)), nameof(SnapshotTests));
#else
            Program.Note("NOT INCLUDED: SnapshotTests.cs");
#endif
#if NATIVE_PAYLOAD_TESTS
            Case(() => RunAdditionalSuite(NativePayloadTests.Run, nameof(NativePayloadTests)), nameof(NativePayloadTests));
#else
            Program.Note("NOT INCLUDED: NativePayloadTests.cs");
#endif
            if (failures.Count != 0) throw new AggregateException(failures);
        }
        finally
        {
            // Manual facade operations only; no ScheduleRestore or input hooks.
            // No fallback cleanup on the original station if a guard fails.
            ClipboardThread.Invoke(() =>
            {
                Isolation.Require();
                ClipRestorer.DeleteBackup();
                Native.Clear();
                Native.DestroyOwnerWindow();
            });
        }
    }

    private static void RunAdditionalSuite(Action suite, string name)
    {
        ClipboardThread.Invoke(() =>
        {
            Isolation.Require();
            Native.Clear();
            try
            {
                suite();
                Program.Note("SUITE PASS: " + name);
            }
            finally { Native.Clear(); }
        });
    }

    private static void Fixture(Action action)
    {
        Isolation.Require();
        Native.WithClipboard(action);
    }

    private static T Fixture<T>(Func<T> action)
    {
        Isolation.Require();
        return Native.WithClipboard(action);
    }

    private static void MemoryRoundTrip()
    {
        const string text = "ClipRestorer \uD55C\uAE00 \u65E5\u672C\u8A9E \U0001F600 e\u0301\r\nsecond line";
        byte[] unicode = Encoding.Unicode.GetBytes(text + "\0");
        // Embedded zeroes and high bytes ensure this is not a text-only test.
        byte[] custom = [0, 1, 2, 0, 0x7F, 0x80, 0xFE, 0xFF, 0x42, 0, 0x33, 0x55, 0, 0xAA, 0xCC, 0xDD];
        uint customFormat = ClipboardThread.Invoke(() =>
            Native.RegisterClipboardFormatW("SevenKeyboard.ClipRestorer.Smoke.Binary." + Guid.NewGuid().ToString("N")));
        uint replacementFormat = ClipboardThread.Invoke(() =>
            Native.RegisterClipboardFormatW("SevenKeyboard.ClipRestorer.Smoke.Replacement." + Guid.NewGuid().ToString("N")));

        Fixture(() =>
        {
            Native.Check(Native.EmptyClipboard(), "Clear initial memory fixture");
            Native.Put(Native.CF_UNICODETEXT, unicode);
            Native.Put(customFormat, custom);
        });
        // Capture the native allocations after CloseClipboard; Windows may round
        // HGLOBAL sizes. Compare their full actual bytes rather than assuming size.
        byte[] expectedUnicode = Fixture(() => RequiredBytes(Native.CF_UNICODETEXT));
        byte[] expectedCustom = Fixture(() => RequiredBytes(customFormat));
        Program.Check(expectedUnicode.AsSpan().StartsWith(unicode), "Unicode seed bytes match the fixture");
        Program.Check(expectedCustom.AsSpan().StartsWith(custom), "binary seed bytes match the fixture");

        Isolation.Require();
        using var snapshot = OnIsolatedThread(() => ClipboardSnapshot.Capture());
        Program.Check(!snapshot.IsEmpty, "Unicode/custom capture is nonempty");
        Program.Check(snapshot.ReadFailures.Count == 0, "Unicode/custom capture has no read failures");

        void Replace()
        {
            Fixture(() =>
            {
                Native.Check(Native.EmptyClipboard(), "Replace captured fixture");
                Native.Put(Native.CF_UNICODETEXT, Encoding.Unicode.GetBytes("replacement\0"));
                Native.Put(replacementFormat, new byte[] { 9, 8, 7, 6 });
            });
            Program.Check(Fixture(() => !Native.Formats().Contains(customFormat)),
                "replacement removes the original custom format before restore");
        }

        void VerifyRestored(string stage)
        {
            Fixture(() =>
            {
                Program.Check(RequiredBytes(Native.CF_UNICODETEXT).AsSpan().SequenceEqual(expectedUnicode),
                    stage + ": Unicode memory is preserved byte for byte");
                Program.Check(RequiredBytes(customFormat).AsSpan().SequenceEqual(expectedCustom),
                    stage + ": custom binary memory is preserved byte for byte");
                Program.Check(!Native.Formats().Contains(replacementFormat),
                    stage + ": replacement-only format is removed");
            });
        }

        Replace();
        Isolation.Require();
        ClipboardRestoreResult first = snapshot.Restore();
        CheckComplete(first, false, "first restore");
        Program.Check(first.RestoredFormatCount >= 2, "first restore includes both source memory formats");
        VerifyRestored("first restore");

        Replace();
        Isolation.Require();
        ClipboardRestoreResult second = OnIsolatedThread(() => snapshot.Restore(TimeSpan.FromMilliseconds(500)));
        CheckComplete(second, false, "repeated restore");
        Program.Check(second.RestoredFormatCount == first.RestoredFormatCount,
            "repeated restore preserves the format count");
        VerifyRestored("repeated restore");

        OnIsolatedThread(() => { snapshot.Dispose(); return 0; });
        snapshot.Dispose();
        VerifyRestored("after snapshot disposal");
        Program.Throws<ObjectDisposedException>(() => snapshot.Restore(),
            "restoring a disposed snapshot is rejected");
    }

    private static void EmptyRoundTrip()
    {
        ClipboardThread.Invoke(Native.Clear);
        Program.Check(Fixture(() => Native.Formats().Count == 0), "empty fixture has no formats");
        Isolation.Require();
        using var snapshot = ClipboardSnapshot.Capture(TimeSpan.Zero);
        Program.Check(snapshot.IsEmpty, "empty clipboard captures as a valid empty snapshot");
        Program.Check(snapshot.ReadFailures.Count == 0, "empty capture has no read failures");
        Fixture(() => Native.Put(Native.CF_UNICODETEXT, Encoding.Unicode.GetBytes("remove me\0")));
        Program.Check(Fixture(() => Native.Formats().Count != 0), "empty restore starts with replacement data");

        Isolation.Require();
        ClipboardRestoreResult result = snapshot.Restore();
        CheckComplete(result, true, "empty restore");
        Program.Check(result.RestoredFormatCount == 0, "empty restore reports zero restored formats");
        Program.Check(Fixture(() => Native.Formats().Count == 0), "empty restore clears all clipboard formats");

        Isolation.Require();
        ClipboardRestoreResult again = snapshot.Restore();
        CheckComplete(again, true, "repeated empty restore");
        Program.Check(Fixture(() => Native.Formats().Count == 0), "repeated empty restore stays empty");
    }


    private static T OnIsolatedThread<T>(Func<T> action)
    {
        Isolation.Require();
        nint desktop = Native.GetThreadDesktop(Native.GetCurrentThreadId());
        uint caller = Native.GetCurrentThreadId();
        T result = default!;
        Exception? failure = null;
        var worker = new Thread(() =>
        {
            try
            {
                Native.Check(Native.SetThreadDesktop(desktop) != 0, "SetThreadDesktop for cross-thread snapshot call");
                Isolation.Require();
                Program.Check(Native.GetCurrentThreadId() != caller && !ClipboardThread.IsCurrent,
                    "snapshot call runs on a distinct caller thread outside the STA actor");
                result = action();
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true, Name = "Isolated snapshot caller" };
        worker.Start();
        if (!worker.Join(TimeSpan.FromSeconds(15)))
            throw new TimeoutException("A cross-thread snapshot call did not complete within 15 seconds.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    private static void ManualBackupRoundTrip()
    {
        byte[] original = Encoding.Unicode.GetBytes("manual backup \uD55C\U0001F600\0");
        byte[] temporary = Encoding.Unicode.GetBytes("temporary manual data\0");
        byte[] latest = Encoding.Unicode.GetBytes("latest backup\0");
        void Seed(byte[] bytes) => Fixture(() =>
        {
            Native.Check(Native.EmptyClipboard(), "Clear manual backup fixture");
            Native.Put(Native.CF_UNICODETEXT, bytes);
        });
        byte[] Current() => Fixture(() => RequiredBytes(Native.CF_UNICODETEXT));
        void Expect(byte[] bytes, string description) =>
            Program.Check(Current().AsSpan().SequenceEqual(bytes), description);

        Isolation.Require();
        ClipRestorer.DeleteBackup();
        Program.Check(!ClipRestorer.HasBackup && !ClipRestorer.IsPending, "manual facade starts without a backup or pending restore");
        Program.Check(ClipRestorer.Restore() is null, "manual Restore returns null without a backup");
        try
        {
            Seed(original);
            byte[] expectedOriginal = Current();
            using var independent = ClipboardSnapshot.Capture();
            Program.Check(ClipRestorer.CreateBackup().Count == 0, "CreateBackup reports no memory read failures");
            Program.Check(ClipRestorer.HasBackup && !ClipRestorer.IsPending, "CreateBackup retains data without scheduling");
            Expect(expectedOriginal, "CreateBackup leaves clipboard contents unchanged");
            Seed(temporary);
            ClipboardRestoreResult restored = ClipRestorer.Restore()
                ?? throw new InvalidOperationException("A valid manual backup was not restored.");
            CheckComplete(restored, false, "manual backup restore");
            Program.Check(!ClipRestorer.HasBackup && !ClipRestorer.IsPending, "manual Restore consumes the private backup");
            Expect(expectedOriginal, "published data remains readable after private backup disposal");
            Program.Check(ClipRestorer.Restore() is null, "manual Restore is null after consuming its backup");
            Expect(expectedOriginal, "Restore without a backup leaves published data unchanged");

            Seed(temporary);
            CheckComplete(independent.Restore(), false, "independent snapshot after facade restore");
            Expect(expectedOriginal, "independent snapshot survives private backup disposal");
            Seed(latest);
            CheckComplete(independent.Restore(), false, "independent repeated snapshot restore");
            Expect(expectedOriginal, "independent snapshot remains reusable");

            Program.Check(ClipRestorer.CreateBackup().Count == 0, "first replaceable backup succeeds");
            Seed(latest);
            byte[] expectedLatest = Current();
            Program.Check(ClipRestorer.CreateBackup().Count == 0, "second CreateBackup replaces the private backup");
            Seed(temporary);
            restored = ClipRestorer.Restore() ?? throw new InvalidOperationException("Replacement backup missing.");
            CheckComplete(restored, false, "replacement backup restore");
            Expect(expectedLatest, "replaced backup restores the latest capture");

            Program.Check(ClipRestorer.CreateBackup().Count == 0, "backup for deletion succeeds");
            Seed(temporary);
            byte[] expectedTemporary = Current();
            ClipRestorer.DeleteBackup();
            ClipRestorer.DeleteBackup();
            Program.Check(!ClipRestorer.HasBackup && !ClipRestorer.IsPending, "DeleteBackup is idempotent and clears state");
            Expect(expectedTemporary, "DeleteBackup preserves current clipboard contents");
            Program.Check(ClipRestorer.Restore() is null, "Restore after DeleteBackup returns null");

            ClipboardThread.Invoke(Native.Clear);
            Program.Check(ClipRestorer.CreateBackup().Count == 0 && ClipRestorer.HasBackup,
                "an empty clipboard is still a valid private backup");
            Seed(temporary);
            restored = ClipRestorer.Restore() ?? throw new InvalidOperationException("Empty backup missing.");
            CheckComplete(restored, true, "manual empty backup restore");
            Program.Check(!ClipRestorer.HasBackup && Fixture(() => Native.Formats().Count == 0),
                "manual empty backup restore consumes backup and clears clipboard");
        }
        finally { ClipRestorer.DeleteBackup(); }
    }

    private static byte[] RequiredBytes(uint format) =>
        Native.Read(format) ?? throw new InvalidOperationException($"Expected memory format {format} is absent.");

    private static void CheckComplete(ClipboardRestoreResult result, bool empty, string stage)
    {
        Program.Check(result.IsComplete && result.WriteFailures.Count == 0, stage + ": no write failures");
        Program.Check(result.IsEmpty == empty, stage + ": empty flag matches snapshot");
    }
}