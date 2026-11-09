namespace SevenKeyboard;

internal sealed class SnapshotEntry : IDisposable
{
    internal SnapshotEntry(NativePayload payload, uint aspect = SnapshotNative.ContentAspect,
        byte[]? targetDevice = null, bool virtualFile = false)
    {
        Payload = payload;
        Aspect = aspect;
        TargetDevice = targetDevice;
        VirtualFile = virtualFile;
    }

    internal NativePayload Payload { get; }
    internal uint Aspect { get; }
    internal byte[]? TargetDevice { get; }
    internal bool VirtualFile { get; }
    internal ClipboardFailure Failure(int hr) =>
        new(Payload.Format, Payload.Name, Payload.Index, hr);
    internal SnapshotEntry Clone() => new(Payload.Clone(), Aspect, TargetDevice, VirtualFile);
    public void Dispose() => Payload.Dispose();

    internal static void DisposeAll(IEnumerable<SnapshotEntry> entries)
    {
        foreach (SnapshotEntry entry in entries)
        {
            // Cleanup must reach every owned payload even if a particular backend fails.
            try { entry.Dispose(); } catch { }
        }
    }
}

internal sealed record SnapshotRequest(uint Format, string? Name, int Index, uint Aspect,
    uint Tymed, byte[]? TargetDevice = null)
{
    internal ClipboardFailure Failure(int hr) => new(Format, Name, Index, hr);
}
