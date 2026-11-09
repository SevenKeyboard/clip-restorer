using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SevenKeyboard;

/// <summary>Actor-owned publication reference. OLE and consumers hold separate COM references.</summary>
internal static class SnapshotRuntime
{
    private static nint _current;
    private static bool _subscribed;

    internal static bool IsCurrentPublication => ClipboardThread.Invoke(() =>
    {
        RefreshOwnership();
        return _current != 0;
    });

    internal static void RefreshOwnership()
    {
        if (_current != 0 && SnapshotNative.OleIsCurrentClipboard(_current) != 0)
            ReleaseCurrent();
    }

    internal static ClipboardRestoreResult Restore(SnapshotEntry[] source, bool originallyEmpty,
        TimeSpan budget, Func<bool>? mayReplace, Action<uint> recordMutation)
    {
        if (!_subscribed)
        {
            ClipboardThread.ClipboardChanged += RefreshOwnership;
            _subscribed = true;
        }
        RefreshOwnership();
        CheckGuard(mayReplace);
        if (source.Length == 0)
        {
            if (!originallyEmpty)
                throw new ExternalException("No captured formats can be restored; the clipboard was not cleared.",
                    SnapshotNative.DV_E_FORMATETC);
            Clear(budget, mayReplace, recordMutation);
            return new ClipboardRestoreResult([], 0);
        }

        List<SnapshotEntry> prepared = [];
        List<ClipboardFailure> failures = [];
        nint candidate = 0;
        try
        {
            foreach (SnapshotEntry entry in source.Where(item => !item.VirtualFile))
            {
                try { prepared.Add(entry.Clone()); }
                catch (Exception error) when (error is not OutOfMemoryException && !SnapshotNative.IsAllocationFailure(error.HResult)) { failures.Add(entry.Failure(SnapshotNative.Error(error))); }
            }
            SnapshotEntry[] virtualSource = source.Where(item => item.VirtualFile).ToArray();
            List<SnapshotEntry> virtualCopies = [];
            try
            {
                foreach (SnapshotEntry entry in virtualSource) virtualCopies.Add(entry.Clone());
                prepared.AddRange(virtualCopies);
                virtualCopies.Clear();
            }
            catch (Exception error) when (error is not OutOfMemoryException && !SnapshotNative.IsAllocationFailure(error.HResult))
            {
                foreach (SnapshotEntry entry in virtualSource)
                    failures.Add(entry.Failure(SnapshotNative.Error(error)));
            }
            finally { SnapshotEntry.DisposeAll(virtualCopies); }

            if (prepared.Count == 0)
                throw new ExternalException("No formats could be prepared; the clipboard was not cleared.",
                    failures.Count == 0 ? SnapshotNative.E_FAIL : failures[0].ErrorCode);

            int count = prepared.Count;
            candidate = SnapshotCom.CreateProvider(prepared.ToArray());
            prepared.Clear(); // Provider now owns all detached clones.

            Stopwatch watch = Stopwatch.StartNew();
            while (true)
            {
                // Retry only OpenClipboard contention. This probe is non-destructive;
                // OLE must acquire the clipboard itself, so there remains a Windows race.
                OpenGuarded(budget - watch.Elapsed, mayReplace);
                SnapshotNative.CloseClipboard();
                CheckGuard(mayReplace);
                uint before = SnapshotNative.GetClipboardSequenceNumber();
                int hr = SnapshotNative.OleSetClipboard(candidate);
                uint after = SnapshotNative.GetClipboardSequenceNumber();
                bool ours = SnapshotNative.OleIsCurrentClipboard(candidate) == 0;
                if (ours && after != before) recordMutation(after);
                if (ours)
                {
                    nint old = _current;
                    _current = candidate;
                    candidate = 0; // Keep our initial reference until replacement/shutdown.
                    SnapshotNative.Release(old);
                }
                if (hr >= 0)
                {
                    if (!ours)
                        throw new OperationCanceledException("The clipboard was replaced during publication.");
                    return new ClipboardRestoreResult(failures, count);
                }
                // OLE errors can have side effects. Never retry after confirmed ownership,
                // an observed sequence change, or any failure other than open contention.
                if (hr != SnapshotNative.CLIPBRD_E_CANT_OPEN || ours || before != after ||
                    watch.Elapsed >= budget)
                    throw new ExternalException("OLE clipboard publication failed.", hr);
                CheckGuard(mayReplace);
                Thread.Sleep((int)Math.Min(50, Math.Max(1, (budget - watch.Elapsed).TotalMilliseconds)));
            }
        }
        finally
        {
            SnapshotEntry.DisposeAll(prepared);
            SnapshotNative.Release(candidate);
        }
    }

    private static void CheckGuard(Func<bool>? guard)
    {
        if (guard is not null && !guard())
            throw new OperationCanceledException("The clipboard no longer matches the scheduled restore.");
    }

    private static void OpenGuarded(TimeSpan remaining, Func<bool>? guard)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (true)
        {
            CheckGuard(guard);
            if (SnapshotNative.OpenClipboard(ClipboardThread.Window)) return;
            if (remaining <= TimeSpan.Zero || watch.Elapsed >= remaining)
                throw new ExternalException("The clipboard is busy.", SnapshotNative.CLIPBRD_E_CANT_OPEN);
            Thread.Sleep((int)Math.Min(50, Math.Max(1, (remaining - watch.Elapsed).TotalMilliseconds)));
        }
    }

    private static void Clear(TimeSpan budget, Func<bool>? guard, Action<uint> recordMutation)
    {
        OpenGuarded(budget, guard);
        try
        {
            CheckGuard(guard);
            uint before = SnapshotNative.GetClipboardSequenceNumber();
            bool success = SnapshotNative.EmptyClipboard();
            int error = Marshal.GetLastPInvokeError();
            uint after = SnapshotNative.GetClipboardSequenceNumber();
            if (after != before && SnapshotNative.GetClipboardOwner() == ClipboardThread.Window)
                recordMutation(after);
            if (!success)
                throw new System.ComponentModel.Win32Exception(error);
        }
        finally { SnapshotNative.CloseClipboard(); }
        ReleaseCurrent();
    }

    /// <summary>
    /// Flush only our publication. Windows flushing is best effort and does not guarantee
    /// that multiple FileContents indices remain available after this process exits.
    /// </summary>
    internal static void FlushCurrent()
    {
        RefreshOwnership();
        if (_current != 0)
        {
            SnapshotNative.Check(SnapshotNative.OleFlushClipboard());
            ReleaseCurrent();
        }
    }

    internal static void ReleaseCurrent()
    {
        nint old = _current;
        _current = 0;
        SnapshotNative.Release(old);
    }

    /// <summary>Called on the STA after pending restore cleanup, before OleUninitialize.</summary>
    internal static void Shutdown()
    {
        try { FlushCurrent(); }
        finally
        {
            ReleaseCurrent();
            if (_subscribed)
            {
                ClipboardThread.ClipboardChanged -= RefreshOwnership;
                _subscribed = false;
            }
        }
    }

    internal static void ShutdownCurrent() => Shutdown();
}
