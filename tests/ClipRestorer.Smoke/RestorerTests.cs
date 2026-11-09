using System.Runtime.InteropServices;
using System.Text;
using SevenKeyboard;

namespace ClipRestorerVerification;

// Scheduling is tested only after private desktop/station proof. No input is sent.
internal static class RestorerTests
{
    internal static void Run()
    {
        Isolation.Require();
        try
        {
            TimerGeneration();
            Prepare();
            var completed = ClipRestorer.ScheduleRestore(TimeSpan.FromMilliseconds(30));
            var result = completed.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            Program.Check(result is { IsComplete: true } && Read() == "saved", "timer restores saved clipboard");
            Program.Check(!ClipRestorer.HasBackup && !ClipRestorer.IsPending, "successful timer releases backup and interception");

            Prepare();
            var cancelled = ClipRestorer.ScheduleRestore(TimeSpan.FromSeconds(30));
            Put("external copy");
            Program.Throws<TaskCanceledException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(),
                "external copy cancels scheduled completion");
            Program.Check(!ClipRestorer.HasBackup && Read() == "external copy", "external copy survives and old backup is discarded");

            Prepare();
            var retained = ClipRestorer.ScheduleRestore(TimeSpan.FromSeconds(30));
            ClipRestorer.CancelRestore();
            Program.Check(retained.IsCanceled && ClipRestorer.HasBackup && !ClipRestorer.IsPending,
                "explicit cancellation keeps backup and stops interception");
            Program.Check(ClipRestorer.Restore() is { IsComplete: true } && Read() == "saved", "cancelled backup can restore manually");

            BusyFailure();
            Program.Note("SUITE PASS: RestorerTests (isolated hook lifecycle; no keystrokes injected)");
        }
        finally { ClipRestorer.DeleteBackup(); }
    }

    private static void TimerGeneration()
    {
        int elapsed = 0;
        Action observe = () => elapsed++;
        ClipboardThread.Invoke(() =>
        {
            ClipboardThread.TimerElapsed += observe;
            ClipboardThread.StartTimer(30000);
            nuint stale = ClipboardThread.TimerId;
            ClipboardThread.StopTimer();
            ClipboardThread.StartTimer(30000);
            Program.Check(stale != ClipboardThread.TimerId, "rearming uses a distinct timer identity");
            Native.Check(ThreadNative.PostMessageW(ClipboardThread.Window, 0x0113, stale, 0) != 0,
                "post a stale timer message");
        });
        Thread.Sleep(50);
        ClipboardThread.Invoke(() =>
        {
            ClipboardThread.StopTimer();
            ClipboardThread.TimerElapsed -= observe;
            Program.Check(elapsed == 0, "stale timer cannot fire a newly scheduled restore");
        });
    }
    private static void Prepare()
    {
        ClipRestorer.DeleteBackup();
        Put("saved");
        Program.Check(ClipRestorer.CreateBackup().Count == 0, "facade captures clean backup");
        Put("temporary");
    }
    private static void Put(string text) => ClipboardThread.Invoke(() => Native.WithClipboard(() =>
    {
        Native.Check(Native.EmptyClipboard(), "replace isolated text");
        Native.Put(13, Encoding.Unicode.GetBytes(text + "\0"));
    }));
    private static string Read() => Native.WithClipboard(() =>
        Encoding.Unicode.GetString(Native.Read(13) ?? []).TrimEnd('\0'));

    private static void BusyFailure()
    {
        Prepare();
        nint desktop = Native.GetThreadDesktop(Native.GetCurrentThreadId());
        using var opened = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Exception? holderError = null;
        var holder = new Thread(() =>
        {
            try
            {
                Native.Check(Native.SetThreadDesktop(desktop) != 0, "holder inherits private desktop");
                Isolation.Require();
                Native.WithClipboard(() =>
                {
                    opened.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("holder release timed out");
                });
                Native.DestroyOwnerWindow();
            }
            catch (Exception error) { holderError = error; opened.Set(); }
        }) { IsBackground = true };
        holder.Start();
        try
        {
            if (!opened.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("holder did not start");
            if (holderError is not null) throw new InvalidOperationException("holder failed", holderError);
            var attempt = ClipRestorer.ScheduleRestore(TimeSpan.FromMilliseconds(30), TimeSpan.Zero);
            Program.Throws<ExternalException>(() => attempt.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(),
                "clipboard contention reports total failure through Completion");
            Program.Check(ClipRestorer.HasBackup && ClipRestorer.IsPending, "total failure retains backup and interception for next request");
        }
        finally
        {
            release.Set();
            if (!holder.Join(TimeSpan.FromSeconds(5))) throw new TimeoutException("holder did not stop");
        }
        if (holderError is not null) throw new InvalidOperationException("holder failed", holderError);
        Program.Check(ClipRestorer.Restore() is { IsComplete: true } && Read() == "saved", "explicit retry succeeds after clipboard contention ends");
        Program.Check(!ClipRestorer.HasBackup && !ClipRestorer.IsPending, "retry success clears pending state");
    }
}
