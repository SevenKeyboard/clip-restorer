using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SevenKeyboard;

internal static unsafe class SnapshotCapture
{
    private sealed class UnstableSourceException : Exception
    {
        internal UnstableSourceException(nint owner) => Owner = owner;
        internal nint Owner { get; }
    }

    internal static ClipboardSnapshot Capture(TimeSpan timeout)
    {
        SnapshotRuntime.RefreshOwnership();
        // A first pass can materialize delayed data and change the sequence. Never return
        // that pass. At most two complete retries, with a fresh IDataObject every time.
        for (int attempt = 0; ; attempt++)
        {
            try { return CaptureOnce(timeout); }
            catch (UnstableSourceException error)
            {
                if (attempt >= 2 || SnapshotNative.GetClipboardOwner() != error.Owner)
                    throw new InvalidOperationException("The clipboard changed during capture; no mixed snapshot was retained.");
            }
        }
    }

    private static ClipboardSnapshot CaptureOnce(TimeSpan timeout)
    {
        List<SnapshotEntry> entries = [];
        List<ClipboardFailure> failures = [];
        List<ClipboardFailure> skipped = [];
        nint dataObject = 0;
        bool transferred = false;
        SnapshotNative.Open(timeout);
        uint sequence;
        nint owner;
        List<SnapshotRequest> raw = [];
        try
        {
            sequence = SnapshotNative.GetClipboardSequenceNumber();
            owner = SnapshotNative.GetClipboardOwner();
            uint format = 0;
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                format = SnapshotNative.EnumClipboardFormats(format);
                if (format == 0)
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error != 0) throw new System.ComponentModel.Win32Exception(error);
                    break;
                }
                raw.Add(new SnapshotRequest(format, SnapshotNative.FormatName(format), -1,
                    SnapshotNative.ContentAspect, SnapshotNative.SupportedMedia));
                if (raw.Count > 65536)
                    throw new InvalidOperationException("Invalid clipboard format enumeration.");
            }
        }
        finally { SnapshotNative.CloseClipboard(); }

        bool originallyEmpty = raw.Count == 0;
        try
        {
            List<SnapshotRequest> requests = [];
            if (!originallyEmpty)
            {
                int hr = SnapshotNative.OleGetClipboard(out dataObject);
                SnapshotNative.ThrowIfAllocationFailure(hr);
                if (hr >= 0 && dataObject != 0)
                    ReadOleFormats(dataObject, requests, failures);
                foreach (SnapshotRequest request in raw)
                {
                    if (!requests.Any(item => SameKey(item, request)))
                        requests.Add(request);
                }
            }

            bool hasVirtual = requests.Any(request => IsVirtual(request.Name));
            foreach (SnapshotRequest request in requests)
            {
                if (IsVirtual(request.Name)) continue;
                if (Excluded(request.Name) || request.Format == 0x80) // CF_OWNERDISPLAY is owner-drawn.
                {
                    skipped.Add(request.Failure(SnapshotNative.DV_E_FORMATETC));
                    continue;
                }
                try { entries.Add(CaptureEntry(dataObject, request, timeout)); }
                catch (Exception error) when (error is not OutOfMemoryException && !SnapshotNative.IsAllocationFailure(error.HResult)) { failures.Add(request.Failure(SnapshotNative.Error(error))); }
            }

            if (hasVirtual)
                CaptureVirtualGroup(dataObject, requests, entries, failures, timeout);

            // No partially old/new collection escapes. Owner replacement is also detected
            // when the sequence API cannot provide a useful value.
            if (SnapshotNative.GetClipboardSequenceNumber() != sequence ||
                SnapshotNative.GetClipboardOwner() != owner)
                throw new UnstableSourceException(owner);

            if (!originallyEmpty && entries.Count == 0)
                throw new ExternalException(
                    "The clipboard was nonempty, but no formats could be captured.",
                    failures.Count == 0 ? SnapshotNative.DV_E_FORMATETC : failures[0].ErrorCode);

            var snapshot = new ClipboardSnapshot(entries.ToArray(), failures, skipped, originallyEmpty, sequence);
            transferred = true;
            return snapshot;
        }
        finally
        {
            SnapshotNative.Release(dataObject);
            if (!transferred) SnapshotEntry.DisposeAll(entries);
        }
    }

    internal static bool Excluded(string? name) =>
        name is not null && (
            name.StartsWith("Link Source", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("ObjectLink", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("OwnerLink", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Native", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Embed Source", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("DataObject", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Ole Private Data", StringComparison.OrdinalIgnoreCase));

    internal static bool IsVirtual(string? name) =>
        NameIs(name, "FileGroupDescriptor") || NameIs(name, "FileGroupDescriptorW") || NameIs(name, "FileContents");

    private static bool NameIs(string? actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    private static bool SameKey(SnapshotRequest left, SnapshotRequest right) =>
        left.Format == right.Format && left.Index == right.Index && left.Aspect == right.Aspect &&
        ((left.TargetDevice is null && right.TargetDevice is null) ||
            (left.TargetDevice is not null && right.TargetDevice is not null &&
             left.TargetDevice.AsSpan().SequenceEqual(right.TargetDevice)));

    private static void ReadOleFormats(nint dataObject, List<SnapshotRequest> requests,
        List<ClipboardFailure> failures)
    {
        nint enumerator = 0;
        try
        {
            int hr = SnapshotNative.Enumerate(dataObject, &enumerator);
            SnapshotNative.ThrowIfAllocationFailure(hr);
            if (hr < 0 || enumerator == 0)
            {
                failures.Add(new(0, null, -1, hr < 0 ? hr : SnapshotNative.E_FAIL));
                return;
            }
            for (int i = 0; i < 65536; i++)
            {
                SnapshotFormat format = default;
                uint fetched = 0;
                try
                {
                    hr = SnapshotNative.Next(enumerator, &format, &fetched);
                    SnapshotNative.ThrowIfAllocationFailure(hr);
                    if (hr < 0) { failures.Add(new(0, null, -1, hr)); return; }
                    if (fetched == 0) return;
                    if (fetched != 1 || format.Format == 0)
                        throw new ExternalException("Malformed FORMATETC enumeration.", SnapshotNative.DV_E_FORMATETC);
                    var request = new SnapshotRequest(format.Format,
                        SnapshotNative.FormatName(format.Format), format.Index,
                        format.Aspect, format.Tymed, SnapshotNative.CopyTargetDevice(format.TargetDevice));
                    int existing = requests.FindIndex(item => SameKey(item, request));
                    if (existing >= 0)
                        requests[existing] = requests[existing] with { Tymed = requests[existing].Tymed | request.Tymed };
                    else requests.Add(request);
                    if (hr == SnapshotNative.S_FALSE) return;
                }
                finally { SnapshotNative.CoTaskMemFree(format.TargetDevice); }
            }
            throw new ExternalException("FORMATETC enumeration did not terminate.", SnapshotNative.E_FAIL);
        }
        finally { SnapshotNative.Release(enumerator); }
    }

    internal static SnapshotEntry CaptureEntry(nint source, SnapshotRequest request,
        TimeSpan timeout, bool virtualFile = false, ulong? logicalByteLength = null)
    {
        uint media = request.Tymed & SnapshotNative.SupportedMedia;
        int hr = SnapshotNative.DV_E_TYMED;
        if (source != 0 && media != 0)
        {
            // Give the source its preferred representation first. If that representation
            // cannot be detached, try each permitted medium once, including a narrow
            // request for the source's first choice. A combined mask is never repeated.
            Span<uint> attempts = stackalloc uint[7];
            int attemptCount = 1;
            attempts[0] = media;
            ReadOnlySpan<uint> individualMedia = [8u, 4u, 1u, 16u, 32u, 64u];
            foreach (uint individual in individualMedia)
                if ((media & individual) != 0 && media != individual)
                    attempts[attemptCount++] = individual;

            fixed (byte* device = request.TargetDevice)
            {
                SnapshotFormat format = new()
                {
                    Format = checked((ushort)request.Format), Aspect = request.Aspect,
                    Index = request.Index, TargetDevice = (nint)device
                };
                for (int attempt = 0; attempt < attemptCount; attempt++)
                {
                    format.Tymed = attempts[attempt];
                    NativeMedium medium = default;
                    hr = SnapshotNative.GetData(source, &format, &medium);
                    SnapshotNative.ThrowIfAllocationFailure(hr);
                    // A failing GetData does not transfer any STGMEDIUM ownership.
                    if (hr < 0) continue;
                    NativePayload? payload = null;
                    try
                    {
                        if (medium.Tymed == 0 || (medium.Tymed & (medium.Tymed - 1)) != 0 ||
                            (medium.Tymed & format.Tymed) == 0)
                            throw new ExternalException("Unexpected storage medium.", SnapshotNative.DV_E_TYMED);
                        payload = NativePayload.Capture(request.Format, request.Index, in medium, request.Name);
                        // FD_FILESIZE describes byte content, not an IStorage serialization.
                        // HGLOBAL allocation padding is retained; conversion gets the exact size.
                        if (logicalByteLength is ulong length && payload.Tymed is 1 or 4)
                            payload.SetLogicalByteLength(length);
                        var entry = new SnapshotEntry(payload, request.Aspect, request.TargetDevice, virtualFile);
                        payload = null;
                        return entry;
                    }
                    catch (Exception error) when (error is not OutOfMemoryException &&
                        !SnapshotNative.IsAllocationFailure(error.HResult))
                    {
                        hr = SnapshotNative.Error(error);
                    }
                    finally
                    {
                        try { payload?.Dispose(); }
                        finally { medium.Dispose(); }
                    }
                }
            }
        }
        if (virtualFile || request.Index != -1 || request.Aspect != SnapshotNative.ContentAspect ||
            request.TargetDevice is not null)
            throw new ExternalException("The source did not supply a recoverable requested format.", hr);

        // Win32 fallback preserves ordinary/private memory formats not represented by OLE.
        SnapshotNative.Open(timeout);
        try
        {
            Marshal.SetLastPInvokeError(0);
            nint handle = SnapshotNative.GetClipboardData(request.Format);
            if (handle == 0)
            {
                int error = Marshal.GetLastPInvokeError();
                // Presence-only formats with no payload are preserved only when their
                // protocol defines them as markers. Unknown failed renders are failures.
                if (error == 0 && IsPresenceMarker(request.Name))
                    return new SnapshotEntry(NativePayload.FromMarker(request.Format, request.Name));
                int failure = error == 0 ? hr : unchecked((int)(0x80070000u | (uint)error));
                SnapshotNative.ThrowIfAllocationFailure(failure);
                throw new ExternalException("The source did not render clipboard data.", failure);
            }
            return new SnapshotEntry(NativePayload.FromClipboard(request.Format, handle, request.Name));
        }
        finally { SnapshotNative.CloseClipboard(); }
    }

    private static bool IsPresenceMarker(string? name) =>
        NameIs(name, "Clipboard Viewer Ignore") || NameIs(name, "ExcludeClipboardContentFromMonitorProcessing") ||
        NameIs(name, "MSDEVColumnSelect") || NameIs(name, "MSDEVLineSelect");

    private static void CaptureVirtualGroup(nint source, List<SnapshotRequest> requests,
        List<SnapshotEntry> destination, List<ClipboardFailure> failures, TimeSpan timeout)
    {
        List<SnapshotEntry> group = [];
        SnapshotRequest? active = null;
        try
        {
            SnapshotRequest[] descriptors = requests
                .Where(item => NameIs(item.Name, "FileGroupDescriptorW") || NameIs(item.Name, "FileGroupDescriptor"))
                .GroupBy(item => item.Format).Select(items => items.First()).ToArray();
            if (descriptors.Length == 0)
                throw new ExternalException("Virtual contents have no file descriptor.", SnapshotNative.DV_E_FORMATETC);
            ulong?[]? lengths = null;
            foreach (SnapshotRequest descriptor in descriptors)
            {
                active = descriptor with { Index = -1, Aspect = 1, Tymed = 1, TargetDevice = null };
                SnapshotEntry captured = CaptureEntry(source, active, timeout, true);
                group.Add(captured);
                ulong?[] actual = DescriptorLengths(captured.Payload.ReadBytes(), NameIs(descriptor.Name, "FileGroupDescriptorW"));
                if (lengths is null) lengths = actual;
                else
                {
                    if (lengths.Length != actual.Length)
                        throw new ExternalException("Descriptor counts disagree.", SnapshotNative.DV_E_FORMATETC);
                    for (int index = 0; index < lengths.Length; index++)
                    {
                        if (lengths[index] is ulong known && actual[index] is ulong other && known != other)
                            throw new ExternalException("Descriptor file sizes disagree.", SnapshotNative.DV_E_FORMATETC);
                        lengths[index] ??= actual[index];
                    }
                }
            }
            uint contentsFormat = SnapshotNative.Register("FileContents");
            for (int index = 0; index < lengths!.Length; index++)
            {
                active = new SnapshotRequest(contentsFormat, "FileContents", index, 1, 1 | 4 | 8);
                group.Add(CaptureEntry(source, active, timeout, true, lengths[index]));
            }
            destination.AddRange(group);
            group.Clear();
        }
        catch (Exception error) when (error is not OutOfMemoryException && !SnapshotNative.IsAllocationFailure(error.HResult))
        {
            int hr = SnapshotNative.Error(error);
            if (active is not null) failures.Add(active.Failure(hr));
            // Report every discarded descriptor/content representation as part of the
            // failed group. An unpaired descriptor is never published later.
            foreach (SnapshotRequest request in requests.Where(item => IsVirtual(item.Name)))
                if (active is null || !SameKey(request, active))
                    failures.Add(request.Failure(hr));
        }
        finally { SnapshotEntry.DisposeAll(group); }
    }

    private static ulong?[] DescriptorLengths(byte[] bytes, bool unicode)
    {
        uint count = DescriptorCount(bytes, unicode);
        var lengths = new ulong?[checked((int)count)];
        int stride = unicode ? 592 : 332;
        for (int index = 0; index < lengths.Length; index++)
        {
            ReadOnlySpan<byte> descriptor = bytes.AsSpan(4 + index * stride, stride);
            if ((BinaryPrimitives.ReadUInt32LittleEndian(descriptor) & 0x40) == 0) continue; // FD_FILESIZE
            lengths[index] = ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(descriptor[64..]) << 32) |
                BinaryPrimitives.ReadUInt32LittleEndian(descriptor[68..]);
        }
        return lengths;
    }

    internal static uint DescriptorCount(byte[] bytes, bool unicode)
    {
        const int fileDescriptorA = 332;
        const int fileDescriptorW = 592;
        if (bytes.Length < 4) throw new ExternalException("Truncated file descriptor.", SnapshotNative.DV_E_FORMATETC);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        ulong required = 4UL + (ulong)count * (uint)(unicode ? fileDescriptorW : fileDescriptorA);
        if (count == 0 || count > 65536 || required > (ulong)bytes.Length)
            throw new ExternalException("Invalid file descriptor count.", SnapshotNative.DV_E_FORMATETC);
        return count;
    }
}
