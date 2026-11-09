using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace SevenKeyboard;

/// <summary>One process-wide temporary clipboard backup with delayed restoration and paste interception.</summary>
public static class ClipRestorer
{
    private static ClipboardSnapshot? _backup;
    private static bool _pending, _timerArmed;
    private static long _generation, _lastRestoredGeneration = -1, _lastExternalCancellation = -1;
    private static uint _expectedSequence;
    private static TimeSpan _timeout = TimeSpan.FromMilliseconds(500);
    private static TaskCompletionSource<ClipboardRestoreResult?>? _completion;
    private static Task<ClipboardRestoreResult?> _lastCompletion = Task.FromResult<ClipboardRestoreResult?>(null);

    static ClipRestorer()
    {
        ClipboardThread.ClipboardChanged += OnClipboardChanged;
        ClipboardThread.TimerElapsed += OnTimer;
        ClipboardThread.Exiting += OnExit;
    }

    public static bool HasBackup => ClipboardThread.Invoke(() => _backup is not null);
    public static bool IsPending => ClipboardThread.Invoke(() => _pending);
    /// <summary>The latest scheduled or intercepted restore attempt, including an exception on total failure.</summary>
    public static Task<ClipboardRestoreResult?> Completion => ClipboardThread.Invoke(() => _lastCompletion);

    /// <summary>Captures the current clipboard. Call ScheduleRestore after writing the temporary content.</summary>
    public static IReadOnlyList<ClipboardFailure> CreateBackup(TimeSpan? timeout = null) => ClipboardThread.Invoke(() =>
    {
        ClipboardSnapshot captured = ClipboardSnapshot.Capture(timeout);
        EndPending(cancelCompletion: true);
        _backup?.Dispose();
        _backup = captured;
        _generation++;
        return captured.ReadFailures;
    });

    /// <summary>Schedules one restore. Paste/history shortcuts wait for restoration; menu inputs pass through and request restoration.</summary>
    /// <remarks>Total failure keeps the backup for another explicit attempt. Partial success completes the operation.</remarks>
    public static Task<ClipboardRestoreResult?> ScheduleRestore(TimeSpan? delay = null, TimeSpan? timeout = null) =>
        ClipboardThread.Invoke(() =>
        {
            TimeSpan interval = delay ?? TimeSpan.FromMilliseconds(1200);
            TimeSpan wait = timeout ?? TimeSpan.FromMilliseconds(500);
            if (interval < TimeSpan.Zero || interval.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(delay));
            if (wait < TimeSpan.Zero || wait.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(timeout));
            if (_backup is null) throw new InvalidOperationException("Create a backup before scheduling restoration.");
            EndPending(cancelCompletion: true);
            _generation++;
            _expectedSequence = RestorerNative.GetClipboardSequenceNumber();
            _timeout = wait;
            _pending = true;
            _timerArmed = true;
            _completion = NewCompletion();
            try
            {
                long generation = _generation;
                PasteKeyboardHook.Enable(key => ClipboardThread.Post(() => OnPaste(generation, key)));
                ClipboardThread.StartTimer((uint)Math.Max(1, interval.TotalMilliseconds));
            }
            catch (Exception error)
            {
                EndPending(cancelCompletion: false);
                _completion.TrySetException(error);
                throw;
            }
            return _completion.Task;
        });

    /// <summary>Restores immediately. A total failure throws and retains the backup; partial success returns its failures.</summary>
    public static ClipboardRestoreResult? Restore(TimeSpan? timeout = null) => ClipboardThread.Invoke(() =>
    {
        if (_backup is null) return null;
        if (timeout is { } wait && (wait < TimeSpan.Zero || wait.TotalMilliseconds > int.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        try { return RestoreCore(timeout ?? _timeout); }
        catch (OperationCanceledException)
        {
            CancelForExternalChange();
            throw;
        }
        catch (Exception error)
        {
            _completion?.TrySetException(error);
            throw;
        }
    });

    /// <summary>Cancels scheduling and interception while retaining the backup for an explicit Restore.</summary>
    public static void CancelRestore() => ClipboardThread.Invoke(() => EndPending(cancelCompletion: true));

    /// <summary>Cancels pending restoration and releases the saved backup. Already published data remains available.</summary>
    public static void DeleteBackup() => ClipboardThread.Invoke(() =>
    {
        EndPending(cancelCompletion: true);
        _generation++;
        _backup?.Dispose();
        _backup = null;
    });

    private static bool IsExpectedClipboard() => _expectedSequence != 0 &&
        RestorerNative.GetClipboardSequenceNumber() == _expectedSequence;

    private static ClipboardRestoreResult? RestoreCore(TimeSpan timeout, bool complete = true)
    {
        if (_backup is null) return null;
        _timerArmed = false;
        ClipboardThread.StopTimer();
        if (_pending && !IsExpectedClipboard())
        {
            CancelForExternalChange();
            return null;
        }
        ClipboardSnapshot snapshot = _backup;
        ClipboardRestoreResult result;
        try
        {
            result = _pending ? snapshot.Restore(timeout, IsExpectedClipboard) : snapshot.Restore(timeout);
        }
        catch
        {
            // OLE can mutate the clipboard before reporting failure. Preserve the
            // saved backup when that mutation is still ours; never mask a newer copy.
            if (snapshot.LastMutationSequence is uint ownSequence &&
                ownSequence == RestorerNative.GetClipboardSequenceNumber())
                _expectedSequence = ownSequence;
            throw;
        }
        long completedGeneration = _generation;
        EndPending(cancelCompletion: false);
        _lastRestoredGeneration = completedGeneration;
        _expectedSequence = RestorerNative.GetClipboardSequenceNumber();
        _backup = null;
        snapshot.Dispose();
        if (complete) _completion?.TrySetResult(result);
        return result;
    }

    private static void OnTimer()
    {
        if (!_pending || !_timerArmed) return;
        AttemptScheduled(null);
    }
    private static void OnPaste(long generation, PasteKey key)
    {
        if (!PasteHookPolicy.RequiresReplay(key))
        {
            // The menu/mouse event has already passed to its target. It only
            // advances the current reservation; stale notifications never replay.
            if (generation == _generation && _pending) AttemptScheduled(null);
            return;
        }
        if (generation != _generation)
        {
            if (generation == _lastExternalCancellation && _generation == generation + 1 && _backup is null)
                ReplayObserved(key);
            return;
        }
        if (_pending) AttemptScheduled(key);
        else if (_lastRestoredGeneration == generation && IsExpectedClipboard())
            ReplayObserved(key);
    }
    private static void AttemptScheduled(PasteKey? key)
    {
        if (_completion is null || _completion.Task.IsCompleted) _completion = NewCompletion();
        try
        {
            long generation = _generation;
            ClipboardRestoreResult? result = RestoreCore(_timeout, complete: false);
            if (key is { } paste && (result is not null ||
                (_backup is null && _lastExternalCancellation == generation)))
            {
                if (result is null) ReplayObserved(paste);
                else PasteKeyboardHook.Replay(paste);
            }
            // Completion includes key replay, so SendInput failures are observable.
            _completion?.TrySetResult(result);
        }
        catch (OperationCanceledException)
        {
            CancelForExternalChange();
            if (key is { } paste) ReplayObserved(paste);
        }
        catch (Exception error)
        {
            // No repeating timer. Keep the backup; the next paste/request can explicitly retry.
            _timerArmed = false;
            ClipboardThread.StopTimer();
            _completion?.TrySetException(error);
        }
    }
    private static void ReplayObserved(PasteKey key)
    {
        // A late key/cancellation replay is a separate attempt if the prior task ended.
        if (_completion is null || _completion.Task.IsCompleted) _completion = NewCompletion();
        try { PasteKeyboardHook.Replay(key); _completion.TrySetResult(null); }
        catch (Exception error) { _completion.TrySetException(error); }
    }
    private static TaskCompletionSource<ClipboardRestoreResult?> NewCompletion()
    {
        var source = new TaskCompletionSource<ClipboardRestoreResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _lastCompletion = source.Task;
        return source;
    }
    private static void OnClipboardChanged()
    {
        if (_pending && !IsExpectedClipboard()) CancelForExternalChange();
    }
    private static void CancelForExternalChange()
    {
        _lastExternalCancellation = _generation;
        DeleteBackup();
    }
    private static void EndPending(bool cancelCompletion)
    {
        _pending = false;
        _timerArmed = false;
        ClipboardThread.StopTimer();
        PasteKeyboardHook.Disable();
        if (cancelCompletion) _completion?.TrySetCanceled();
    }
    private static void OnExit()
    {
        try
        {
            if (_pending && _backup is not null && IsExpectedClipboard()) RestoreCore(TimeSpan.Zero);
        }
        catch { /* Exit restoration is best effort. */ }
        finally
        {
            EndPending(cancelCompletion: true);
            PasteKeyboardHook.Shutdown();
            _backup?.Dispose();
            _backup = null;
        }
    }
}
internal static partial class RestorerNative
{
    [LibraryImport("user32.dll")] internal static partial uint GetClipboardSequenceNumber();
}
