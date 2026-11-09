using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SevenKeyboard;

namespace ClipRestorerVerification;

internal static unsafe class SnapshotTests
{
    internal static void Run()
    {
        Isolation.Require();
        Program.Check(ClipboardThread.IsCurrent, "snapshot suite runs on the isolated STA");
        Program.Check(sizeof(SnapshotFormat) == (IntPtr.Size == 8 ? 32 : 20), "FORMATETC native ABI layout");
        IndexedVirtualFiles();
        DescriptorByteLengths();
        DescriptorSizeFailures();
        BoundedMediumFallback();
        FatalMediumAllocationFailures();
        BrokenVirtualGroup();
        UnsupportedDoesNotClear();
        GuardAndPreparationFailures();
        AllUnavailableAndAllocationFailure();
        SourceReplacementRejectsMixedData();
        OwnershipAndShutdown();
    }

    private static void Hr(int hr)
    {
        if (hr < 0) throw new ExternalException("Snapshot fixture COM call failed.", hr);
    }

    private static uint Format(string name) => Native.RegisterClipboardFormatW(name);

    private static byte[] Descriptor(params string[] names)
    {
        byte[] bytes = new byte[4 + 592 * names.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)names.Length);
        for (int i = 0; i < names.Length; i++)
        {
            int offset = 4 + 592 * i;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), 0x80000000); // FD_UNICODE
            Encoding.Unicode.GetBytes(names[i] + "\0").CopyTo(bytes, offset + 72);
        }
        return bytes;
    }

    private static byte[] SizedDescriptor(bool unicode, params (string Name, ulong? Length)[] files)
    {
        int stride = unicode ? 592 : 332;
        byte[] bytes = new byte[4 + stride * files.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)files.Length);
        for (int index = 0; index < files.Length; index++)
        {
            int offset = 4 + stride * index;
            uint flags = unicode ? 0x80000000u : 0;
            if (files[index].Length is ulong length)
            {
                flags |= 0x40; // FD_FILESIZE, including an explicitly empty file.
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 64), (uint)(length >> 32));
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset + 68), unchecked((uint)length));
            }
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), flags);
            (unicode ? Encoding.Unicode : Encoding.ASCII).GetBytes(files[index].Name + "\0").CopyTo(bytes, offset + 72);
        }
        return bytes;
    }

    private static NativePayload StreamPayload(uint format, int index, byte[] bytes)
    {
        Hr(NativePayloadNative.CreateStreamOnHGlobal(0, 1, out nint stream));
        try
        {
            fixed (byte* data = bytes)
            {
                Hr(NativePayloadNative.StreamWrite(stream, data, (uint)bytes.Length, out uint written));
                Program.Check(written == bytes.Length, "fixture writes complete stream");
            }
            var medium = new NativeMedium { Tymed = 4, Handle = stream };
            return NativePayload.Capture(format, index, medium, "FileContents");
        }
        finally { NativePayloadNative.Release(stream); }
    }

    private static NativePayload StoragePayload(uint format, int index, byte[] bytes)
    {
        Hr(NativePayloadNative.CreateILockBytesOnHGlobal(0, 1, out nint lockBytes));
        nint storage = 0;
        try
        {
            Hr(NativePayloadNative.StgCreateDocfileOnILockBytes(lockBytes, 0x1012, 0, out storage));
            nint child = 0;
            fixed (char* name = "payload")
                Hr(((delegate* unmanaged[Stdcall]<nint, char*, uint, uint, uint, nint*, int>)
                    (*(nint**)storage)[3])(storage, name, 0x1012, 0, 0, &child));
            try
            {
                fixed (byte* data = bytes)
                {
                    Hr(NativePayloadNative.StreamWrite(child, data, (uint)bytes.Length, out uint written));
                    Program.Check(written == bytes.Length, "fixture writes complete storage child");
                }
            }
            finally { NativePayloadNative.Release(child); }
            Hr(NativePayloadNative.StorageCommit(storage));
            var medium = new NativeMedium { Tymed = 8, Handle = storage };
            return NativePayload.Capture(format, index, medium, "FileContents");
        }
        finally
        {
            if (storage != 0) NativePayloadNative.Release(storage);
            NativePayloadNative.Release(lockBytes);
        }
    }

    private static ClipboardSnapshot SeedSnapshot(SnapshotEntry[] entries) => new(entries, [], [], false, 0);

    private static byte[] ReadGlobal(in NativeMedium medium)
    {
        using var payload = NativePayload.Capture(Format("SevenKeyboard.Snapshot.Test.Bytes"), -1, medium);
        return payload.ReadBytes();
    }

    private static byte[] ReadStream(nint stream)
    {
        Hr(NativePayloadNative.StreamStat(stream, out NativePayloadStat stat));
        Program.Check(stat.Size <= 1024 * 1024, "fixture stream has bounded size");
        byte[] bytes = new byte[(int)stat.Size];
        Hr(NativePayloadNative.StreamSeek(stream, 0, 0, out _));
        fixed (byte* pointer = bytes)
        {
            Hr(NativePayloadNative.StreamRead(stream, pointer, (uint)bytes.Length, out uint read));
            Program.Check(read == bytes.Length, "provider returns complete stream bytes");
        }
        return bytes;
    }

    private static NativeMedium Get(nint source, uint format, int index, uint mask)
    {
        SnapshotFormat request = new() { Format = checked((ushort)format), Aspect = 1, Index = index, Tymed = mask };
        NativeMedium result = default;
        Hr(SnapshotNative.GetData(source, &request, &result));
        return result;
    }

    private static void IndexedVirtualFiles()
    {
        uint descriptor = Format("FileGroupDescriptorW"), contents = Format("FileContents");
        byte[] first = [1, 0, 3, 4, 5, 0, 7, 8];
        byte[] second = [9, 8, 0, 6, 5, 4, 3, 2, 1];
        byte[] third = [99, 98, 97, 0, 95];
        using var seed = SeedSnapshot([
            new(NativePayload.FromBytes(descriptor, Descriptor("memory.bin", "stream.bin", "storage.msg"),
                name: "FileGroupDescriptorW"), virtualFile: true),
            new(NativePayload.FromBytes(contents, first, 0, "FileContents"), virtualFile: true),
            new(StreamPayload(contents, 1, second), virtualFile: true),
            new(StoragePayload(contents, 2, third), virtualFile: true)
        ]);
        Program.Check(seed.Restore().IsComplete, "publishes three indexed virtual files");
        // Do not render any source data first: this exercises delayed OLE recapture.
        using ClipboardSnapshot captured = ClipboardSnapshot.Capture();
        Program.Check(captured.ReadFailures.Count == 0 && !captured.IsEmpty,
            "captures our still-delayed provider without misclassifying rendering as replacement");
        Native.Clear();
        seed.Dispose();
        Program.Check(captured.Restore().IsComplete, "restores detached indexed virtual files");
        captured.Dispose();
        Hr(SnapshotNative.OleGetClipboard(out nint source));
        try
        {
            using NativeMedium one = Get(source, contents, 0, 1);
            Program.Check(ReadGlobal(one).AsSpan().StartsWith(first), "index zero retains HGLOBAL content");
            using NativeMedium two = Get(source, contents, 1, 4);
            Program.Check(ReadStream(two.Handle).AsSpan().SequenceEqual(second), "index one retains exact stream content");
            using NativeMedium twoGlobal = Get(source, contents, 1, 1);
            Program.Check(ReadGlobal(twoGlobal).AsSpan().StartsWith(second), "stream contents serve HGLOBAL consumers");
            using NativeMedium oneStream = Get(source, contents, 0, 4);
            Program.Check(ReadStream(oneStream.Handle).AsSpan().StartsWith(first), "HGLOBAL contents serve stream consumers");
            using NativeMedium three = Get(source, contents, 2, 8);
            nint child = 0;
            fixed (char* name = "payload")
                Hr(((delegate* unmanaged[Stdcall]<nint, char*, nint, uint, uint, nint*, int>)
                    (*(nint**)three.Handle)[4])(three.Handle, name, 0, 0x10, 0, &child));
            try { Program.Check(ReadStream(child).AsSpan().SequenceEqual(third), "index two retains storage child bytes"); }
            finally { NativePayloadNative.Release(child); }

            // Returned media are independent, so a consumer cannot corrupt subsequent requests.
            byte* bytes = (byte*)NativePayloadNative.GlobalLock(one.Handle);
            bytes[0] = 77;
            NativePayloadNative.GlobalUnlock(one.Handle);
            using NativeMedium fresh = Get(source, contents, 0, 1);
            Program.Check(ReadGlobal(fresh)[0] == first[0], "consumer mutation does not alter published snapshot");

            SnapshotFormat missing = new() { Format = (ushort)contents, Aspect = 1, Index = 99, Tymed = 4 };
            NativeMedium absent = default;
            Program.Check(SnapshotNative.GetData(source, &missing, &absent) < 0 && absent.Handle == 0,
                "invalid virtual-file index returns HRESULT and no ownership");

            VerifyEnumeration(source, contents, exactIndices: false);
            nint rawProvider = SnapshotCom.CreateProvider([
                new(NativePayload.FromBytes(contents, [1], 0)),
                new(NativePayload.FromBytes(contents, [2], 1)),
                new(NativePayload.FromBytes(contents, [3], 2))]);
            try { VerifyEnumeration(rawProvider, contents, exactIndices: true); }
            finally { SnapshotNative.Release(rawProvider); }
        }
        finally { SnapshotNative.Release(source); Native.Clear(); SnapshotRuntime.RefreshOwnership(); }
    }

    private static void DescriptorByteLengths()
    {
        uint wide = Format("FileGroupDescriptorW"), ansi = Format("FileGroupDescriptor");
        uint contents = Format("FileContents");
        // Nonzero padding makes trimming zeros an insufficient implementation. File data
        // itself ends in two zeros, which must survive the authoritative nine-byte length.
        byte[] allocation = [1, 2, 0, 4, 5, 6, 7, 0, 0, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC];
        using var seed = SeedSnapshot([
            new(NativePayload.FromBytes(wide, SizedDescriptor(true, ("padded.bin", null), ("empty.bin", 0UL)),
                name: "FileGroupDescriptorW"), virtualFile: true),
            new(NativePayload.FromBytes(ansi, SizedDescriptor(false, ("padded.bin", 9UL), ("empty.bin", 0UL)),
                name: "FileGroupDescriptor"), virtualFile: true),
            new(NativePayload.FromBytes(contents, allocation, 0, "FileContents"), virtualFile: true),
            new(NativePayload.FromBytes(contents, [0xDD, 0xDD, 0xDD, 0xDD, 0xDD, 0xDD, 0xDD, 0xDD], 1, "FileContents"), virtualFile: true)
        ]);
        seed.Restore();
        using var captured = ClipboardSnapshot.Capture();
        Program.Check(captured.ReadFailures.Count == 0, "ANSI and Unicode descriptor sizes merge when one size is unspecified");
        Native.Clear();
        seed.Dispose();
        captured.Restore();
        captured.Dispose(); // Provider cloning must retain size metadata independently.
        Hr(SnapshotNative.OleGetClipboard(out nint source));
        try
        {
            using NativeMedium stream = Get(source, contents, 0, 4);
            Program.Check(ReadStream(stream.Handle).AsSpan().SequenceEqual(allocation.AsSpan(0, 9)),
                "FD_FILESIZE controls exact stream size and retains trailing file zeros");
            using NativeMedium global = Get(source, contents, 0, 1);
            Program.Check(ReadGlobal(global).AsSpan().StartsWith(allocation),
                "authoritative file length does not trim or rewrite HGLOBAL allocation bytes");
            using NativeMedium empty = Get(source, contents, 1, 4);
            Program.Check(ReadStream(empty.Handle).Length == 0,
                "FD_FILESIZE zero creates an empty stream despite nonempty allocation padding");
        }
        finally { SnapshotNative.Release(source); Native.Clear(); SnapshotRuntime.RefreshOwnership(); }
    }

    private static void DescriptorSizeFailures()
    {
        uint wide = Format("FileGroupDescriptorW"), ansi = Format("FileGroupDescriptor");
        uint contents = Format("FileContents"), standalone = Format("SevenKeyboard.Snapshot.BadSize.Independent");
        foreach (bool conflicting in new[] { false, true })
        {
            // The high DWORD must participate: 2^32 cannot fit this small allocation.
            ulong wideLength = conflicting ? 3UL : 1UL << 32;
            List<SnapshotEntry> entries = [
                new(NativePayload.FromBytes(wide, SizedDescriptor(true, ("bad.bin", wideLength)),
                    name: "FileGroupDescriptorW"), virtualFile: true),
                new(NativePayload.FromBytes(contents, [1, 2, 3, 4, 5, 6, 7, 8], 0, "FileContents"), virtualFile: true),
                new(NativePayload.FromBytes(standalone, [77]))
            ];
            if (conflicting)
                entries.Add(new(NativePayload.FromBytes(ansi, SizedDescriptor(false, ("bad.bin", 4UL)),
                    name: "FileGroupDescriptor"), virtualFile: true));
            using var seed = SeedSnapshot(entries.ToArray());
            seed.Restore();
            using var captured = ClipboardSnapshot.Capture();
            Program.Check(captured.ReadFailures.Count != 0,
                conflicting ? "conflicting descriptor sizes reject the virtual group" : "oversized descriptor length rejects all byte media");
            Native.Clear();
            ClipboardRestoreResult restored = captured.Restore();
            Program.Check(restored.RestoredFormatCount == 1 && restored.IsComplete,
                "invalid descriptor size discards the whole group but preserves independent data");
            Native.Clear();
        }
    }

    private static nint VirtualSource(uint descriptor, uint contents, uint standalone) =>
        SnapshotCom.CreateProvider([
            new(NativePayload.FromBytes(descriptor, SizedDescriptor(true, ("fallback.bin", 3UL)),
                name: "FileGroupDescriptorW"), virtualFile: true),
            new(NativePayload.FromBytes(contents, [7, 0, 0, 0xCC, 0xCC, 0xCC, 0xCC, 0xCC], 0, "FileContents"), virtualFile: true),
            new(NativePayload.FromBytes(standalone, [91]))
        ]);

    private static void BoundedMediumFallback()
    {
        uint descriptor = Format("FileGroupDescriptorW"), contents = Format("FileContents");
        uint standalone = Format("SevenKeyboard.Snapshot.Fallback.Independent");
        // Both paths return an IStream successfully, but detaching it fails during Read.
        // Recovery must continue after clone failures as well as failed GetData HRESULTs.
        foreach (bool recover in new[] { true, false })
        {
            List<uint> attempts = [];
            int released = 0;
            nint inner = VirtualSource(descriptor, contents, standalone);
            nint wrapper = ChangingSource.Create(inner, (nint source, SnapshotFormat* request, NativeMedium* output) =>
            {
                if (request->Format != contents) return SnapshotNative.GetData(source, request, output);
                attempts.Add(request->Tymed);
                *output = default;
                if (request->Tymed is 13 or 4)
                {
                    *output = new NativeMedium { Tymed = 4,
                        Handle = UnreadableStream.Create(SnapshotNative.E_FAIL, () => released++) };
                    return 0;
                }
                if (request->Tymed == 1 && recover) return SnapshotNative.GetData(source, request, output);
                return SnapshotNative.DV_E_TYMED;
            });
            try
            {
                Hr(SnapshotNative.OleSetClipboard(wrapper));
                using var captured = ClipboardSnapshot.Capture();
                Program.Check(attempts.SequenceEqual(new uint[] { 13, 8, 4, 1 }),
                    "combined medium first, then each individual medium at most once");
                Program.Check(released == 2, "failed deep copies release every successfully returned stream exactly once");
                Program.Check((captured.ReadFailures.Count == 0) == recover,
                    "virtual group is skipped only after every fallback fails");
                Native.Clear();
                ClipboardRestoreResult restored = captured.Restore();
                Program.Check(restored.RestoredFormatCount == (recover ? 3 : 1),
                    "recoverable group survives; exhausted group leaves independent data");
                if (recover)
                {
                    Hr(SnapshotNative.OleGetClipboard(out nint published));
                    try
                    {
                        using NativeMedium bytes = Get(published, contents, 0, 4);
                        Program.Check(ReadStream(bytes.Handle).AsSpan().SequenceEqual(new byte[] { 7, 0, 0 }),
                            "fallback HGLOBAL receives descriptor length before stream publication");
                    }
                    finally { SnapshotNative.Release(published); }
                }
            }
            finally { Native.Clear(); SnapshotNative.Release(wrapper); SnapshotNative.Release(inner); }
        }

        // A mask already containing one medium is not attempted twice.
        List<uint> singleAttempts = [];
        nint singleInner = VirtualSource(descriptor, contents, standalone);
        nint singleWrapper = ChangingSource.Create(singleInner, (nint source, SnapshotFormat* request, NativeMedium* output) =>
        {
            if (request->Format != descriptor) return SnapshotNative.GetData(source, request, output);
            singleAttempts.Add(request->Tymed);
            *output = default;
            return SnapshotNative.E_FAIL;
        });
        try
        {
            // Exercise our retry bound directly: the Windows OLE clipboard wrapper
            // can perform additional Win32 render attempts after a forwarded failure.
            Program.Throws<ExternalException>(() =>
            {
                using var ignored = SnapshotCapture.CaptureEntry(singleWrapper,
                    new SnapshotRequest(descriptor, "FileGroupDescriptorW", -1, 1, 1),
                    TimeSpan.Zero, virtualFile: true);
            }, "unavailable descriptor is rejected after its single permitted medium");
            Program.Check(singleAttempts.SequenceEqual(new uint[] { 1 }), "a single-medium mask has exactly one library attempt");
        }
        finally { Native.Clear(); SnapshotNative.Release(singleWrapper); SnapshotNative.Release(singleInner); }
    }

    private static void FatalMediumAllocationFailures()
    {
        uint descriptor = Format("FileGroupDescriptorW"), contents = Format("FileContents");
        uint standalone = Format("SevenKeyboard.Snapshot.OOM.Independent");
        int[] allocationErrors = [SnapshotNative.E_OUTOFMEMORY, unchecked((int)0x80070008), unchecked((int)0x80030008)];
        foreach (int error in allocationErrors)
        foreach (bool duringCopy in new[] { false, true })
        {
            int attempts = 0, released = 0;
            nint inner = VirtualSource(descriptor, contents, standalone);
            nint wrapper = ChangingSource.Create(inner, (nint source, SnapshotFormat* request, NativeMedium* output) =>
            {
                if (request->Format != contents) return SnapshotNative.GetData(source, request, output);
                attempts++;
                *output = default;
                if (!duringCopy) return error;
                *output = new NativeMedium { Tymed = 4, Handle = UnreadableStream.Create(error, () => released++) };
                return 0;
            });
            try
            {
                // Assert our boundary using the raw source. The Windows clipboard
                // wrapper may translate a failing source HRESULT before returning it.
                Program.Throws<OutOfMemoryException>(() =>
                {
                    using var ignored = SnapshotCapture.CaptureEntry(wrapper,
                        new SnapshotRequest(contents, "FileContents", 0, 1, 13),
                        TimeSpan.Zero, virtualFile: true);
                }, $"allocation HRESULT 0x{error:X8} is fatal during {(duringCopy ? "deep copy" : "GetData")}");
                Program.Check(attempts == 1, "allocation failure does not try another medium or retry capture");
                Program.Check(released == (duringCopy ? 1 : 0), "allocation failure releases transferred medium ownership");
            }
            finally { Native.Clear(); SnapshotNative.Release(wrapper); SnapshotNative.Release(inner); }
        }
    }

    private static void VerifyEnumeration(nint source, uint contents, bool exactIndices)
    {
        nint enumerator = 0;
        Hr(SnapshotNative.Enumerate(source, &enumerator));
        try
        {
            var indices = new HashSet<int>();
            for (;;)
            {
                SnapshotFormat format = default;
                uint fetched = 0;
                int hr = SnapshotNative.Next(enumerator, &format, &fetched);
                Hr(hr);
                try { if (fetched != 0 && format.Format == contents) indices.Add(format.Index); }
                finally { SnapshotNative.CoTaskMemFree(format.TargetDevice); }
                if (fetched == 0) break;
            }
            if (exactIndices)
                Program.Check(indices.SetEquals([0, 1, 2]), "raw provider enumerates every FileContents index");
            else
            {
                Program.Note("OLE wrapper enumerated FileContents indices: " + string.Join(",", indices));
                Program.Check(indices.Count != 0 && indices.All(index => index is >= -1 and <= 2),
                    "OLE wrapper advertises contents; indexed retrieval was separately verified");
            }
            Hr(((delegate* unmanaged[Stdcall]<nint, int>)(*(nint**)enumerator)[5])(enumerator));
            Hr(((delegate* unmanaged[Stdcall]<nint, uint, int>)(*(nint**)enumerator)[4])(enumerator, 1));
            nint cloned = 0;
            Hr(((delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)enumerator)[6])(enumerator, &cloned));
            try
            {
                SnapshotFormat left = default, right = default;
                uint leftCount = 0, rightCount = 0;
                Hr(SnapshotNative.Next(enumerator, &left, &leftCount));
                Hr(SnapshotNative.Next(cloned, &right, &rightCount));
                try
                {
                    Program.Check(leftCount == rightCount && left.Format == right.Format &&
                        left.Index == right.Index, "enumerator clone retains cursor independently");
                }
                finally
                {
                    SnapshotNative.CoTaskMemFree(left.TargetDevice);
                    SnapshotNative.CoTaskMemFree(right.TargetDevice);
                }
            }
            finally { SnapshotNative.Release(cloned); }
        }
        finally { SnapshotNative.Release(enumerator); }
    }

    private static void BrokenVirtualGroup()
    {
        uint descriptor = Format("FileGroupDescriptorW"), contents = Format("FileContents");
        uint standalone = Format("SevenKeyboard.Snapshot.Independent");
        using var seed = SeedSnapshot([
            new(NativePayload.FromBytes(descriptor, Descriptor("one.bin", "missing.bin"), name: "FileGroupDescriptorW"), virtualFile: true),
            new(NativePayload.FromBytes(contents, [1, 2, 3], 0, "FileContents"), virtualFile: true),
            new(NativePayload.FromBytes(standalone, [5, 6, 7]))
        ]);
        seed.Restore();
        using var captured = ClipboardSnapshot.Capture();
        Program.Check(captured.ReadFailures.Any(item => item.Format == contents && item.Index == 1),
            "missing indexed file is reported");
        Native.Clear();
        captured.Restore();
        Hr(SnapshotNative.OleGetClipboard(out nint source));
        try
        {
            using NativeMedium independent = Get(source, standalone, -1, 1);
            Program.Check(ReadGlobal(independent)[0] == 5, "independent format survives failed virtual group");
            SnapshotFormat request = new() { Format = (ushort)descriptor, Aspect = 1, Index = -1, Tymed = 1 };
            NativeMedium absent = default;
            Program.Check(SnapshotNative.GetData(source, &request, &absent) < 0,
                "failed virtual group never publishes an unpaired descriptor");
        }
        finally { SnapshotNative.Release(source); Native.Clear(); }
    }

    private static void UnsupportedDoesNotClear()
    {
        uint excluded = Format("Native");
        Native.WithClipboard(() => { Native.Check(Native.EmptyClipboard(), "seed exclusions"); Native.Put(excluded, [1, 2, 3]); });
        Program.Throws<ExternalException>(() => ClipboardSnapshot.Capture(),
            "all-excluded nonempty capture is a whole failure");
        // Defense in depth for an internal empty prepared collection: origin is nonempty.
        using var snapshot = SeedSnapshot([]);
        Native.WithClipboard(() => { Native.Check(Native.EmptyClipboard(), "replace exclusions"); Native.Put(13, Encoding.Unicode.GetBytes("keep me\0")); });
        uint sequence = SnapshotNative.GetClipboardSequenceNumber();
        Program.Throws<ExternalException>(() => snapshot.Restore(), "unsupported-only restore fails without clearing");
        Program.Check(sequence == SnapshotNative.GetClipboardSequenceNumber(), "unsupported-only failure leaves sequence unchanged");
        Native.Clear();
    }

    private static void GuardAndPreparationFailures()
    {
        uint format = Format("SevenKeyboard.Snapshot.Guarded");
        using var snapshot = SeedSnapshot([new(NativePayload.FromBytes(format, [1, 2, 3]))]);
        uint sequence = SnapshotNative.GetClipboardSequenceNumber();
        Program.Throws<OperationCanceledException>(() => snapshot.Restore(null, () => false),
            "false restore guard cancels before publication");
        Program.Check(sequence == SnapshotNative.GetClipboardSequenceNumber() && snapshot.LastMutationSequence is null,
            "guard cancellation records no mutation");
        int checks = 0;
        Program.Throws<OperationCanceledException>(() => snapshot.Restore(null, () => ++checks < 3),
            "restore rechecks guard immediately before destructive publication");
        Program.Check(sequence == SnapshotNative.GetClipboardSequenceNumber(), "late guard cancellation preserves clipboard");
        snapshot.Restore();
        Program.Check(snapshot.LastMutationSequence == SnapshotNative.GetClipboardSequenceNumber(),
            "successful own publication records mutation sequence");
        Native.Clear();

        NativePayload invalid = NativePayload.FromBytes(format, [1]);
        invalid.Dispose();
        using var broken = SeedSnapshot([new(invalid)]);
        sequence = SnapshotNative.GetClipboardSequenceNumber();
        Program.Throws<ExternalException>(() => broken.Restore(), "total preparation failure throws");
        Program.Check(sequence == SnapshotNative.GetClipboardSequenceNumber(), "total preparation failure is nondestructive");

        uint other = Format("SevenKeyboard.Snapshot.Prepared");
        using var partial = SeedSnapshot([new(invalid), new(NativePayload.FromBytes(other, [2]))]);
        ClipboardRestoreResult result = partial.Restore();
        Program.Check(!result.IsComplete && result.WriteFailures.Count == 1 && result.RestoredFormatCount == 1,
            "independent preparation failure returns partial success");
        Native.Clear();
    }

    private static void AllUnavailableAndAllocationFailure()
    {
        uint format = Format("SevenKeyboard.Snapshot.Unavailable");
        NativePayload unavailable = NativePayload.FromBytes(format, [1]);
        unavailable.Dispose();
        nint source = SnapshotCom.CreateProvider([new(unavailable)]);
        try
        {
            Hr(SnapshotNative.OleSetClipboard(source));
            Program.Throws<ExternalException>(() => ClipboardSnapshot.Capture(),
                "all-unavailable nonempty capture is a whole failure");
        }
        finally { Native.Clear(); SnapshotNative.Release(source); }

        nint inner = SnapshotCom.CreateProvider([new(NativePayload.FromBytes(format, [3]))]);
        nint wrapper = ChangingSource.Create(inner, () => throw new OutOfMemoryException("Fixture allocation failure."));
        try
        {
            Program.Throws<OutOfMemoryException>(() =>
            {
                using var ignored = SnapshotCapture.CaptureEntry(wrapper,
                    new SnapshotRequest(format, null, -1, 1, 1), TimeSpan.Zero);
            }, "reported GetData E_OUTOFMEMORY aborts instead of falling back or skipping");
        }
        finally { Native.Clear(); SnapshotNative.Release(wrapper); SnapshotNative.Release(inner); }
        Program.Throws<OutOfMemoryException>(() => SnapshotNative.Check(unchecked((int)0x80030008)),
            "STG_E_INSUFFICIENTMEMORY is also fatal");
    }

    private static void SourceReplacementRejectsMixedData()
    {
        uint format = Format("SevenKeyboard.Snapshot.ChangingSource");
        nint original = SnapshotCom.CreateProvider([new(NativePayload.FromBytes(format, [11, 22]))]);
        nint wrapper = ChangingSource.Create(original, () =>
        {
            Native.Clear();
            Native.WithClipboard(() => Native.Put(13, Encoding.Unicode.GetBytes("external replacement\0")));
        });
        try
        {
            Hr(SnapshotNative.OleSetClipboard(wrapper));
            Program.Throws<InvalidOperationException>(() => ClipboardSnapshot.Capture(),
                "source owner replacement during GetData rejects the whole mixed snapshot");
        }
        finally { Native.Clear(); SnapshotNative.Release(wrapper); SnapshotNative.Release(original); }
    }

    private static void OwnershipAndShutdown()
    {
        uint format = Format("SevenKeyboard.Snapshot.Shutdown");
        using var snapshot = SeedSnapshot([new(NativePayload.FromBytes(format, [77]))]);
        snapshot.Restore();
        Program.Check(SnapshotRuntime.IsCurrentPublication, "runtime tracks its own OLE publication");
        Native.Clear();
        Native.WithClipboard(() => Native.Put(13, Encoding.Unicode.GetBytes("foreign\0")));
        uint sequence = SnapshotNative.GetClipboardSequenceNumber();
        SnapshotRuntime.RefreshOwnership();
        Program.Check(!SnapshotRuntime.IsCurrentPublication, "external replacement releases runtime ownership");
        SnapshotRuntime.Shutdown();
        Program.Check(sequence == SnapshotNative.GetClipboardSequenceNumber(),
            "shutdown never flushes or clears an external publication");
        Native.Clear();
    }

    // A valid independently owned IStream whose Read deterministically fails. This
    // exercises a successful GetData followed by failed native detachment, without
    // relying on an invalid handle, a live application, or an exhausted process heap.
    private static class UnreadableStream
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Object
        {
            internal nint Table, Released;
            internal int Refs, ReadError;
            internal ulong Position;
        }

        private static readonly nint Vtable = Build();
        private static readonly Guid StreamId = new("0000000c-0000-0000-C000-000000000046");
        private static readonly Guid SequentialId = new("0c733a30-2a1c-11ce-ade5-00aa0044773d");

        internal static nint Create(int readError, Action released)
        {
            Object* value = (Object*)NativeMemory.AllocZeroed((nuint)sizeof(Object));
            if (value is null) throw new OutOfMemoryException();
            try
            {
                value->Released = GCHandle.ToIntPtr(GCHandle.Alloc(released));
                value->Table = Vtable; value->Refs = 1; value->ReadError = readError;
                return (nint)value;
            }
            catch { NativeMemory.Free(value); throw; }
        }

        private static nint Build()
        {
            nint* table = (nint*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(UnreadableStream), 14 * sizeof(nint));
            table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&Query;
            table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Add;
            table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
            table[3] = (nint)(delegate* unmanaged[Stdcall]<nint, void*, uint, uint*, int>)&Read;
            table[4] = (nint)(delegate* unmanaged[Stdcall]<nint, void*, uint, uint*, int>)&Write;
            table[5] = (nint)(delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)&Seek;
            table[6] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong, int>)&SetSize;
            table[7] = (nint)(delegate* unmanaged[Stdcall]<nint, nint, ulong, ulong*, ulong*, int>)&CopyTo;
            table[8] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, int>)&Commit;
            table[9] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&Revert;
            table[10] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong, ulong, uint, int>)&Region;
            table[11] = (nint)(delegate* unmanaged[Stdcall]<nint, ulong, ulong, uint, int>)&Region;
            table[12] = (nint)(delegate* unmanaged[Stdcall]<nint, NativePayloadStat*, uint, int>)&Stat;
            table[13] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&Clone;
            return (nint)table;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Query(nint self, Guid* iid, nint* output)
        {
            if (output is null) return SnapshotNative.E_POINTER;
            *output = 0;
            if (iid is null) return SnapshotNative.E_POINTER;
            if (*iid != SnapshotNative.IUnknownId && *iid != StreamId && *iid != SequentialId)
                return SnapshotNative.E_NOINTERFACE;
            Interlocked.Increment(ref ((Object*)self)->Refs); *output = self; return 0;
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint Add(nint self) => (uint)Interlocked.Increment(ref ((Object*)self)->Refs);
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint Release(nint self)
        {
            Object* value = (Object*)self;
            int count = Interlocked.Decrement(ref value->Refs);
            if (count == 0)
            {
                GCHandle callback = GCHandle.FromIntPtr(value->Released);
                try { ((Action)callback.Target!)(); }
                catch { /* No managed exception may cross the COM boundary. */ }
                finally { callback.Free(); NativeMemory.Free(value); }
            }
            return (uint)count;
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Read(nint self, void* bytes, uint count, uint* read)
        { if (read is not null) *read = 0; return ((Object*)self)->ReadError; }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Write(nint self, void* bytes, uint count, uint* written)
        { if (written is not null) *written = 0; return SnapshotNative.E_NOTIMPL; }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Seek(nint self, long offset, uint origin, ulong* position)
        {
            Object* value = (Object*)self;
            if (origin > 2) return SnapshotNative.E_INVALIDARG;
            long basis = origin == 0 ? 0 : origin == 1 ? (long)value->Position : 1;
            if (offset < -basis || offset > 1 - basis) return SnapshotNative.E_INVALIDARG;
            value->Position = (ulong)(basis + offset);
            if (position is not null) *position = value->Position;
            return 0;
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int SetSize(nint self, ulong size) => SnapshotNative.E_NOTIMPL;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int CopyTo(nint self, nint target, ulong count, ulong* read, ulong* written)
        { if (read is not null) *read = 0; if (written is not null) *written = 0; return SnapshotNative.E_NOTIMPL; }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Commit(nint self, uint flags) => SnapshotNative.E_NOTIMPL;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Revert(nint self) => SnapshotNative.E_NOTIMPL;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Region(nint self, ulong offset, ulong count, uint type) => SnapshotNative.E_NOTIMPL;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Stat(nint self, NativePayloadStat* stat, uint flags)
        {
            if (stat is null) return SnapshotNative.E_POINTER;
            *stat = default; stat->Type = 2; stat->Size = 1; return 0;
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Clone(nint self, nint* output)
        { if (output is not null) *output = 0; return SnapshotNative.E_NOTIMPL; }
    }

    // Test-only proxy intercepts source reads or changes the isolated clipboard once.
    // All other supported operations delegate to a real provider.
    private static class ChangingSource
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Object
        {
            internal nint Table, Inner, Callback;
            internal int Refs, Fired;
        }

        private static readonly nint Vtable = Build();

        internal delegate int GetDataHandler(nint inner, SnapshotFormat* format, NativeMedium* medium);

        internal static nint Create(nint inner, Action callback) => CreateCore(inner, callback);
        internal static nint Create(nint inner, GetDataHandler callback) => CreateCore(inner, callback);

        private static nint CreateCore(nint inner, object callback)
        {
            Object* value = (Object*)NativeMemory.AllocZeroed((nuint)sizeof(Object));
            if (value is null) throw new OutOfMemoryException();
            try
            {
                value->Callback = GCHandle.ToIntPtr(GCHandle.Alloc(callback));
                value->Table = Vtable; value->Inner = inner; value->Refs = 1;
                SnapshotNative.AddRef(inner);
                return (nint)value;
            }
            catch { NativeMemory.Free(value); throw; }
        }

        private static nint Build()
        {
            nint* table = (nint*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(ChangingSource), 12 * sizeof(nint));
            table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&Query;
            table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Add;
            table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&Release;
            table[3] = (nint)(delegate* unmanaged[Stdcall]<nint, SnapshotFormat*, NativeMedium*, int>)&Get;
            table[4] = (nint)(delegate* unmanaged[Stdcall]<nint, SnapshotFormat*, NativeMedium*, int>)&Here;
            table[5] = (nint)(delegate* unmanaged[Stdcall]<nint, SnapshotFormat*, int>)&Available;
            table[6] = (nint)(delegate* unmanaged[Stdcall]<nint, SnapshotFormat*, SnapshotFormat*, int>)&Canonical;
            table[7] = (nint)(delegate* unmanaged[Stdcall]<nint, SnapshotFormat*, NativeMedium*, int, int>)&Set;
            table[8] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)&Enumerate;
            table[9] = (nint)(delegate* unmanaged[Stdcall]<nint, SnapshotFormat*, uint, nint, uint*, int>)&Advise;
            table[10] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, int>)&Unadvise;
            table[11] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&EnumAdvise;
            return (nint)table;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Query(nint self, Guid* iid, nint* output)
        {
            if (output is null) return SnapshotNative.E_POINTER;
            *output = 0;
            if (iid is null) return SnapshotNative.E_POINTER;
            if (*iid != SnapshotNative.IUnknownId && *iid != SnapshotNative.IDataObjectId) return SnapshotNative.E_NOINTERFACE;
            Interlocked.Increment(ref ((Object*)self)->Refs); *output = self; return 0;
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint Add(nint self) => (uint)Interlocked.Increment(ref ((Object*)self)->Refs);
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint Release(nint self)
        {
            Object* value = (Object*)self;
            int count = Interlocked.Decrement(ref value->Refs);
            if (count == 0)
            {
                SnapshotNative.Release(value->Inner);
                GCHandle.FromIntPtr(value->Callback).Free(); NativeMemory.Free(value);
            }
            return (uint)count;
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Get(nint self, SnapshotFormat* format, NativeMedium* medium)
        {
            try
            {
                Object* value = (Object*)self;
                object callback = GCHandle.FromIntPtr(value->Callback).Target!;
                if (callback is GetDataHandler handler) return handler(value->Inner, format, medium);
                if (Interlocked.Exchange(ref value->Fired, 1) == 0) ((Action)callback)();
                return SnapshotNative.GetData(value->Inner, format, medium);
            }
            catch (Exception e) { return e.HResult; }
        }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Here(nint self, SnapshotFormat* format, NativeMedium* medium) => SnapshotNative.E_NOTIMPL;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Available(nint self, SnapshotFormat* format) =>
            ((delegate* unmanaged[Stdcall]<nint, SnapshotFormat*, int>)(*(nint**)((Object*)self)->Inner)[5])(((Object*)self)->Inner, format);
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Canonical(nint self, SnapshotFormat* input, SnapshotFormat* output)
        { if (output is not null) *output = default; return SnapshotNative.E_NOTIMPL; }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Set(nint self, SnapshotFormat* format, NativeMedium* medium, int release) => SnapshotNative.E_NOTIMPL;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Enumerate(nint self, uint direction, nint* output) =>
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)((Object*)self)->Inner)[8])(((Object*)self)->Inner, direction, output);
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Advise(nint self, SnapshotFormat* format, uint flags, nint sink, uint* connection)
        { if (connection is not null) *connection = 0; return SnapshotNative.OLE_E_ADVISENOTSUPPORTED; }
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int Unadvise(nint self, uint connection) => SnapshotNative.OLE_E_ADVISENOTSUPPORTED;
        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int EnumAdvise(nint self, nint* output)
        { if (output is not null) *output = 0; return SnapshotNative.OLE_E_ADVISENOTSUPPORTED; }
    }
}
