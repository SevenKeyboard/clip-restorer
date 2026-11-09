using System.Runtime.InteropServices;

namespace SevenKeyboard;

/// <summary>An independent, owned native payload. Use and dispose on the OLE STA.
/// Capturing never takes ownership of the supplied medium or clipboard handle.</summary>
internal sealed unsafe class NativePayload : IDisposable
{
    internal const uint HGlobal = 1;
    internal const uint IStream = 4;
    internal const uint IStorage = 8;
    internal const uint Gdi = 16;
    internal const uint MfPict = 32;
    internal const uint EnhMf = 64;

    private const uint Moveable = 0x0002;
    private const uint ZeroInit = 0x0040;
    private const int InvalidMedium = unchecked((int)0x80040069); // DV_E_TYMED
    private const int InvalidHandle = unchecked((int)0x80030006); // STG_E_INVALIDHANDLE
    private const int ReadFault = unchecked((int)0x8003001E);
    private const int WriteFault = unchecked((int)0x8003001D);
    private const int InvalidFunction = unchecked((int)0x80030001);

    private NativeMedium _owned;
    private bool _disposed;
    private ulong? _logicalByteLength;

    public uint Format { get; }
    public int Index { get; }
    public uint Tymed { get; }
    public string? Name { get; }

    private NativePayload(uint format, int index, NativeMedium owned, string? name)
    {
        Format = format;
        Index = index;
        Tymed = owned.Tymed;
        Name = name;
        _owned = owned;
    }

    public static NativePayload Capture(uint format, int index, in NativeMedium borrowed, string? name = null)
    {
        var owned = CloneMedium(borrowed);
        try { return new(format, index, owned, name); }
        catch { owned.Dispose(); throw; }
    }

    public static NativePayload FromClipboard(uint format, nint borrowed, string? name = null)
    {
        // These are the handle-bearing standard formats, including their display variants.
        // Private/registered formats otherwise retain their HGLOBAL representation.
        uint tymed = format switch
        {
            2 or 9 or 0x82 => Gdi,
            3 or 0x83 => MfPict,
            14 or 0x8e => EnhMf,
            0x80 => throw new COMException("CF_OWNERDISPLAY has no standalone payload.", InvalidMedium),
            _ => HGlobal
        };
        var medium = new NativeMedium { Tymed = tymed, Handle = borrowed };
        return Capture(format, -1, medium, name);
    }

    public static NativePayload FromBytes(uint format, byte[] bytes, int index = -1, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (format is 2 or 3 or 9 or 14 or 0x80 or 0x82 or 0x83 or 0x8e)
            throw new COMException("This clipboard format requires a typed native handle.", InvalidMedium);

        nint global = AllocateGlobal((nuint)bytes.LongLength);
        try
        {
            if (bytes.Length != 0)
            {
                nint destination = LockGlobal(global);
                try
                {
                    bytes.AsSpan().CopyTo(new Span<byte>((void*)destination, bytes.Length));
                }
                finally { NativePayloadNative.GlobalUnlock(global); }
            }
            return new NativePayload(format, index,
                new NativeMedium { Tymed = HGlobal, Handle = global }, name);
        }
        catch
        {
            NativePayloadNative.GlobalFree(global);
            throw;
        }
    }

    public static NativePayload FromMarker(uint format, string? name = null) =>
        FromBytes(format, [], -1, name);

    /// <summary>Applies authoritative FD_FILESIZE metadata to virtual-file bytes.
    /// HGLOBAL length must fit its allocation; an IStream must already match exactly.
    /// Ordinary byte payloads retain their full allocation, including padding.
    /// This metadata affects stream conversion only; the owned HGLOBAL stays intact.</summary>
    internal void SetLogicalByteLength(ulong length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Tymed == HGlobal)
        {
            if (length > (ulong)GlobalLength(_owned.Handle))
                throw new COMException("The virtual-file size exceeds its HGLOBAL allocation.", ReadFault);
        }
        else if (Tymed == IStream)
        {
            NativePayloadStat stat = default;
            try
            {
                Check(NativePayloadNative.StreamStat(_owned.Handle, out stat), "IStream.Stat");
                if (length != stat.Size)
                    throw new COMException("The virtual-file size differs from its stream length.", ReadFault);
            }
            finally
            {
                if (stat.Name != 0)
                    NativePayloadNative.CoTaskMemFree(stat.Name);
            }
        }
        else
            throw new COMException("Logical byte length requires HGLOBAL or IStream bytes.", InvalidMedium);
        _logicalByteLength = length;
    }

    public NativePayload Clone()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var clone = Capture(Format, Index, _owned, Name);
        clone._logicalByteLength = _logicalByteLength;
        return clone;
    }

    public NativeMedium CreateMedium()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Windows rejects a discarded zero-size handle when it is published.
        // Keep logical emptiness internally, but render a valid zero-filled marker.
        if (Tymed == HGlobal && GlobalLength(_owned.Handle) == 0)
            return new NativeMedium { Tymed = HGlobal, Handle = AllocateGlobal(1) };
        return CloneMedium(_owned);
    }


    /// <summary>Formats this owned representation can independently materialize.
    /// Bytes are never advertised as IStorage without parsing a compound document.</summary>
    public uint SupportedMedia => Tymed switch
    {
        HGlobal or IStream => HGlobal | IStream,
        IStorage => IStorage | HGlobal | IStream,
        _ => Tymed
    };

    public NativeMedium CreateMedium(uint requestedMask)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((requestedMask & Tymed) != 0)
            return CreateMedium();

        uint available = SupportedMedia & requestedMask;
        if ((available & HGlobal) != 0)
        {
            nint global = Tymed == IStream ? StreamToGlobal(_owned.Handle) : CopyStorage(_owned.Handle, true);
            return PublishGlobal(global);
        }
        if ((available & IStream) != 0)
        {
            nint global = Tymed == HGlobal ? CloneGlobal(_owned.Handle) : CopyStorage(_owned.Handle, true);
            return new NativeMedium
            {
                Tymed = IStream,
                Handle = StreamFromOwnedGlobal(global, Tymed == HGlobal ? _logicalByteLength : null)
            };
        }
        throw new COMException("None of the requested storage media are supported.", InvalidMedium);
    }

    // Takes ownership even if publication/allocation fails.
    private static NativeMedium PublishGlobal(nint global)
    {
        try
        {
            if (GlobalLength(global) == 0)
            {
                nint allocatedMarker = AllocateGlobal(1);
                NativePayloadNative.GlobalFree(global);
                global = allocatedMarker;
            }
            return new NativeMedium { Tymed = HGlobal, Handle = global };
        }
        catch
        {
            NativePayloadNative.GlobalFree(global);
            throw;
        }
    }

    // Takes ownership of an independent HGLOBAL and transfers it only on success.
    private static nint StreamFromOwnedGlobal(nint global, ulong? logicalByteLength)
    {
        nint stream = 0;
        try
        {
            ulong available = (ulong)GlobalLength(global);
            if (logicalByteLength is ulong length && length > available)
                throw new COMException("The virtual-file size exceeds its HGLOBAL allocation.", ReadFault);
            if (available == 0)
            {
                NativePayloadNative.GlobalFree(global);
                global = 0;
            }
            Check(NativePayloadNative.CreateStreamOnHGlobal(global, 1, out stream), "CreateStreamOnHGlobal");
            if (stream == 0)
                throw new COMException("CreateStreamOnHGlobal returned no stream.", InvalidHandle);
            global = 0; // The stream owns the allocation, including any reallocation by SetSize.
            if (logicalByteLength is ulong authoritativeLength)
                Check(NativePayloadNative.StreamSetSize(stream, authoritativeLength), "IStream.SetSize");
            nint result = stream;
            stream = 0;
            return result;
        }
        finally
        {
            if (global != 0)
                NativePayloadNative.GlobalFree(global);
            if (stream != 0)
                NativePayloadNative.Release(stream);
        }
    }

    private static nint StreamToGlobal(nint source)
    {
        NativePayloadStat stat = default;
        nint independent = CloneStream(source);
        nint destination = 0;
        try
        {
            Check(NativePayloadNative.StreamStat(independent, out stat), "IStream.Stat");
            if (stat.Size > (ulong)nuint.MaxValue || stat.Size > long.MaxValue)
                throw new OutOfMemoryException("The stream is too large for an HGLOBAL allocation.");

            Check(NativePayloadNative.GetHGlobalFromStream(independent, out nint backing), "GetHGlobalFromStream");
            if ((ulong)GlobalLength(backing) < stat.Size)
                throw new COMException("The memory stream backing is truncated.", ReadFault);
            destination = AllocateGlobal((nuint)stat.Size);
            if (stat.Size != 0)
            {
                nint src = LockGlobal(backing);
                try
                {
                    nint dst = LockGlobal(destination);
                    try { Buffer.MemoryCopy((void*)src, (void*)dst, (long)stat.Size, (long)stat.Size); }
                    finally { NativePayloadNative.GlobalUnlock(destination); }
                }
                finally { NativePayloadNative.GlobalUnlock(backing); }
            }
            nint result = destination;
            destination = 0;
            return result;
        }
        finally
        {
            if (stat.Name != 0)
                NativePayloadNative.CoTaskMemFree(stat.Name);
            if (destination != 0)
                NativePayloadNative.GlobalFree(destination);
            NativePayloadNative.Release(independent);
        }
    }

    internal byte[] ReadBytes()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Tymed != HGlobal)
            throw new COMException("Only HGLOBAL payloads can be read as bytes.", InvalidMedium);

        nuint size = GlobalLength(_owned.Handle);
        if (size > int.MaxValue)
            throw new OutOfMemoryException("The payload is too large for a managed byte array.");
        if (size == 0)
            return [];

        nint source = LockGlobal(_owned.Handle);
        try { return new ReadOnlySpan<byte>((void*)source, (int)size).ToArray(); }
        finally { NativePayloadNative.GlobalUnlock(_owned.Handle); }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _owned.Dispose();
    }

    private static NativeMedium CloneMedium(in NativeMedium source)
    {
        if (source.Handle == 0)
            throw new COMException("A null medium handle is not a valid empty payload.", InvalidHandle);

        nint copy = source.Tymed switch
        {
            HGlobal => CloneGlobal(source.Handle),
            Gdi => CloneGdi(source.Handle),
            MfPict => CloneMetafilePicture(source.Handle),
            EnhMf => RequireHandle(NativePayloadNative.CopyEnhMetaFile(source.Handle, 0), "CopyEnhMetaFile"),
            IStream => CloneStream(source.Handle),
            IStorage => CloneStorage(source.Handle),
            _ => throw new COMException("The storage medium is unsupported.", InvalidMedium)
        };
        // We duplicated the content. The source's release owner must never be retained.
        return new NativeMedium { Tymed = source.Tymed, Handle = copy, ReleaseOwner = 0 };
    }

    private static nuint GlobalLength(nint handle)
    {
        if (handle == 0 || NativePayloadNative.GlobalFlags(handle) == 0x8000)
            throw new COMException("Invalid HGLOBAL payload.", InvalidHandle);

        nuint size = NativePayloadNative.GlobalSize(handle);
        if (size == 0 && Marshal.GetLastPInvokeError() != 0)
            ThrowLastError("GlobalSize");

        // A zero-sized moveable allocation is a valid discarded handle.
        // Do not lock it, and do not confuse it with a failed retrieval.
        return size;
    }

    private static nint AllocateGlobal(nuint size)
    {
        nint global = NativePayloadNative.GlobalAlloc(Moveable | ZeroInit, size);
        if (global == 0)
            throw new OutOfMemoryException("GlobalAlloc could not allocate the native payload.");
        return global;
    }

    private static nint LockGlobal(nint handle) =>
        RequireHandle(NativePayloadNative.GlobalLock(handle), "GlobalLock");

    private static nint CloneGlobal(nint source)
    {
        nuint size = GlobalLength(source);
        nint destination = AllocateGlobal(size);
        try
        {
            if (size != 0)
            {
                if (size > long.MaxValue)
                    throw new OutOfMemoryException("The global allocation is too large to copy.");
                nint src = LockGlobal(source);
                try
                {
                    nint dst = LockGlobal(destination);
                    try { Buffer.MemoryCopy((void*)src, (void*)dst, (long)size, (long)size); }
                    finally { NativePayloadNative.GlobalUnlock(destination); }
                }
                finally { NativePayloadNative.GlobalUnlock(source); }
            }
            return destination;
        }
        catch
        {
            NativePayloadNative.GlobalFree(destination);
            throw;
        }
    }

    private static nint CloneGdi(nint source)
    {
        uint kind = NativePayloadNative.GetObjectType(source);
        if (kind == 7) // OBJ_BITMAP; omit LR_COPYRETURNORG so the result is independent.
            return RequireHandle(NativePayloadNative.CopyImage(source, 0, 0, 0, 0x2000), "CopyImage");

        if (kind != 5) // OBJ_PAL
            throw new COMException("Only bitmap and palette GDI media are supported.", InvalidMedium);

        uint count = NativePayloadNative.GetPaletteEntries(source, 0, 0, null);
        if (count == 0)
            ThrowLastError("GetPaletteEntries");
        if (count > ushort.MaxValue)
            throw new COMException("The palette has an invalid entry count.", ReadFault);

        byte[] palette = new byte[checked(4 + (int)count * 4)];
        fixed (byte* buffer = palette)
        {
            ((ushort*)buffer)[0] = 0x0300;
            ((ushort*)buffer)[1] = (ushort)count;
            if (NativePayloadNative.GetPaletteEntries(source, 0, count, buffer + 4) != count)
                throw new COMException("The complete palette could not be read.", ReadFault);
            return RequireHandle(NativePayloadNative.CreatePalette(buffer), "CreatePalette");
        }
    }

    private static nint CloneMetafilePicture(nint source)
    {
        if (GlobalLength(source) < (nuint)sizeof(NativePayloadMetafilePicture))
            throw new COMException("The METAFILEPICT wrapper is truncated.", ReadFault);

        NativePayloadMetafilePicture picture;
        nint sourceMemory = LockGlobal(source);
        try { picture = *(NativePayloadMetafilePicture*)sourceMemory; }
        finally { NativePayloadNative.GlobalUnlock(source); }

        if (picture.Metafile == 0)
            throw new COMException("The METAFILEPICT has no metafile.", InvalidHandle);
        nint metafile = RequireHandle(NativePayloadNative.CopyMetaFile(picture.Metafile, 0), "CopyMetaFile");
        nint destination = 0;
        try
        {
            destination = AllocateGlobal((nuint)sizeof(NativePayloadMetafilePicture));
            nint memory = LockGlobal(destination);
            try
            {
                picture.Metafile = metafile;
                *(NativePayloadMetafilePicture*)memory = picture;
            }
            finally { NativePayloadNative.GlobalUnlock(destination); }
            return destination;
        }
        catch
        {
            NativePayloadNative.DeleteMetaFile(metafile);
            if (destination != 0)
                NativePayloadNative.GlobalFree(destination);
            throw;
        }
    }

    private static nint CloneStream(nint source)
    {
        nint destination = 0;
        int cursorHr = NativePayloadNative.StreamSeek(source, 0, 1, out ulong originalPosition);
        ThrowIfOutOfMemory(cursorHr, "IStream.Seek(current)");
        bool restoreCursor = cursorHr == 0;
        bool outOfMemory = false;
        try
        {
            try
            {
                ulong length;
                int statHr = NativePayloadNative.StreamStat(source, out var stat);
                try
                {
                    ThrowIfOutOfMemory(statHr, "IStream.Stat");
                    if (statHr == 0)
                        length = stat.Size;
                    else if (restoreCursor)
                    {
                        Check(NativePayloadNative.StreamSeek(source, 0, 2, out length), "IStream.Seek(end)");
                    }
                    else
                    {
                        Check(statHr, "A nonseekable stream must report its entire length");
                        throw new COMException("The stream length is unavailable.", ReadFault);
                    }
                }
                finally
                {
                    if (stat.Name != 0)
                        NativePayloadNative.CoTaskMemFree(stat.Name);
                }

                if (length > long.MaxValue || (restoreCursor && originalPosition > long.MaxValue))
                    throw new COMException("The stream position exceeds the supported seek range.", InvalidFunction);

                if (restoreCursor)
                    SeekExactly(source, 0);

                Check(NativePayloadNative.CreateStreamOnHGlobal(0, 1, out destination), "CreateStreamOnHGlobal");
                if (destination == 0)
                    throw new COMException("CreateStreamOnHGlobal returned no stream.", InvalidHandle);

                // Read/Write works with sources whose optional optimized CopyTo is unimplemented.
                // For a nonseekable source, matching Stat's full length plus EOF rejects a partial tail.
                byte[] buffer = new byte[64 * 1024];
                fixed (byte* memory = buffer)
                {
                    ulong remaining = length;
                    while (remaining != 0)
                    {
                        uint wanted = (uint)Math.Min((ulong)buffer.Length, remaining);
                        int hr = NativePayloadNative.StreamRead(source, memory, wanted, out uint read);
                        if (hr < 0) Check(hr, "IStream.Read");
                        if (read == 0 || read > wanted || (hr != 0 && hr != 1))
                            throw new COMException("The stream ended before its reported length.", ReadFault);

                        uint writtenTotal = 0;
                        while (writtenTotal != read)
                        {
                            int writeHr = NativePayloadNative.StreamWrite(
                                destination, memory + writtenTotal, read - writtenTotal, out uint written);
                            Check(writeHr, "IStream.Write");
                            if (writeHr != 0 || written == 0 || written > read - writtenTotal)
                                throw new COMException("The destination stream accepted an incomplete write.", WriteFault);
                            writtenTotal += written;
                        }
                        remaining -= read;
                        if (hr == 1 && remaining != 0)
                            throw new COMException("The stream was truncated.", ReadFault);
                    }

                    int endHr = NativePayloadNative.StreamRead(source, memory, 1, out uint extra);
                    if (endHr < 0) Check(endHr, "IStream.Read(end)");
                    if (extra != 0 || (endHr != 0 && endHr != 1))
                        throw new COMException("The stream length changed while copying.", ReadFault);
                }
                SeekExactly(destination, 0);
            }
            catch (OutOfMemoryException)
            {
                outOfMemory = true;
                throw;
            }
            finally
            {
                if (restoreCursor)
                {
                    try
                    {
                        if (originalPosition > long.MaxValue)
                            throw new COMException("The original stream cursor cannot be restored.", InvalidFunction);
                        SeekExactly(source, originalPosition);
                    }
                    // A secondary restore failure must not downgrade a fatal OOM into a skipped format.
                    catch when (outOfMemory) { }
                }
            }

            nint result = destination;
            destination = 0;
            return result;
        }
        finally
        {
            if (destination != 0)
                NativePayloadNative.Release(destination);
        }
    }

    private static void SeekExactly(nint stream, ulong position)
    {
        Check(NativePayloadNative.StreamSeek(stream, checked((long)position), 0, out ulong actual), "IStream.Seek");
        if (actual != position)
            throw new COMException("The stream did not seek to the requested position.", InvalidFunction);
    }

    private static nint CloneStorage(nint source) => CopyStorage(source, false);

    private static nint CopyStorage(nint source, bool asGlobal)
    {
        NativePayloadStat stat = default;
        nint lockBytes = 0;
        nint destination = 0;
        try
        {
            Check(NativePayloadNative.StorageStat(source, out stat), "IStorage.Stat");
            Check(NativePayloadNative.CreateILockBytesOnHGlobal(0, 1, out lockBytes), "CreateILockBytesOnHGlobal");
            if (lockBytes == 0)
                throw new COMException("CreateILockBytesOnHGlobal returned no object.", InvalidHandle);
            Check(NativePayloadNative.StgCreateDocfileOnILockBytes(lockBytes, 0x1012, 0, out destination),
                "StgCreateDocfileOnILockBytes");
            if (destination == 0)
                throw new COMException("StgCreateDocfileOnILockBytes returned no storage.", InvalidHandle);

            Check(NativePayloadNative.StorageCopyTo(source, destination), "IStorage.CopyTo");
            Check(NativePayloadNative.StorageSetClass(destination, stat.ClassId), "IStorage.SetClass");
            Check(NativePayloadNative.StorageSetStateBits(destination, stat.StateBits), "IStorage.SetStateBits");
            Check(NativePayloadNative.StorageCommit(destination), "IStorage.Commit");
            if (asGlobal)
            {
                Check(NativePayloadNative.GetHGlobalFromILockBytes(lockBytes, out nint backing), "GetHGlobalFromILockBytes");
                return CloneGlobal(backing);
            }

            nint result = destination;
            destination = 0;
            return result;
        }
        finally
        {
            if (stat.Name != 0)
                NativePayloadNative.CoTaskMemFree(stat.Name);
            if (destination != 0)
                NativePayloadNative.Release(destination);
            if (lockBytes != 0)
                NativePayloadNative.Release(lockBytes);
        }
    }

    private static nint RequireHandle(nint handle, string operation)
    {
        if (handle == 0)
            ThrowLastError(operation);
        return handle;
    }

    private static void ThrowLastError(string operation)
    {
        int error = Marshal.GetLastPInvokeError();
        if (error is 8 or 14 || error == unchecked((int)0x8007000E) || error == unchecked((int)0x80030008))
            throw new OutOfMemoryException(operation + " ran out of memory.");
        int hr = error == 0 ? unchecked((int)0x80004005) :
            error < 0 ? error : unchecked((int)(0x80070000u | ((uint)error & 0xffff)));
        throw new COMException(operation + " failed.", hr);
    }

    private static void ThrowIfOutOfMemory(int hr, string operation)
    {
        if (hr is unchecked((int)0x8007000E) or unchecked((int)0x80070008) or unchecked((int)0x80030008))
            throw new OutOfMemoryException(operation + " ran out of memory.");
    }

    private static void Check(int hr, string operation)
    {
        ThrowIfOutOfMemory(hr, operation);
        if (hr != 0)
            throw new COMException(operation + " failed.", hr < 0 ? hr : unchecked((int)0x80004005));
    }
}
