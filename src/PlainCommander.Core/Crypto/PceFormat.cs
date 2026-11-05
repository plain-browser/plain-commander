using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PlainCommander.Core.Crypto;

/// <summary>
/// The ".pce" container (Plain Commander Encrypted): a fixed header in the clear, then a chain of authenticated
/// blocks. Every block is AES-256-GCM (ChaCha20-Poly1305 where the CPU has no AES instructions) over up to
/// <see cref="PceHeader.ChunkSize"/> bytes with the header as associated data; the nonce is a random per-file
/// prefix plus the block counter, and the last block carries a flag, so a truncated, reordered or extended file
/// is rejected. Block 0 holds the metadata (original name, size, time, attributes); the data starts at block 1.
/// The key comes from the password through Argon2id (parameters stored in the header) or PBKDF2-SHA512.
/// </summary>
public static class PceFormat
{
    public const string Extension = ".pce";

    /// <summary>Bytes of the fixed header, which is also the associated data of every block.</summary>
    public const int HeaderSize = 42;

    public const int SaltSize = 16;
    public const int NoncePrefixSize = 8;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int KeySize = 32;

    /// <summary>Set on the counter of the last block.</summary>
    public const uint LastBlockFlag = 0x8000_0000u;

    private static ReadOnlySpan<byte> Magic => "PCE1"u8;

    public static bool IsPceName(string name) => name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    internal static void WriteHeader(Span<byte> buffer, PceHeader header)
    {
        if (buffer.Length < HeaderSize)
        {
            throw new ArgumentException("Header buffer too small.", nameof(buffer));
        }

        Magic.CopyTo(buffer);
        buffer[4] = (byte)header.Flags;
        buffer[5] = (byte)header.Kdf;
        buffer[6] = (byte)header.Cipher;
        buffer[7] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[8..], header.KdfCost);
        buffer[12] = header.KdfTime;
        buffer[13] = header.KdfParallelism;
        header.Salt.CopyTo(buffer[14..]);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[30..], (uint)header.ChunkSize);
        header.NoncePrefix.CopyTo(buffer[34..]);
    }

    internal static PceHeader ReadHeader(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < HeaderSize || !buffer[..4].SequenceEqual(Magic))
        {
            throw new PceFormatException("Not a Plain Commander encrypted file.");
        }

        var kdf = (PceKdf)buffer[5];
        var cipher = (PceCipherKind)buffer[6];
        if (!Enum.IsDefined(kdf) || !Enum.IsDefined(cipher))
        {
            throw new PceFormatException("The file was written by a newer version: unknown key derivation or cipher.");
        }

        var chunk = BinaryPrimitives.ReadUInt32LittleEndian(buffer[30..]);
        if (chunk is 0 or > 64 * 1024 * 1024)
        {
            throw new PceFormatException("Invalid block size in the header.");
        }

        return new PceHeader(
            (PceLayout)buffer[4],
            kdf,
            cipher,
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]),
            buffer[12],
            buffer[13],
            buffer.Slice(14, SaltSize).ToArray(),
            (int)chunk,
            buffer.Slice(34, NoncePrefixSize).ToArray());
    }

    /// <summary>The nonce of block <paramref name="counter"/>: the file's prefix then the counter (with the last flag).</summary>
    internal static void FillNonce(Span<byte> nonce, ReadOnlySpan<byte> prefix, uint counter)
    {
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce[NoncePrefixSize..], counter);
    }

    internal static byte[] EncodeMetadata(PceMetadata metadata)
    {
        var name = Encoding.UTF8.GetBytes(metadata.Name);
        if (name.Length > ushort.MaxValue)
        {
            throw new ArgumentException("Name too long.", nameof(metadata));
        }

        var buffer = new byte[1 + 2 + name.Length + 8 + 8 + 4];
        buffer[0] = 1; // metadata version
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(1), (ushort)name.Length);
        name.CopyTo(buffer, 3);
        var at = 3 + name.Length;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(at), metadata.Size);
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(at + 8), metadata.ModifiedUtc.Ticks);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(at + 16), (uint)metadata.Attributes);
        return buffer;
    }

    internal static PceMetadata DecodeMetadata(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 3 || buffer[0] != 1)
        {
            throw new PceFormatException("Unknown metadata version.");
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(buffer[1..]);
        if (buffer.Length < 3 + length + 20)
        {
            throw new PceFormatException("Metadata block too short.");
        }

        var name = Encoding.UTF8.GetString(buffer.Slice(3, length));
        var at = 3 + length;
        return new PceMetadata(
            name,
            BinaryPrimitives.ReadInt64LittleEndian(buffer[at..]),
            new DateTime(BinaryPrimitives.ReadInt64LittleEndian(buffer[(at + 8)..]), DateTimeKind.Utc),
            (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(buffer[(at + 16)..]));
    }

    internal static PceHeader NewHeader(PceOptions options, PceLayout flags)
    {
        var cipher = options.Cipher ?? (AesGcm.IsSupported ? PceCipherKind.AesGcm : PceCipherKind.ChaCha20Poly1305);
        return new PceHeader(
            flags,
            options.Kdf,
            cipher,
            options.Kdf == PceKdf.Argon2id ? (uint)options.Argon2MemoryKiB : (uint)options.Pbkdf2Iterations,
            options.Kdf == PceKdf.Argon2id ? options.Argon2Time : (byte)0,
            options.Kdf == PceKdf.Argon2id ? options.Argon2Parallelism : (byte)0,
            RandomNumberGenerator.GetBytes(SaltSize),
            options.ChunkSize,
            RandomNumberGenerator.GetBytes(NoncePrefixSize));
    }
}

[Flags]
public enum PceLayout : byte
{
    None = 0,

    /// <summary>The content is a TAR of a folder, not a single file.</summary>
    Folder = 1,

    /// <summary>The content is Deflate-compressed before encryption (files written before zstd; still read).</summary>
    Deflate = 2,

    /// <summary>The content is zstd-compressed before encryption.</summary>
    Zstd = 4,
}

/// <summary>Whether to compress before encrypting.</summary>
public enum PceCompression
{
    None,

    /// <summary>Compress when a sample of the first megabyte shrinks by more than a few percent.</summary>
    Auto,

    Always,
}

public enum PceKdf : byte
{
    Argon2id = 0,

    /// <summary>PBKDF2-SHA512: weaker against GPUs, kept for environments without Argon2.</summary>
    Pbkdf2Sha512 = 1,
}

public enum PceCipherKind : byte
{
    AesGcm = 0,
    ChaCha20Poly1305 = 1,
}

/// <param name="KdfCost">Argon2: memory in KiB; PBKDF2: iterations.</param>
public sealed record PceHeader(
    PceLayout Flags,
    PceKdf Kdf,
    PceCipherKind Cipher,
    uint KdfCost,
    byte KdfTime,
    byte KdfParallelism,
    byte[] Salt,
    int ChunkSize,
    byte[] NoncePrefix)
{
    public bool IsFolder => (Flags & PceLayout.Folder) != 0;

    public bool IsCompressed => (Flags & (PceLayout.Deflate | PceLayout.Zstd)) != 0;

    internal byte[] ToBytes()
    {
        var bytes = new byte[PceFormat.HeaderSize];
        PceFormat.WriteHeader(bytes, this);
        return bytes;
    }
}

/// <param name="Size">Bytes of the content; -1 when unknown at the start (a folder streamed as TAR).</param>
public sealed record PceMetadata(string Name, long Size, DateTime ModifiedUtc, FileAttributes Attributes);

/// <summary>How a new file is written. Defaults: Argon2id 64 MiB / 3 passes / 4 lanes, 1 MiB blocks, zstd level 3.</summary>
public sealed record PceOptions
{
    public static PceOptions Default { get; } = new();

    /// <summary>Cheap key derivation for tests.</summary>
    public static PceOptions Fast { get; } = new() { Argon2MemoryKiB = 8 * 1024, Argon2Time = 1, Argon2Parallelism = 1, ChunkSize = 64 * 1024 };

    public PceKdf Kdf { get; init; } = PceKdf.Argon2id;

    public int Argon2MemoryKiB { get; init; } = 64 * 1024;

    public byte Argon2Time { get; init; } = 3;

    public byte Argon2Parallelism { get; init; } = 4;

    public int Pbkdf2Iterations { get; init; } = 600_000;

    public int ChunkSize { get; init; } = 1024 * 1024;

    /// <summary>zstd level: 1 is fastest, 3 the usual balance, 19 slow and tight.</summary>
    public int ZstdLevel { get; init; } = 3;

    /// <summary>Auto mode compresses when the sample shrinks below this fraction of its size.</summary>
    public double AutoThreshold { get; init; } = 0.95;

    /// <summary>Null picks AES-GCM where the CPU accelerates it, ChaCha20-Poly1305 otherwise.</summary>
    public PceCipherKind? Cipher { get; init; }
}

/// <summary>The file is not a .pce file, or it was damaged, cut short, extended or tampered with.</summary>
public class PceFormatException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>Block 0 did not authenticate: the password is wrong (or the file is not what it claims).</summary>
public sealed class PceWrongPasswordException(string file) : IOException($"Wrong password for {file}.")
{
    public string File { get; } = file;
}
