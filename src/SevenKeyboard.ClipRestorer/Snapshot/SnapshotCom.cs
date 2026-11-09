using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SevenKeyboard;

/// <summary>Raw COM identity and lifetime. No runtime-generated COM wrappers are involved.</summary>
internal static unsafe class SnapshotCom
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Identity
    {
        internal nint Vtable;
        internal nint StateHandle;
        internal int References;
    }

    private sealed class ProviderState : IDisposable
    {
        internal readonly SnapshotEntry[] Entries;
        internal ProviderState(SnapshotEntry[] entries) => Entries = entries;
        public void Dispose() => SnapshotEntry.DisposeAll(Entries);
    }

    private sealed record FormatInfo(ushort Format, uint Aspect, int Index, uint Tymed, byte[]? Device);

    private sealed class EnumeratorState
    {
        internal readonly FormatInfo[] Formats;
        internal int Position;
        internal EnumeratorState(FormatInfo[] formats, int position = 0)
        { Formats = formats; Position = position; }
    }

    private static readonly nint ProviderVtable = BuildProviderVtable();
    private static readonly nint EnumeratorVtable = BuildEnumeratorVtable();

    // Ownership transfers only on successful return.
    internal static nint CreateProvider(SnapshotEntry[] entries) =>
        Create(new ProviderState(entries), ProviderVtable);

    private static nint Create(object state, nint vtable)
    {
        Identity* identity = (Identity*)NativeMemory.AllocZeroed((nuint)sizeof(Identity));
        if (identity is null) throw new OutOfMemoryException();
        try
        {
            identity->StateHandle = GCHandle.ToIntPtr(GCHandle.Alloc(state));
            identity->References = 1;
            identity->Vtable = vtable;
            return (nint)identity;
        }
        catch { NativeMemory.Free(identity); throw; }
    }

    private static T State<T>(nint self) where T : class =>
        (T)GCHandle.FromIntPtr(((Identity*)self)->StateHandle).Target!;

    private static uint AddReference(nint self) =>
        unchecked((uint)Interlocked.Increment(ref ((Identity*)self)->References));

    private static uint ReleaseReference(nint self)
    {
        Identity* identity = (Identity*)self;
        int remaining = Interlocked.Decrement(ref identity->References);
        if (remaining != 0) return unchecked((uint)remaining);
        GCHandle handle = GCHandle.FromIntPtr(identity->StateHandle);
        try
        {
            if (handle.Target is IDisposable owned) owned.Dispose();
        }
        finally
        {
            handle.Free();
            NativeMemory.Free(identity);
        }
        return 0;
    }

    private static int Query(nint self, Guid* iid, nint* output, Guid supported)
    {
        if (output is null) return SnapshotNative.E_POINTER;
        *output = 0;
        if (iid is null) return SnapshotNative.E_POINTER;
        if (*iid != SnapshotNative.IUnknownId && *iid != supported)
            return SnapshotNative.E_NOINTERFACE;
        AddReference(self);
        *output = self;
        return 0;
    }

    private static nint BuildProviderVtable()
    {
        nint* table = (nint*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(SnapshotCom), 12 * sizeof(nint));
        table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&ProviderQuery;
        table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ComAddRef;
        table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ComRelease;
        table[3] = (nint)(delegate* unmanaged[Stdcall]<nint, SnapshotFormat*, NativeMedium*, int>)&GetData;
        table[4] = (nint)(delegate* unmanaged[Stdcall]<nint, SnapshotFormat*, NativeMedium*, int>)&GetDataHere;
        table[5] = (nint)(delegate* unmanaged[Stdcall]<nint, SnapshotFormat*, int>)&QueryGetData;
        table[6] = (nint)(delegate* unmanaged[Stdcall]<nint, SnapshotFormat*, SnapshotFormat*, int>)&CanonicalFormat;
        table[7] = (nint)(delegate* unmanaged[Stdcall]<nint, SnapshotFormat*, NativeMedium*, int, int>)&SetData;
        table[8] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)&EnumFormats;
        table[9] = (nint)(delegate* unmanaged[Stdcall]<nint, SnapshotFormat*, uint, nint, uint*, int>)&Advise;
        table[10] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, int>)&Unadvise;
        table[11] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&EnumAdvise;
        return (nint)table;
    }

    private static nint BuildEnumeratorVtable()
    {
        nint* table = (nint*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(SnapshotCom), 7 * sizeof(nint));
        table[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&EnumeratorQuery;
        table[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ComAddRef;
        table[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ComRelease;
        table[3] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, SnapshotFormat*, uint*, int>)&EnumNext;
        table[4] = (nint)(delegate* unmanaged[Stdcall]<nint, uint, int>)&EnumSkip;
        table[5] = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&EnumReset;
        table[6] = (nint)(delegate* unmanaged[Stdcall]<nint, nint*, int>)&EnumClone;
        return (nint)table;
    }

    private static bool DeviceMatches(byte[]? stored, nint requested)
    {
        if (stored is null) return requested == 0;
        if (requested == 0 || *(uint*)requested != stored.Length) return false;
        return new ReadOnlySpan<byte>((void*)requested, stored.Length).SequenceEqual(stored);
    }

    private static SnapshotEntry? Find(ProviderState state, SnapshotFormat* format, out int error)
    {
        error = SnapshotNative.DV_E_FORMATETC;
        foreach (SnapshotEntry entry in state.Entries)
        {
            if (entry.Payload.Format != format->Format) continue;
            if (entry.Aspect != format->Aspect) { error = SnapshotNative.DV_E_DVASPECT; continue; }
            if (entry.Payload.Index != format->Index) { error = SnapshotNative.DV_E_LINDEX; continue; }
            if (!DeviceMatches(entry.TargetDevice, format->TargetDevice))
            { error = SnapshotNative.DV_E_DVTARGETDEVICE; continue; }
            if ((entry.Payload.SupportedMedia & format->Tymed) == 0)
            { error = SnapshotNative.DV_E_TYMED; continue; }
            error = 0;
            return entry;
        }
        return null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ProviderQuery(nint self, Guid* iid, nint* output)
    {
        try { return Query(self, iid, output, SnapshotNative.IDataObjectId); }
        catch (Exception e) { return SnapshotNative.Error(e); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnumeratorQuery(nint self, Guid* iid, nint* output)
    {
        try { return Query(self, iid, output, SnapshotNative.IEnumFormatEtcId); }
        catch (Exception e) { return SnapshotNative.Error(e); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ComAddRef(nint self)
    {
        try { return AddReference(self); } catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ComRelease(nint self)
    {
        try { return ReleaseReference(self); } catch { return 0; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetData(nint self, SnapshotFormat* format, NativeMedium* output)
    {
        if (output is null) return SnapshotNative.E_POINTER;
        *output = default;
        if (format is null) return SnapshotNative.E_POINTER;
        try
        {
            SnapshotEntry? entry = Find(State<ProviderState>(self), format, out int hr);
            if (entry is null) return hr;
            *output = entry.Payload.CreateMedium(format->Tymed);
            return 0;
        }
        catch (Exception e) { return SnapshotNative.Error(e); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetDataHere(nint self, SnapshotFormat* format, NativeMedium* output)
    {
        return SnapshotNative.E_NOTIMPL;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryGetData(nint self, SnapshotFormat* format)
    {
        if (format is null) return SnapshotNative.E_POINTER;
        try { Find(State<ProviderState>(self), format, out int hr); return hr; }
        catch (Exception e) { return SnapshotNative.Error(e); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CanonicalFormat(nint self, SnapshotFormat* input, SnapshotFormat* output)
    {
        if (output is null) return SnapshotNative.E_POINTER;
        *output = default;
        return SnapshotNative.E_NOTIMPL;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetData(nint self, SnapshotFormat* format, NativeMedium* medium, int release) =>
        SnapshotNative.E_NOTIMPL; // Read-only provider: never consumes a rejected medium.

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnumFormats(nint self, uint direction, nint* output)
    {
        if (output is null) return SnapshotNative.E_POINTER;
        *output = 0;
        if (direction != 1) return SnapshotNative.E_NOTIMPL;
        try
        {
            // Enumerate the captured representation so OLE does not prefer a
            // converted stream over native Win32 HGLOBAL text during rendering.
            // Explicit GetData/QueryGetData requests still support conversions.
            FormatInfo[] formats = State<ProviderState>(self).Entries.Select(entry =>
                new FormatInfo(checked((ushort)entry.Payload.Format), entry.Aspect,
                    entry.Payload.Index, entry.Payload.Tymed, entry.TargetDevice)).ToArray();
            *output = Create(new EnumeratorState(formats), EnumeratorVtable);
            return 0;
        }
        catch (Exception e) { return SnapshotNative.Error(e); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Advise(nint self, SnapshotFormat* format, uint flags, nint sink, uint* connection)
    {
        if (connection is not null) *connection = 0;
        return SnapshotNative.OLE_E_ADVISENOTSUPPORTED;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Unadvise(nint self, uint connection) => SnapshotNative.OLE_E_ADVISENOTSUPPORTED;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnumAdvise(nint self, nint* output)
    {
        if (output is not null) *output = 0;
        return SnapshotNative.OLE_E_ADVISENOTSUPPORTED;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnumNext(nint self, uint count, SnapshotFormat* output, uint* fetched)
    {
        if (fetched is not null) *fetched = 0;
        if (count != 0 && output is null) return SnapshotNative.E_POINTER;
        if (fetched is null && count != 1) return SnapshotNative.E_POINTER;
        uint completed = 0;
        try
        {
            EnumeratorState state = State<EnumeratorState>(self);
            lock (state)
            {
                int available = state.Formats.Length - state.Position;
                int take = (int)Math.Min(count, (uint)available);
                for (int i = 0; i < take; i++)
                {
                    FormatInfo info = state.Formats[state.Position + i];
                    nint device = 0;
                    if (info.Device is not null)
                    {
                        device = SnapshotNative.CoTaskMemAlloc((nuint)info.Device.Length);
                        if (device == 0) throw new OutOfMemoryException();
                        info.Device.AsSpan().CopyTo(new Span<byte>((void*)device, info.Device.Length));
                    }
                    output[i] = new SnapshotFormat
                    {
                        Format = info.Format, Aspect = info.Aspect, Index = info.Index,
                        Tymed = info.Tymed, TargetDevice = device
                    };
                    completed++;
                }
                state.Position += take;
            }
            if (fetched is not null) *fetched = completed;
            return completed == count ? 0 : 1;
        }
        catch (Exception e)
        {
            // No partial allocation ownership escapes on a failed Next.
            for (uint i = 0; i < completed; i++)
            {
                SnapshotNative.CoTaskMemFree(output[i].TargetDevice);
                output[i] = default;
            }
            return SnapshotNative.Error(e);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnumSkip(nint self, uint count)
    {
        try
        {
            EnumeratorState state = State<EnumeratorState>(self);
            lock (state)
            {
                uint take = Math.Min(count, (uint)(state.Formats.Length - state.Position));
                state.Position += (int)take;
                return take == count ? 0 : 1;
            }
        }
        catch (Exception e) { return SnapshotNative.Error(e); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnumReset(nint self)
    {
        try
        {
            EnumeratorState state = State<EnumeratorState>(self);
            lock (state) state.Position = 0;
            return 0;
        }
        catch (Exception e) { return SnapshotNative.Error(e); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnumClone(nint self, nint* output)
    {
        if (output is null) return SnapshotNative.E_POINTER;
        *output = 0;
        try
        {
            EnumeratorState state = State<EnumeratorState>(self);
            lock (state)
                *output = Create(new EnumeratorState(state.Formats, state.Position), EnumeratorVtable);
            return 0;
        }
        catch (Exception e) { return SnapshotNative.Error(e); }
    }
}
