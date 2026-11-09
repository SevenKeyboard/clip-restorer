using System.Collections.ObjectModel;

namespace SevenKeyboard;

/// <summary>A detached clipboard snapshot. Capture and publication run on the shared STA.</summary>
public sealed class ClipboardSnapshot : IDisposable
{
    private SnapshotEntry[]? _entries;
    private readonly ReadOnlyCollection<ClipboardFailure> _readFailures;
    private readonly ReadOnlyCollection<ClipboardFailure> _skippedFormats;

    internal ClipboardSnapshot(SnapshotEntry[] entries, IEnumerable<ClipboardFailure> failures,
        IEnumerable<ClipboardFailure> skipped, bool originallyEmpty, uint captureSequence)
    {
        _entries = entries;
        _readFailures = Array.AsReadOnly(failures.ToArray());
        _skippedFormats = Array.AsReadOnly(skipped.ToArray());
        IsEmpty = originallyEmpty;
        CaptureSequence = captureSequence;
    }

    public IReadOnlyList<ClipboardFailure> ReadFailures => _readFailures;
    public IReadOnlyList<ClipboardFailure> SkippedFormats => _skippedFormats;
    public bool IsEmpty { get; }
    internal uint CaptureSequence { get; }
    internal uint? LastMutationSequence { get; private set; }

    /// <summary>Copies a stable view. Rejects owner changes or a source that keeps changing during capture.</summary>
    public static ClipboardSnapshot Capture(TimeSpan? timeout = null)
    {
        TimeSpan budget = SnapshotNative.ValidateTimeout(timeout);
        return ClipboardThread.Invoke(() => SnapshotCapture.Capture(budget));
    }

    /// <summary>
    /// Publishes a separately owned OLE provider. Timeout applies only to clipboard-open contention.
    /// RestoredFormatCount counts FORMATETC entries, including each indexed file.
    /// </summary>
    public ClipboardRestoreResult Restore(TimeSpan? timeout = null) => RestoreCore(timeout, null);

    internal ClipboardRestoreResult Restore(TimeSpan? timeout, Func<bool> mayReplace) =>
        RestoreCore(timeout, mayReplace ?? throw new ArgumentNullException(nameof(mayReplace)));

    private ClipboardRestoreResult RestoreCore(TimeSpan? timeout, Func<bool>? mayReplace)
    {
        TimeSpan budget = SnapshotNative.ValidateTimeout(timeout);
        return ClipboardThread.Invoke(() =>
        {
            LastMutationSequence = null;
            SnapshotEntry[] entries = _entries ?? throw new ObjectDisposedException(nameof(ClipboardSnapshot));
            return SnapshotRuntime.Restore(entries, IsEmpty, budget, mayReplace,
                sequence => LastMutationSequence = sequence);
        });
    }

    public void Dispose() => ClipboardThread.Invoke(() =>
    {
        SnapshotEntry[]? entries = Interlocked.Exchange(ref _entries, null);
        if (entries is not null)
            SnapshotEntry.DisposeAll(entries);
    });
}

/// <summary>A format-specific native failure. Index is -1 for non-indexed formats.</summary>
public readonly record struct ClipboardFailure(uint Format, string? Name, int Index, int ErrorCode);

public sealed class ClipboardRestoreResult
{
    internal ClipboardRestoreResult(IEnumerable<ClipboardFailure> failures, int restoredCount)
    {
        WriteFailures = Array.AsReadOnly(failures.ToArray());
        RestoredFormatCount = restoredCount;
    }

    public bool IsComplete => WriteFailures.Count == 0;
    public IReadOnlyList<ClipboardFailure> WriteFailures { get; }
    public int RestoredFormatCount { get; }
    public bool IsEmpty => RestoredFormatCount == 0;
}
