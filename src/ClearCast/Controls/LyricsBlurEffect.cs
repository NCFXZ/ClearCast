using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics.Effects;
using WinRT;

namespace ClearCast;

/// <summary>
/// Direct2D Gaussian blur description for Composition effect brushes, without a Win2D dependency.
/// Composition reads effects through the native IGraphicsEffectD2D1Interop interface, which a managed
/// CsWinRT object cannot expose, so this is a minimal native COM object with fixed vtables.
/// "BlurAmount" (the standard deviation in DIPs) is the only animatable property.
/// </summary>
internal static unsafe class LyricsBlurEffect
{
    private const int Ok = 0, NoInterface = unchecked((int)0x80004002), InvalidArgument = unchecked((int)0x80070057);
    internal const string EffectName = "Blur";
    private const uint DirectMapping = 1, BalancedOptimization = 1, SoftBorder = 0;
    private static readonly Guid UnknownId = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid InspectableId = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
    private static readonly Guid AgileObjectId = new("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90");
    private static readonly Guid MarshalId = new("00000003-0000-0000-C000-000000000046");
    private static readonly Guid GraphicsEffectId = new("CB51C0CE-8FE6-4636-B202-861FAA07D8F3");
    private static readonly Guid EffectSourceId = new("2D8F9DDC-4339-4EB9-9216-F9DEB75658A2");
    private static readonly Guid D2D1InteropId = new("2FC57384-A068-44D7-A331-30982FCF7177");
    private static readonly Guid PropertyValueId = new("4BD682DD-7554-40E9-9A9B-82654EDE7E62");
    private static readonly Guid GaussianBlurId = new("1FEB6D69-2FE6-4AC9-8C58-1D7F93E7A6A5");

    [StructLayout(LayoutKind.Sequential)]
    private struct Slot { public nint Vtable; public Effect* Owner; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Effect
    {
        public Slot Graphics, Source, Interop;
        public int References;
        public nint Input, Marshaler, Deviation, Optimization, Border;
    }

    private static readonly nint GraphicsVtable = CreateVtable(
        (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterface,
        (nint)(delegate* unmanaged<nint, uint>)&AddRef, (nint)(delegate* unmanaged<nint, uint>)&Release,
        (nint)(delegate* unmanaged<nint, uint*, Guid**, int>)&GetIids,
        (nint)(delegate* unmanaged<nint, nint*, int>)&GetRuntimeClassName,
        (nint)(delegate* unmanaged<nint, int*, int>)&GetTrustLevel,
        (nint)(delegate* unmanaged<nint, nint*, int>)&GetName, (nint)(delegate* unmanaged<nint, nint, int>)&SetName);
    private static readonly nint SourceVtable = CreateVtable(
        (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterface,
        (nint)(delegate* unmanaged<nint, uint>)&AddRef, (nint)(delegate* unmanaged<nint, uint>)&Release,
        (nint)(delegate* unmanaged<nint, uint*, Guid**, int>)&GetIids,
        (nint)(delegate* unmanaged<nint, nint*, int>)&GetRuntimeClassName,
        (nint)(delegate* unmanaged<nint, int*, int>)&GetTrustLevel);
    private static readonly nint InteropVtable = CreateVtable(
        (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterface,
        (nint)(delegate* unmanaged<nint, uint>)&AddRef, (nint)(delegate* unmanaged<nint, uint>)&Release,
        (nint)(delegate* unmanaged<nint, Guid*, int>)&GetEffectId,
        (nint)(delegate* unmanaged<nint, char*, uint*, uint*, int>)&GetNamedPropertyMapping,
        (nint)(delegate* unmanaged<nint, uint*, int>)&GetPropertyCount,
        (nint)(delegate* unmanaged<nint, uint, nint*, int>)&GetProperty,
        (nint)(delegate* unmanaged<nint, uint, nint*, int>)&GetSource,
        (nint)(delegate* unmanaged<nint, uint*, int>)&GetSourceCount);

    /// <summary>Creates a blur of <paramref name="source"/>, projected for CreateEffectFactory.</summary>
    internal static IGraphicsEffect Create(IGraphicsEffectSource source)
    {
        var effect = (Effect*)NativeMemory.AllocZeroed((nuint)sizeof(Effect));
        effect->Graphics = new Slot { Vtable = GraphicsVtable, Owner = effect };
        effect->Source = new Slot { Vtable = SourceVtable, Owner = effect };
        effect->Interop = new Slot { Vtable = InteropVtable, Owner = effect };
        effect->References = 1;
        nint pointer = (nint)(&effect->Graphics);
        try
        {
            // Everything Composition may read later is resolved now, on the creating thread, so the
            // callbacks never activate WinRT types or call managed projections on its worker threads.
            effect->Input = QueryManaged(source, EffectSourceId);
            // Standard deviation, optimization and border mode, in Direct2D property order.
            effect->Deviation = QueryManaged(PropertyValue.CreateSingle(0f), PropertyValueId);
            effect->Optimization = QueryManaged(PropertyValue.CreateUInt32(BalancedOptimization), PropertyValueId);
            effect->Border = QueryManaged(PropertyValue.CreateUInt32(SoftBorder), PropertyValueId);
            // Agile, like WRL effect descriptions: the free-threaded marshaler serves IMarshal.
            Marshal.ThrowExceptionForHR(CoCreateFreeThreadedMarshaler(pointer, out effect->Marshaler));
            if (effect->Input == 0 || effect->Deviation == 0 || effect->Optimization == 0 || effect->Border == 0)
                throw new InvalidCastException("An effect input or property has no required interface.");
        }
        catch
        {
            ReleaseEffect(pointer);
            throw;
        }
        // The projection takes its own reference; drop the creation reference afterwards.
        try { return MarshalInterface<IGraphicsEffect>.FromAbi(pointer); }
        finally { ReleaseEffect(pointer); }
    }

    private static nint CreateVtable(params nint[] methods)
    {
        var table = (nint*)NativeMemory.Alloc((nuint)(methods.Length * sizeof(nint)));
        for (int index = 0; index < methods.Length; ++index) table[index] = methods[index];
        return (nint)table;
    }

    private static Effect* Owner(nint self) => ((Slot*)self)->Owner;

    private static nint QueryManaged(object value, Guid id)
    {
        nint inspectable = MarshalInspectable<object>.FromManaged(value);
        try { return Marshal.QueryInterface(inspectable, in id, out nint result) == Ok ? result : 0; }
        finally { Marshal.Release(inspectable); }
    }

    [UnmanagedCallersOnly]
    private static int QueryInterface(nint self, Guid* id, nint* result)
    {
        Effect* effect = Owner(self);
        Guid requested = *id;
        if (requested == MarshalId)
            return effect->Marshaler == 0 ? NoInterface : Marshal.QueryInterface(effect->Marshaler, in requested, out *result);
        Slot* slot = requested == UnknownId || requested == InspectableId || requested == GraphicsEffectId || requested == AgileObjectId
            ? &effect->Graphics
            : requested == EffectSourceId ? &effect->Source
            : requested == D2D1InteropId ? &effect->Interop : null;
        *result = (nint)slot;
        if (slot is null) return NoInterface;
        Interlocked.Increment(ref effect->References);
        return Ok;
    }

    [UnmanagedCallersOnly]
    private static uint AddRef(nint self) => (uint)Interlocked.Increment(ref Owner(self)->References);

    [UnmanagedCallersOnly]
    private static uint Release(nint self) => ReleaseEffect(self);

    private static uint ReleaseEffect(nint self)
    {
        Effect* effect = Owner(self);
        int remaining = Interlocked.Decrement(ref effect->References);
        if (remaining == 0)
        {
            // Keep the object alive while the aggregated marshaler releases its inner state.
            effect->References = 1;
            foreach (nint held in new[] { effect->Marshaler, effect->Input, effect->Deviation, effect->Optimization, effect->Border })
                if (held != 0) Marshal.Release(held);
            NativeMemory.Free(effect);
        }
        return (uint)remaining;
    }

    [UnmanagedCallersOnly]
    private static int GetIids(nint self, uint* count, Guid** ids) { *count = 0; *ids = null; return Ok; }

    [UnmanagedCallersOnly]
    private static int GetRuntimeClassName(nint self, nint* name) { *name = 0; return Ok; }

    [UnmanagedCallersOnly]
    private static int GetTrustLevel(nint self, int* level) { *level = 0; return Ok; }

    // Animatable properties are addressed as "<effect name>.<property>", so the name must be "Blur".
    [UnmanagedCallersOnly]
    private static int GetName(nint self, nint* name)
    {
        fixed (char* text = EffectName) return WindowsCreateString(text, (uint)EffectName.Length, name);
    }

    [UnmanagedCallersOnly]
    private static int SetName(nint self, nint name) => Ok;

    [UnmanagedCallersOnly]
    private static int GetEffectId(nint self, Guid* id) { *id = GaussianBlurId; return Ok; }

    [UnmanagedCallersOnly]
    private static int GetNamedPropertyMapping(nint self, char* name, uint* index, uint* mapping)
    {
        *index = 0;
        *mapping = DirectMapping;
        return name is not null && new string(name) == "BlurAmount" ? Ok : InvalidArgument;
    }

    [UnmanagedCallersOnly]
    private static int GetPropertyCount(nint self, uint* count) { *count = 3; return Ok; }

    [UnmanagedCallersOnly]
    private static int GetProperty(nint self, uint index, nint* value)
    {
        Effect* effect = Owner(self);
        nint property = index switch { 0 => effect->Deviation, 1 => effect->Optimization, 2 => effect->Border, _ => 0 };
        *value = property;
        if (property == 0) return InvalidArgument;
        Marshal.AddRef(property);
        return Ok;
    }

    [UnmanagedCallersOnly]
    private static int GetSource(nint self, uint index, nint* source)
    {
        *source = 0;
        if (index != 0) return InvalidArgument;
        nint input = Owner(self)->Input;
        Marshal.AddRef(input);
        *source = input;
        return Ok;
    }

    [UnmanagedCallersOnly]
    private static int GetSourceCount(nint self, uint* count) { *count = 1; return Ok; }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString(char* text, uint length, nint* result);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateFreeThreadedMarshaler(nint outer, out nint marshaler);
}
