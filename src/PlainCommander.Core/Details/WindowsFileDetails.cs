using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PlainCommander.Core.Details;

/// <summary>
/// File properties from the Windows property system (SHGetPropertyStoreFromParsingName), the source of Explorer's
/// columns: it understands JPEG/HEIC/RAW photos, audio and video containers and program versions through the
/// handlers installed in Windows. The handler reads the file's header (a recording's length is not a "fast"
/// property); a handler that fails gives what it has (best effort).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsFileDetails : IFileDetailsProvider
{
    private const uint BestEffort = 0x40; // GPS_DEFAULT | GPS_BESTEFFORT

    private static readonly PropertyKey ImageWidth = new(new Guid("6444048F-4C8B-11D1-8B70-080036B11A03"), 3);
    private static readonly PropertyKey ImageHeight = new(new Guid("6444048F-4C8B-11D1-8B70-080036B11A03"), 4);
    private static readonly PropertyKey VideoWidth = new(new Guid("64440491-4C8B-11D1-8B70-080036B11A03"), 3);
    private static readonly PropertyKey VideoHeight = new(new Guid("64440491-4C8B-11D1-8B70-080036B11A03"), 4);
    private static readonly PropertyKey DateTaken = new(new Guid("14B81DA1-0135-4D31-96D9-6CBFC9671A99"), 36867);
    private static readonly PropertyKey MediaDuration = new(new Guid("64440490-4C8B-11D1-8B70-080036B11A03"), 3);
    private static readonly PropertyKey FileVersion = new(new Guid("0CEF7D53-FA64-11D1-A203-0000F81FEDEE"), 4);

    public FileDetails Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, BestEffort, ref iid, out var store) < 0 || store is null)
        {
            return FileDetails.None;
        }

        try
        {
            var width = UInt(store, ImageWidth) ?? UInt(store, VideoWidth);
            var height = UInt(store, ImageHeight) ?? UInt(store, VideoHeight);
            var duration = ULong(store, MediaDuration) is { } ticks and > 0 ? TimeSpan.FromTicks((long)ticks) : (TimeSpan?)null;
            var taken = Time(store, DateTaken);
            var version = Text(store, FileVersion);
            return width is null && height is null && duration is null && taken is null && version is null
                ? FileDetails.None
                : new FileDetails((int?)width, (int?)height, taken, duration, version);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return FileDetails.None;
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private static uint? UInt(IPropertyStore store, PropertyKey key)
    {
        var value = Read(store, key, out var variant);
        return value is 19 /* VT_UI4 */ or 18 /* VT_UI2 */ ? (uint)variant.Value : null;
    }

    private static ulong? ULong(IPropertyStore store, PropertyKey key)
    {
        var value = Read(store, key, out var variant);
        return value is 21 /* VT_UI8 */ ? (ulong)variant.Value : value is 19 ? (uint)variant.Value : null;
    }

    private static DateTimeOffset? Time(IPropertyStore store, PropertyKey key)
    {
        var value = Read(store, key, out var variant);
        return value is 64 /* VT_FILETIME */ && variant.Value > 0 ? new DateTimeOffset(DateTime.FromFileTimeUtc(variant.Value)) : null;
    }

    private static string? Text(IPropertyStore store, PropertyKey key)
    {
        var k = key;
        var variant = default(PropVariant);
        store.GetValue(ref k, ref variant);
        try
        {
            return variant.Type == 31 /* VT_LPWSTR */ && variant.Pointer != IntPtr.Zero ? Marshal.PtrToStringUni(variant.Pointer)?.Trim() : null;
        }
        finally
        {
            _ = PropVariantClear(ref variant);
        }
    }

    /// <summary>Reads a number or a time (no memory to free) and returns its type.</summary>
    private static ushort Read(IPropertyStore store, PropertyKey key, out PropVariant variant)
    {
        var k = key;
        variant = default;
        store.GetValue(ref k, ref variant);
        var type = variant.Type;
        if (type is not (18 or 19 or 21 or 64))
        {
            _ = PropVariantClear(ref variant);
        }

        return type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PropertyKey(Guid FormatId, uint PropertyId);

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort Type;

        [FieldOffset(8)]
        public long Value;

        [FieldOffset(8)]
        public IntPtr Pointer;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        uint GetCount();

        PropertyKey GetAt(uint index);

        void GetValue(ref PropertyKey key, ref PropVariant value);

        void SetValue(ref PropertyKey key, ref PropVariant value);

        void Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bindContext, uint flags, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PropVariant value);
}
