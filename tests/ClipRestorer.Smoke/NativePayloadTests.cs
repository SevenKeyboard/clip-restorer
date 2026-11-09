using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SevenKeyboard;

namespace ClipRestorerVerification;

internal static unsafe partial class NativePayloadTests
{
    // Native objects only: this suite never opens or modifies a clipboard.
    internal static void Run()
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            RunOnSta();
            return;
        }
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { RunOnSta(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void RunOnSta()
    {
        int hr = OleInitialize(0);
        if (hr < 0) throw new COMException("OleInitialize", hr);
        try
        {
            GlobalAndMarker();
            BitmapAndPalette();
            Metafiles();
            StreamAndStorage();
            StreamFailures();
            MediumConversions();
            VirtualFileLengths();
            Console.WriteLine("NativePayload: independent memory/GDI/metafile/stream/storage and failure checks passed.");
        }
        finally { OleUninitialize(); }
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Hr(int hr)
    {
        if (hr < 0) throw new COMException("Native test API failed.", hr);
    }

    private static void MustFail(Action operation)
    {
        try { operation(); }
        catch (COMException) { return; }
        throw new InvalidOperationException("Expected a native payload failure.");
    }

    private static void GlobalAndMarker()
    {
        Assert(Marshal.SizeOf<NativeMedium>() == (IntPtr.Size == 8 ? 24 : 12), "STGMEDIUM ABI size.");
        Assert(Marshal.OffsetOf<NativeMedium>(nameof(NativeMedium.Handle)).ToInt32() == IntPtr.Size, "STGMEDIUM union alignment.");
        Assert(Marshal.SizeOf<NativePayloadStat>() == (IntPtr.Size == 8 ? 80 : 72), "STATSTG ABI size.");

        foreach (uint format in new uint[] { 0xc001, 0x207, 0x303 })
        {
            using var original = NativePayload.FromBytes(format, [1, 2, 3]);
            byte[] expectedAllocation = original.ReadBytes();
            using var supplied = original.CreateMedium();
            using var saved = NativePayload.Capture(format, 7, supplied, "sample");
            nint memory = NativePayloadNative.GlobalLock(supplied.Handle);
            Assert(memory != 0, "Lock test HGLOBAL.");
            ((byte*)memory)[0] = 99;
            NativePayloadNative.GlobalUnlock(supplied.Handle);
            Assert(saved.ReadBytes().AsSpan().SequenceEqual(expectedAllocation), "HGLOBAL must own independent bytes, including allocation padding.");
            Assert(saved.Index == 7 && saved.Name == "sample", "Payload identity preserved.");
            using var second = saved.Clone();
            saved.Dispose();
            Assert(second.ReadBytes()[0] == 1, "Clone remains valid after original disposal.");
        }

        using var marker = NativePayload.FromMarker(0xc002);
        using var empty = marker.CreateMedium();
        Assert(empty.Handle != 0 && NativePayloadNative.GlobalSize(empty.Handle) >= 1, "Empty marker publishes an allocated byte.");
        using var recaptured = NativePayload.Capture(0xc002, -1, empty);
        Assert(recaptured.ReadBytes()[0] == 0, "Published marker is zero initialized.");
        using var emptyClone = marker.Clone();
        Assert(marker.ReadBytes().Length == 0 && emptyClone.ReadBytes().Length == 0, "Payload clones retain logical emptiness.");
        var borrowedEmpty = new NativeMedium
        {
            Tymed = NativePayload.HGlobal,
            Handle = NativePayloadNative.GlobalAlloc(0x42, 0)
        };
        Assert(borrowedEmpty.Handle != 0, "Allocate a valid discarded marker handle.");
        try
        {
            using var capturedEmpty = NativePayload.Capture(0xc002, -1, borrowedEmpty);
            Assert(capturedEmpty.ReadBytes().Length == 0, "Capture preserves genuine zero-length HGLOBAL.");
            using var renderedEmpty = capturedEmpty.CreateMedium();
            Assert(NativePayloadNative.GlobalSize(renderedEmpty.Handle) >= 1, "Captured zero-length HGLOBAL is materialized on publication.");
        }
        finally { borrowedEmpty.Dispose(); }
        MustFail(() => NativePayload.FromClipboard(0xc002, 0));
        MustFail(() => NativePayload.Capture(0xc002, -1, new NativeMedium { Tymed = 2, Handle = 1 }));
    }

    private static void BitmapAndPalette()
    {
        byte[] pixels = [0x80, 0, 0x40, 0];
        fixed (byte* bits = pixels)
        {
            nint bitmap = CreateBitmap(8, 2, 1, 1, bits);
            Assert(bitmap != 0, "CreateBitmap.");
            try
            {
                using var saved = NativePayload.FromClipboard(2, bitmap);
                using var copy = saved.CreateMedium();
                Assert(copy.Handle != bitmap && NativePayloadNative.GetObjectType(copy.Handle) == 7, "Independent bitmap.");
                Assert(BitmapPixels(copy.Handle, 8, 2).AsSpan().SequenceEqual(BitmapPixels(bitmap, 8, 2)),
                    "Bitmap pixels preserved despite DDB/DIB scanline padding differences.");
                NativePayloadNative.DeleteObject(bitmap);
                bitmap = 0;
                using var again = saved.CreateMedium();
                Assert(NativePayloadNative.GetObjectType(again.Handle) == 7, "Bitmap survives source deletion.");
            }
            finally { if (bitmap != 0) NativePayloadNative.DeleteObject(bitmap); }
        }

        byte[] entries = [0, 3, 2, 0, 10, 20, 30, 0, 40, 50, 60, 0];
        fixed (byte* data = entries)
        {
            nint palette = NativePayloadNative.CreatePalette(data);
            Assert(palette != 0, "CreatePalette.");
            try
            {
                using var saved = NativePayload.FromClipboard(9, palette);
                NativePayloadNative.DeleteObject(palette);
                palette = 0;
                using var copy = saved.CreateMedium();
                byte* actual = stackalloc byte[8];
                Assert(NativePayloadNative.GetPaletteEntries(copy.Handle, 0, 2, actual) == 2, "Read cloned palette.");
                Assert(new ReadOnlySpan<byte>(actual, 8).SequenceEqual(entries.AsSpan(4)), "Palette entries preserved.");
            }
            finally { if (palette != 0) NativePayloadNative.DeleteObject(palette); }
        }
    }

    private static uint[] BitmapPixels(nint bitmap, int width, int height)
    {
        nint dc = CreateCompatibleDC(0);
        Assert(dc != 0, "Create bitmap comparison DC.");
        nint previous = SelectObject(dc, bitmap);
        try
        {
            Assert(previous != 0 && previous != -1, "Select bitmap for pixel comparison.");
            uint[] pixels = new uint[width * height];
            for (int y = 0; y < height; ++y)
                for (int x = 0; x < width; ++x)
                {
                    uint pixel = GetPixel(dc, x, y);
                    Assert(pixel != uint.MaxValue, "Read bitmap pixel.");
                    pixels[y * width + x] = pixel;
                }
            return pixels;
        }
        finally
        {
            if (previous != 0 && previous != -1) SelectObject(dc, previous);
            DeleteDC(dc);
        }
    }
    private static void Metafiles()
    {
        nint dc = CreateMetaFile(0);
        Assert(dc != 0, "CreateMetaFile.");
        Assert(Rectangle(dc, 0, 0, 17, 23) != 0, "Draw metafile.");
        nint metafile = CloseMetaFile(dc);
        Assert(metafile != 0, "CloseMetaFile.");
        nint wrapper = NativePayloadNative.GlobalAlloc(0x42, (nuint)sizeof(NativePayloadMetafilePicture));
        Assert(wrapper != 0, "Allocate metafile wrapper.");
        nint memory = NativePayloadNative.GlobalLock(wrapper);
        *(NativePayloadMetafilePicture*)memory = new() { MappingMode = 8, XExtent = 17, YExtent = 23, Metafile = metafile };
        NativePayloadNative.GlobalUnlock(wrapper);

        var original = new NativeMedium { Tymed = NativePayload.MfPict, Handle = wrapper };
        try
        {
            using var saved = NativePayload.Capture(3, -1, original);
            using var copy = saved.CreateMedium();
            nint copyMemory = NativePayloadNative.GlobalLock(copy.Handle);
            NativePayloadMetafilePicture picture = *(NativePayloadMetafilePicture*)copyMemory;
            NativePayloadNative.GlobalUnlock(copy.Handle);
            Assert(copy.Handle != wrapper && picture.Metafile != metafile, "Both metafile wrapper and child must be independent.");
            Assert(picture.MappingMode == 8 && picture.XExtent == 17 && picture.YExtent == 23, "METAFILEPICT metadata.");
            original.Dispose();
            Assert(GetMetaFileBitsEx(picture.Metafile, 0, null) != 0, "Copied metafile survives source disposal.");
            using var again = saved.Clone();
        }
        finally { original.Dispose(); }

        dc = CreateEnhMetaFile(0, 0, 0, 0);
        Assert(dc != 0, "CreateEnhMetaFile.");
        Assert(Rectangle(dc, 0, 0, 13, 19) != 0, "Draw enhanced metafile.");
        var enhanced = new NativeMedium { Tymed = NativePayload.EnhMf, Handle = CloseEnhMetaFile(dc) };
        Assert(enhanced.Handle != 0, "CloseEnhMetaFile.");
        try
        {
            using var saved = NativePayload.Capture(14, -1, enhanced);
            using var copy = saved.CreateMedium();
            Assert(copy.Handle != enhanced.Handle, "Independent enhanced metafile.");
            enhanced.Dispose();
            Assert(GetEnhMetaFileBits(copy.Handle, 0, null) != 0, "Enhanced metafile survives source disposal.");
        }
        finally { enhanced.Dispose(); }
    }

    private static void StreamAndStorage()
    {
        Hr(NativePayloadNative.CreateStreamOnHGlobal(0, 1, out nint stream));
        var original = new NativeMedium { Tymed = NativePayload.IStream, Handle = stream };
        try
        {
            byte[] bytes = Enumerable.Range(0, 150_000).Select(i => (byte)(i % 251)).ToArray();
            Write(stream, bytes);
            Hr(NativePayloadNative.StreamSeek(stream, 31, 0, out _));
            using var saved = NativePayload.Capture(0xc003, 3, original);
            Hr(NativePayloadNative.StreamSeek(stream, 0, 1, out ulong position));
            Assert(position == 31, "Source stream cursor restored.");
            Hr(NativePayloadNative.StreamSeek(stream, 0, 0, out _));
            Write(stream, [99, 98, 97]);
            original.Dispose();
            using var copy = saved.CreateMedium();
            Assert(Read(copy.Handle).AsSpan().SequenceEqual(bytes), "Entire stream copied independently, including multi-chunk content.");
            saved.Dispose();
            Assert(Read(copy.Handle).AsSpan().SequenceEqual(bytes), "Returned medium outlives payload.");
        }
        finally { original.Dispose(); }

        Hr(NativePayloadNative.CreateILockBytesOnHGlobal(0, 1, out nint lockBytes));
        nint storage = 0;
        try
        {
            Hr(NativePayloadNative.StgCreateDocfileOnILockBytes(lockBytes, 0x1012, 0, out storage));
            Guid classId = new("7e3eaa1a-89b5-4f55-9f50-8f6862f0e507");
            Hr(NativePayloadNative.StorageSetClass(storage, classId));
            Hr(NativePayloadNative.StorageSetStateBits(storage, 0x10203040));
            nint child = CreateStorageStream(storage, "contents");
            Write(child, [7, 8, 9, 10]);
            NativePayloadNative.Release(child);
            var borrowed = new NativeMedium { Tymed = NativePayload.IStorage, Handle = storage };
            using var saved = NativePayload.Capture(0xc004, -1, borrowed);
            NativePayloadNative.Release(storage);
            storage = 0;
            using var copy = saved.CreateMedium();
            Hr(NativePayloadNative.StorageStat(copy.Handle, out var stat));
            Assert(stat.ClassId == classId && stat.StateBits == 0x10203040, "Storage class/state preserved.");
            child = OpenStorageStream(copy.Handle, "contents");
            try { Assert(Read(child).AsSpan().SequenceEqual(new byte[] { 7, 8, 9, 10 }), "Storage child content preserved after source release."); }
            finally { NativePayloadNative.Release(child); }
        }
        finally
        {
            if (storage != 0) NativePayloadNative.Release(storage);
            NativePayloadNative.Release(lockBytes);
        }
    }


    private static void MediumConversions()
    {
        byte[] expected = [12, 34, 56, 78, 90];
        using var bytes = NativePayload.FromBytes(0xc006, expected);
        byte[] expectedAllocation = bytes.ReadBytes();
        Assert(bytes.SupportedMedia == (NativePayload.HGlobal | NativePayload.IStream), "Byte payload advertises only bytes and stream.");
        using var exact = bytes.CreateMedium(NativePayload.HGlobal | NativePayload.IStream);
        Assert(exact.Tymed == NativePayload.HGlobal, "Captured medium wins over alternatives.");
        MustFail(() => bytes.CreateMedium(NativePayload.IStorage));
        using var stream = bytes.CreateMedium(NativePayload.IStream);
        Assert(Read(stream.Handle).AsSpan().SequenceEqual(expectedAllocation), "Generic HGLOBAL to stream preserves the full allocation.");
        using var savedStream = NativePayload.Capture(0xc006, 0, stream);
        using var preferStream = savedStream.CreateMedium(NativePayload.HGlobal | NativePayload.IStream);
        Assert(preferStream.Tymed == NativePayload.IStream, "Captured stream wins over HGLOBAL.");
        using var convertedBytes = savedStream.CreateMedium(NativePayload.HGlobal);
        savedStream.Dispose();
        using var capturedBytes = NativePayload.Capture(0xc006, 0, convertedBytes);
        Assert(capturedBytes.ReadBytes().AsSpan().SequenceEqual(expectedAllocation), "Stream to HGLOBAL preserves independent content.");
        Hr(NativePayloadNative.StreamSeek(stream.Handle, 0, 0, out _));
        Write(stream.Handle, [99]);
        Assert(capturedBytes.ReadBytes().AsSpan().SequenceEqual(expectedAllocation), "Later source mutations cannot affect converted bytes.");

        using var marker = NativePayload.FromMarker(0xc007);
        using var emptyStream = marker.CreateMedium(NativePayload.IStream);
        Assert(Read(emptyStream.Handle).Length == 0, "Logical empty marker converts to a truly empty stream.");
        using var savedEmptyStream = NativePayload.Capture(0xc007, -1, emptyStream);
        using var publishedMarker = savedEmptyStream.CreateMedium(NativePayload.HGlobal);
        Assert(NativePayloadNative.GlobalSize(publishedMarker.Handle) >= 1, "Empty stream publishes a non-discarded HGLOBAL marker.");

        Hr(NativePayloadNative.CreateILockBytesOnHGlobal(0, 1, out nint lockBytes));
        nint source = 0;
        try
        {
            Hr(NativePayloadNative.StgCreateDocfileOnILockBytes(lockBytes, 0x1012, 0, out source));
            Guid classId = new("3e369b28-aeff-40b0-a88c-f9a50249e50e");
            Hr(NativePayloadNative.StorageSetClass(source, classId));
            Hr(NativePayloadNative.StorageSetStateBits(source, 0x12345678));
            nint child = CreateStorageStream(source, "sample");
            try { Write(child, expected); }
            finally { NativePayloadNative.Release(child); }

            using var saved = NativePayload.Capture(0xc008, -1, new NativeMedium { Tymed = NativePayload.IStorage, Handle = source });
            Assert(saved.SupportedMedia == (NativePayload.IStorage | NativePayload.HGlobal | NativePayload.IStream), "Storage conversion mask.");
            MustFail(() => saved.SetLogicalByteLength(0));
            using var exactStorage = saved.CreateMedium(saved.SupportedMedia);
            Assert(exactStorage.Tymed == NativePayload.IStorage, "Storage prefers its original medium.");
            using var serialized = saved.CreateMedium(NativePayload.HGlobal);
            using var serializedStream = saved.CreateMedium(NativePayload.IStream);
            saved.Dispose();
            NativePayloadNative.Release(source);
            source = 0;
            VerifySerializedStorage(serialized.Handle, classId, expected);
            Hr(NativePayloadNative.GetHGlobalFromStream(serializedStream.Handle, out nint streamBacking));
            VerifySerializedStorage(streamBacking, classId, expected);
        }
        finally
        {
            if (source != 0) NativePayloadNative.Release(source);
            NativePayloadNative.Release(lockBytes);
        }
    }

    private static void VirtualFileLengths()
    {
        // Deliberate nonzero padding makes this independent of allocator rounding.
        // Trailing zeros within the declared file are real content, not padding.
        byte[] fileBytes = [0x11, 0, 0x22, 0, 0];
        byte[] padded = Enumerable.Repeat((byte)0xa5, 32).ToArray();
        fileBytes.CopyTo(padded, 0);
        using var payload = NativePayload.FromBytes(0xc009, padded, 2, "FileContents");
        byte[] allocation = payload.ReadBytes();
        using (var generic = payload.CreateMedium(NativePayload.IStream))
            Assert(Read(generic.Handle).AsSpan().SequenceEqual(allocation), "Unspecified length preserves every generic allocation byte.");

        payload.SetLogicalByteLength((ulong)allocation.Length);
        using (var full = payload.CreateMedium(NativePayload.IStream))
            Assert(Read(full.Handle).AsSpan().SequenceEqual(allocation), "An authoritative length equal to the allocation is valid.");
        payload.SetLogicalByteLength((ulong)fileBytes.Length);
        MustFail(() => payload.SetLogicalByteLength((ulong)allocation.Length + 1));
        MustFail(() => payload.SetLogicalByteLength(ulong.MaxValue));

        using var clone = payload.Clone();
        payload.Dispose();
        using var preferred = clone.CreateMedium(NativePayload.HGlobal | NativePayload.IStream);
        Assert(preferred.Tymed == NativePayload.HGlobal, "Virtual-file metadata preserves captured-medium preference.");
        using var capturedGlobal = NativePayload.Capture(0xc009, 2, preferred);
        Assert(capturedGlobal.ReadBytes().AsSpan().SequenceEqual(allocation), "Logical file length does not truncate HGLOBAL publication.");
        using var stream = clone.CreateMedium(NativePayload.IStream);
        Assert(Read(stream.Handle).AsSpan().SequenceEqual(fileBytes), "Cloned virtual-file stream uses FD_FILESIZE and retains trailing zero content.");
        using var savedStream = NativePayload.Capture(0xc009, 2, stream);
        savedStream.SetLogicalByteLength((ulong)fileBytes.Length);
        MustFail(() => savedStream.SetLogicalByteLength((ulong)fileBytes.Length - 1));
        MustFail(() => savedStream.SetLogicalByteLength((ulong)fileBytes.Length + 1));
        using var unchangedStream = savedStream.CreateMedium();
        Assert(Read(unchangedStream.Handle).AsSpan().SequenceEqual(fileBytes), "Descriptor validation never truncates or extends an existing stream.");
        Hr(NativePayloadNative.StreamSeek(stream.Handle, 0, 0, out _));
        Write(stream.Handle, [99]);
        using var secondStream = clone.CreateMedium(NativePayload.IStream);
        Assert(Read(secondStream.Handle).AsSpan().SequenceEqual(fileBytes), "Each length-limited stream owns independent content.");
        Assert(clone.ReadBytes().AsSpan().SequenceEqual(allocation), "Stream resizing and writes leave the full owned HGLOBAL intact.");

        using var emptyFile = NativePayload.FromBytes(0xc009, [0xff, 0xaa, 0], 3, "FileContents");
        byte[] emptyAllocation = emptyFile.ReadBytes();
        emptyFile.SetLogicalByteLength(0);
        using var emptyClone = emptyFile.Clone();
        emptyFile.Dispose();
        using var emptyStream = emptyClone.CreateMedium(NativePayload.IStream);
        Assert(Read(emptyStream.Handle).Length == 0, "A zero FD_FILESIZE produces an empty stream despite nonzero allocation bytes.");
        using var savedEmptyStream = NativePayload.Capture(0xc009, 3, emptyStream);
        savedEmptyStream.SetLogicalByteLength(0);
        MustFail(() => savedEmptyStream.SetLogicalByteLength(1));
        using var emptyGlobal = emptyClone.CreateMedium();
        using var capturedEmptyGlobal = NativePayload.Capture(0xc009, 3, emptyGlobal);
        Assert(capturedEmptyGlobal.ReadBytes().AsSpan().SequenceEqual(emptyAllocation), "Zero logical file length preserves HGLOBAL publication bytes.");
    }

    private static void VerifySerializedStorage(nint global, Guid classId, byte[] expected)
    {
        Hr(NativePayloadNative.CreateILockBytesOnHGlobal(global, 0, out nint lockBytes));
        nint storage = 0;
        try
        {
            Hr(StgIsStorageILockBytes(lockBytes));
            Hr(StgOpenStorageOnILockBytes(lockBytes, 0, 0x10, 0, 0, out storage));
            Hr(NativePayloadNative.StorageStat(storage, out var stat));
            Assert(stat.ClassId == classId && stat.StateBits == 0x12345678, "Serialized compound storage preserves root metadata.");
            nint child = OpenStorageStream(storage, "sample");
            try { Assert(Read(child).AsSpan().SequenceEqual(expected), "Serialized compound storage preserves child content."); }
            finally { NativePayloadNative.Release(child); }
        }
        finally
        {
            if (storage != 0) NativePayloadNative.Release(storage);
            NativePayloadNative.Release(lockBytes);
        }
    }

    private static void Write(nint stream, byte[] bytes)
    {
        fixed (byte* data = bytes)
        {
            Hr(NativePayloadNative.StreamWrite(stream, data, (uint)bytes.Length, out uint written));
            Assert(written == bytes.Length, "Write test data.");
        }
    }

    private static byte[] Read(nint stream)
    {
        Hr(NativePayloadNative.StreamStat(stream, out var stat));
        byte[] bytes = new byte[checked((int)stat.Size)];
        Hr(NativePayloadNative.StreamSeek(stream, 0, 0, out _));
        if (bytes.Length == 0)
            return bytes;
        fixed (byte* data = bytes)
        {
            Hr(NativePayloadNative.StreamRead(stream, data, (uint)bytes.Length, out uint read));
            Assert(read == bytes.Length, "Read entire cloned stream.");
        }
        return bytes;
    }

    private static nint CreateStorageStream(nint storage, string name)
    {
        nint stream = 0;
        fixed (char* text = name)
            Hr(((delegate* unmanaged[Stdcall]<nint, char*, uint, uint, uint, nint*, int>)
                (*(nint**)storage)[3])(storage, text, 0x1012, 0, 0, &stream));
        return stream;
    }

    private static nint OpenStorageStream(nint storage, string name)
    {
        nint stream = 0;
        fixed (char* text = name)
            Hr(((delegate* unmanaged[Stdcall]<nint, char*, nint, uint, uint, nint*, int>)
                (*(nint**)storage)[4])(storage, text, 0, 0x10, 0, &stream));
        return stream;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdversarialStream
    {
        public nint Vtable;
        public int Position;
        public int DeclaredLength;
        public int ActualLength;
        public int Seekable;
        public int FailRead;
    }

    private static void StreamFailures()
    {
        nint* table = stackalloc nint[14];
        new Span<nint>(table, 14).Clear();
        table[3] = (nint)(delegate* unmanaged[Stdcall]<nint, void*, uint, uint*, int>)&FakeRead;
        table[5] = (nint)(delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)&FakeSeek;
        table[12] = (nint)(delegate* unmanaged[Stdcall]<nint, NativePayloadStat*, uint, int>)&FakeStat;
        AdversarialStream source = new() { Vtable = (nint)table, Position = 1, DeclaredLength = 5, ActualLength = 2, Seekable = 1 };
        nint pointer = (nint)(&source);
        MustFail(() => NativePayload.Capture(0xc005, -1, new NativeMedium { Tymed = NativePayload.IStream, Handle = pointer }));
        Assert(source.Position == 1, "Cursor restored after truncated source.");
        source.FailRead = 1;
        MustFail(() => NativePayload.Capture(0xc005, -1, new NativeMedium { Tymed = NativePayload.IStream, Handle = pointer }));
        Assert(source.Position == 1, "Cursor restored after read HRESULT failure.");
        source.FailRead = 2;
        bool sawOutOfMemory = false;
        try { using var unexpected = NativePayload.Capture(0xc005, -1, new NativeMedium { Tymed = NativePayload.IStream, Handle = pointer }); }
        catch (OutOfMemoryException) { sawOutOfMemory = true; }
        Assert(sawOutOfMemory && source.Position == 1, "Native OOM propagates globally and restores the source cursor.");
        source.FailRead = 0;
        source.ActualLength = 5;
        source.Seekable = 0;
        MustFail(() => NativePayload.Capture(0xc005, -1, new NativeMedium { Tymed = NativePayload.IStream, Handle = pointer }));
        source.Position = 0;
        using var full = NativePayload.Capture(0xc005, -1, new NativeMedium { Tymed = NativePayload.IStream, Handle = pointer });
        using var fullMedium = full.CreateMedium();
        Assert(Read(fullMedium.Handle).Length == 5, "Nonseekable full source can be copied when length is verifiable.");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FakeRead(nint self, void* buffer, uint count, uint* read)
    {
        var source = (AdversarialStream*)self;
        *read = 0;
        if (source->FailRead == 2) return unchecked((int)0x8007000E);
        if (source->FailRead != 0) return unchecked((int)0x8003001E);
        uint available = (uint)Math.Max(0, source->ActualLength - source->Position);
        uint actual = Math.Min(count, available);
        for (uint i = 0; i < actual; ++i) ((byte*)buffer)[i] = (byte)(source->Position + i);
        source->Position += (int)actual;
        *read = actual;
        return actual == count ? 0 : 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FakeSeek(nint self, long offset, uint origin, ulong* result)
    {
        var source = (AdversarialStream*)self;
        if (source->Seekable == 0) return unchecked((int)0x80030001);
        long position = offset + (origin == 1 ? source->Position : origin == 2 ? source->ActualLength : 0);
        if (position < 0 || position > int.MaxValue) return unchecked((int)0x80030001);
        source->Position = (int)position;
        *result = (ulong)position;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FakeStat(nint self, NativePayloadStat* stat, uint flags)
    {
        *stat = default;
        stat->Type = 2;
        stat->Size = (ulong)((AdversarialStream*)self)->DeclaredLength;
        return 0;
    }

    [LibraryImport("ole32.dll")] private static partial int StgIsStorageILockBytes(nint lockBytes);
    [LibraryImport("ole32.dll")] private static partial int StgOpenStorageOnILockBytes(nint lockBytes, nint priority, uint mode, nint exclude, uint reserved, out nint storage);
    [LibraryImport("ole32.dll")] private static partial int OleInitialize(nint reserved);
    [LibraryImport("ole32.dll")] private static partial void OleUninitialize();
    [LibraryImport("gdi32.dll")] private static partial nint CreateBitmap(int width, int height, uint planes, uint bits, void* data);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint dc, nint value);
    [LibraryImport("gdi32.dll")] private static partial uint GetPixel(nint dc, int x, int y);
    [LibraryImport("gdi32.dll")] private static partial int DeleteDC(nint dc);
    [LibraryImport("gdi32.dll", EntryPoint = "CreateMetaFileW")] private static partial nint CreateMetaFile(nint fileName);
    [LibraryImport("gdi32.dll")] private static partial nint CloseMetaFile(nint dc);
    [LibraryImport("gdi32.dll")] private static partial uint GetMetaFileBitsEx(nint metafile, uint count, void* buffer);
    [LibraryImport("gdi32.dll", EntryPoint = "CreateEnhMetaFileW")] private static partial nint CreateEnhMetaFile(nint referenceDc, nint fileName, nint rectangle, nint description);
    [LibraryImport("gdi32.dll")] private static partial nint CloseEnhMetaFile(nint dc);
    [LibraryImport("gdi32.dll")] private static partial uint GetEnhMetaFileBits(nint metafile, uint count, void* buffer);
    [LibraryImport("gdi32.dll")] private static partial int Rectangle(nint dc, int left, int top, int right, int bottom);
}
