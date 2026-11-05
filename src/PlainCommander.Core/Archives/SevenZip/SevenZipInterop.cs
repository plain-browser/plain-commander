using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace PlainCommander.Core.Archives.SevenZip;

// The COM-style interfaces of 7-Zip's 7z.dll (IArchive.h, IStream.h, IProgress.h, IPassword.h), in vtable order.
// Every method keeps its HRESULT: 7-Zip uses S_FALSE and E_ABORT as answers, not only as failures.

internal static class HResult
{
    public const int Ok = 0;
    public const int False = 1;
    public const int Abort = unchecked((int)0x80004004);
    public const int Fail = unchecked((int)0x80004005);
    public const int NotImplemented = unchecked((int)0x80004001);
    public const int InvalidArg = unchecked((int)0x80070057);
}

/// <summary>Item property ids (PropID.h).</summary>
internal static class Kpid
{
    public const uint Path = 3;
    public const uint Name = 4;
    public const uint IsDir = 6;
    public const uint Size = 7;
    public const uint Attrib = 9;
    public const uint CTime = 10;
    public const uint ATime = 11;
    public const uint MTime = 12;
    public const uint Encrypted = 15;
    public const uint IsAnti = 21;
    public const uint IsAltStream = 63;
}

/// <summary>NArchive::NHandlerPropID: what GetHandlerProperty2 tells about a format.</summary>
internal static class HandlerProp
{
    public const uint Name = 0;
    public const uint ClassId = 1;
    public const uint Extension = 2;
    public const uint Update = 4;
    public const uint Signature = 6;
    public const uint MultiSignature = 7;
    public const uint SignatureOffset = 8;
}

/// <summary>NArchive::NExtract::NOperationResult.</summary>
internal enum ExtractResult
{
    Ok = 0,
    UnsupportedMethod = 1,
    DataError = 2,
    CrcError = 3,
    Unavailable = 4,
    UnexpectedEnd = 5,
    DataAfterEnd = 6,
    IsNotArc = 7,
    HeadersError = 8,
    WrongPassword = 9,
}

internal static class VarType
{
    public const ushort Empty = 0;
    public const ushort I4 = 3;
    public const ushort Bstr = 8;
    public const ushort Bool = 11;
    public const ushort UI1 = 17;
    public const ushort UI2 = 18;
    public const ushort UI4 = 19;
    public const ushort I8 = 20;
    public const ushort UI8 = 21;
    public const ushort FileTime = 64;
}

/// <summary>
/// PROPVARIANT as 7-Zip uses it: a type tag and an 8-byte value (a BSTR pointer for strings). The real struct is
/// 24 bytes on 64-bit Windows (its union holds 16-byte members) and 7-Zip copies all of it.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)]
    public ushort Type;

    /// <summary>7-Zip puts a time's precision here (wReserved1): <see cref="DosPrecision"/> for zip/rar4 DOS times.</summary>
    [FieldOffset(2)]
    public ushort Precision;

    [FieldOffset(8)]
    public long Value;

    [FieldOffset(8)]
    public IntPtr Pointer;

    /// <summary>k_PropVar_TimePrec_DOS: a 2-second local time that 7-Zip turned into UTC.</summary>
    public const ushort DosPrecision = 2;

    public readonly bool IsEmpty => Type == VarType.Empty;

    public readonly bool AsBool() => Type == VarType.Bool && (short)Value != 0;

    public readonly ulong? AsULong() => Type switch
    {
        VarType.UI1 => (byte)Value,
        VarType.UI2 => (ushort)Value,
        VarType.UI4 => (uint)Value,
        VarType.I4 => (ulong)(int)Value,
        VarType.UI8 or VarType.I8 => (ulong)Value,
        _ => null,
    };

    public readonly string? AsString() => Type == VarType.Bstr && Pointer != IntPtr.Zero ? Marshal.PtrToStringBSTR(Pointer) : null;

    /// <summary>A BSTR that carries bytes (class ids, signatures): its length prefix counts bytes.</summary>
    public readonly byte[]? AsBytes()
    {
        if (Type != VarType.Bstr || Pointer == IntPtr.Zero)
        {
            return null;
        }

        var length = Marshal.ReadInt32(Pointer, -4);
        var bytes = new byte[length];
        Marshal.Copy(Pointer, bytes, 0, length);
        return bytes;
    }

    public readonly DateTime? AsFileTime() =>
        Type == VarType.FileTime && Value > 0 ? DateTime.FromFileTimeUtc(Value) : null;

    public void Clear()
    {
        if (Type == VarType.Bstr && Pointer != IntPtr.Zero)
        {
            Marshal.FreeBSTR(Pointer);
        }

        Type = VarType.Empty;
        Value = 0;
    }

    public static PropVariant FromBool(bool value) => new() { Type = VarType.Bool, Value = value ? -1 : 0 };

    public static PropVariant FromUInt32(uint value) => new() { Type = VarType.UI4, Value = value };

    public static PropVariant FromUInt64(ulong value) => new() { Type = VarType.UI8, Value = (long)value };

    public static PropVariant FromFileTime(DateTime utc) => new() { Type = VarType.FileTime, Value = utc.ToFileTimeUtc() };

    /// <summary>The receiver (7-Zip) frees the BSTR.</summary>
    public static PropVariant FromString(string value) => new() { Type = VarType.Bstr, Pointer = Marshal.StringToBSTR(value) };
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000300010000")]
internal unsafe partial interface ISequentialInStream
{
    [PreserveSig]
    int Read(byte* data, uint size, uint* processedSize);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000300020000")]
internal unsafe partial interface ISequentialOutStream
{
    [PreserveSig]
    int Write(byte* data, uint size, uint* processedSize);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000300030000")]
internal unsafe partial interface IInStream : ISequentialInStream
{
    [PreserveSig]
    int Seek(long offset, uint seekOrigin, ulong* newPosition);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000300040000")]
internal unsafe partial interface IOutStream : ISequentialOutStream
{
    [PreserveSig]
    int Seek(long offset, uint seekOrigin, ulong* newPosition);

    [PreserveSig]
    int SetSize(ulong newSize);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000300060000")]
internal unsafe partial interface IStreamGetSize
{
    [PreserveSig]
    int GetSize(ulong* size);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000000050000")]
internal unsafe partial interface IProgress
{
    [PreserveSig]
    int SetTotal(ulong total);

    [PreserveSig]
    int SetCompleted(ulong* completeValue);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000600100000")]
internal unsafe partial interface IArchiveOpenCallback
{
    [PreserveSig]
    int SetTotal(ulong* files, ulong* bytes);

    [PreserveSig]
    int SetCompleted(ulong* files, ulong* bytes);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000600300000")]
internal unsafe partial interface IArchiveOpenVolumeCallback
{
    [PreserveSig]
    int GetProperty(uint propId, PropVariant* value);

    [PreserveSig]
    int GetStream(char* name, out IInStream? inStream);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000500100000")]
internal unsafe partial interface ICryptoGetTextPassword
{
    [PreserveSig]
    int CryptoGetTextPassword(IntPtr* password);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000500110000")]
internal unsafe partial interface ICryptoGetTextPassword2
{
    [PreserveSig]
    int CryptoGetTextPassword2(int* passwordIsDefined, IntPtr* password);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000600200000")]
internal unsafe partial interface IArchiveExtractCallback : IProgress
{
    [PreserveSig]
    int GetStream(uint index, out ISequentialOutStream? outStream, int askExtractMode);

    [PreserveSig]
    int PrepareOperation(int askExtractMode);

    [PreserveSig]
    int SetOperationResult(int opRes);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000600600000")]
internal unsafe partial interface IInArchive
{
    [PreserveSig]
    int Open(IInStream stream, ulong* maxCheckStartPosition, IArchiveOpenCallback? openCallback);

    [PreserveSig]
    int Close();

    [PreserveSig]
    int GetNumberOfItems(uint* numItems);

    [PreserveSig]
    int GetProperty(uint index, uint propId, PropVariant* value);

    [PreserveSig]
    int Extract(uint* indices, uint numItems, int testMode, IArchiveExtractCallback extractCallback);

    [PreserveSig]
    int GetArchiveProperty(uint propId, PropVariant* value);

    [PreserveSig]
    int GetNumberOfProperties(uint* numProps);

    [PreserveSig]
    int GetPropertyInfo(uint index, IntPtr* name, uint* propId, ushort* varType);

    [PreserveSig]
    int GetNumberOfArchiveProperties(uint* numProps);

    [PreserveSig]
    int GetArchivePropertyInfo(uint index, IntPtr* name, uint* propId, ushort* varType);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000600800000")]
internal unsafe partial interface IArchiveUpdateCallback : IProgress
{
    [PreserveSig]
    int GetUpdateItemInfo(uint index, int* newData, int* newProps, uint* indexInArchive);

    [PreserveSig]
    int GetProperty(uint index, uint propId, PropVariant* value);

    [PreserveSig]
    int GetStream(uint index, out ISequentialInStream? inStream);

    [PreserveSig]
    int SetOperationResult(int operationResult);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000600A00000")]
internal unsafe partial interface IOutArchive
{
    [PreserveSig]
    int UpdateItems(ISequentialOutStream outStream, uint numItems, IArchiveUpdateCallback updateCallback);

    [PreserveSig]
    int GetFileTimeType(uint* type);
}

[GeneratedComInterface]
[Guid("23170F69-40C1-278A-0000-000600030000")]
internal unsafe partial interface ISetProperties
{
    [PreserveSig]
    int SetProperties(char** names, PropVariant* values, uint numProps);
}
