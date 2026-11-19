using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PlainCommander.Core.Crypto;

/// <summary>
/// The second .pce layout (magic "PCE2"): a container of separately encrypted entries with one encrypted index.
/// <code>
/// header   94 B, in the clear: magic, flags, KDF and cipher, KDF parameters, salt, block size,
///          then the master key wrapped with the password key (the bytes before it are its associated data)
/// data     entry streams: blocks of up to ChunkSize bytes, each sealed with the entry's own key,
///          nonce = block number within the entry (u64) + "last" flag (u32); a stream lies in one or more extents
/// index    one AEAD message, key = HKDF(master key, salt from the footer), associated data = the footer;
///          per entry: path, kind, size, time, attributes, entry key, compression, extents; padded to 4 KiB
/// header   a second copy of the 94 B header, used when the first does not open the file (a password change
///          writes this copy first, so a header torn by a crash never costs the master key)
/// footer   48 B, the last bytes of the file: index offset and length, generation, index salt, "PCE2END\0"
/// </code>
/// Entry blocks do not depend on where they lie, so deleting an entry moves ciphertext from the end of the data
/// into the hole and cuts the file: see <see cref="PceContainer"/>. Design: docs/szyfrowanie.md.
/// </summary>
internal static class PceContainerFormat
{
    public const int HeaderSize = 94;
    public const int WrappedAt = 46;
    public const int HeaderAadSize = 34;
    public const int FooterSize = 48;
    public const int IndexPadding = 4096;

    /// <summary>An index is one message in memory; anything near this size is damage, not a real container.</summary>
    public const long MaxIndexSize = 1L << 30;

    /// <summary>Extents are (offset, length) in the file.</summary>
    private const byte IndexVersion = 1;

    /// <summary>Extents are (data object, offset, length): the index of a container kept as separate objects.</summary>
    private const byte SplitIndexVersion = 2;

    public static ReadOnlySpan<byte> Magic => "PCE2"u8;

    public static ReadOnlySpan<byte> FooterMagic => "PCE2END\0"u8;

    private static ReadOnlySpan<byte> IndexInfo => "PCE2 index"u8;

    public static byte[] WriteHeader(PceContainerHeader header)
    {
        var buffer = new byte[HeaderSize];
        Magic.CopyTo(buffer);
        buffer[4] = (byte)header.Flags;
        buffer[5] = (byte)header.Kdf;
        buffer[6] = (byte)header.Cipher;
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), header.KdfCost);
        buffer[12] = header.KdfTime;
        buffer[13] = header.KdfParallelism;
        header.Salt.CopyTo(buffer, 14);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(30), (uint)header.ChunkSize);
        header.WrapNonce.CopyTo(buffer, HeaderAadSize);
        header.WrappedKey.CopyTo(buffer, WrappedAt);
        return buffer;
    }

    /// <param name="split">Whether the header is expected to be the "head" object of a container kept as objects.</param>
    public static PceContainerHeader ReadHeader(ReadOnlySpan<byte> buffer, bool split = false)
    {
        if (buffer.Length < HeaderSize || !buffer[..4].SequenceEqual(Magic))
        {
            throw new PceFormatException("Not a Plain Commander encrypted file.");
        }

        var flags = (PceLayout)buffer[4];
        var kdf = (PceKdf)buffer[5];
        var cipher = (PceCipherKind)buffer[6];
        if ((flags & ~(PceLayout.NfcPassword | PceLayout.Split)) != 0 || buffer[7] != 0 || !Enum.IsDefined(kdf) || !Enum.IsDefined(cipher))
        {
            throw new PceFormatException("The file was written by a newer version.");
        }

        if ((flags & PceLayout.Split) != 0 != split)
        {
            throw new PceFormatException(split
                ? "This is the header of a container file, not of a container stored as separate objects."
                : "This is one part of a container stored as separate objects, not a container file.");
        }

        var chunk = BinaryPrimitives.ReadUInt32LittleEndian(buffer[30..]);
        if (chunk is 0 or > 64 * 1024 * 1024)
        {
            throw new PceFormatException("Invalid block size in the header.");
        }

        return new PceContainerHeader(
            flags,
            kdf,
            cipher,
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]),
            buffer[12],
            buffer[13],
            buffer.Slice(14, PceFormat.SaltSize).ToArray(),
            (int)chunk,
            buffer.Slice(HeaderAadSize, PceFormat.NonceSize).ToArray(),
            buffer.Slice(WrappedAt, PceFormat.KeySize + PceFormat.TagSize).ToArray());
    }

    /// <summary>A header for a new container (or a new password): fresh salt, the master key wrapped with the password key.</summary>
    public static PceContainerHeader NewHeader(PceOptions options, string password, ReadOnlySpan<byte> masterKey, bool split = false) =>
        Wrap(
            new PceContainerHeader(
                split ? PceLayout.NfcPassword | PceLayout.Split : PceLayout.NfcPassword,
                options.Kdf,
                options.Cipher ?? (AesGcm.IsSupported ? PceCipherKind.AesGcm : PceCipherKind.ChaCha20Poly1305),
                options.Kdf == PceKdf.Argon2id ? (uint)options.Argon2MemoryKiB : (uint)options.Pbkdf2Iterations,
                options.Kdf == PceKdf.Argon2id ? options.Argon2Time : (byte)0,
                options.Kdf == PceKdf.Argon2id ? options.Argon2Parallelism : (byte)0,
                [],
                options.ChunkSize,
                [],
                []),
            password,
            masterKey);

    /// <summary>The same parameters with a fresh salt and the master key wrapped for <paramref name="password"/>.</summary>
    public static PceContainerHeader Wrap(PceContainerHeader header, string password, ReadOnlySpan<byte> masterKey)
    {
        var wrapped = header with
        {
            Salt = RandomNumberGenerator.GetBytes(PceFormat.SaltSize),
            WrapNonce = RandomNumberGenerator.GetBytes(PceFormat.NonceSize),
            WrappedKey = new byte[PceFormat.KeySize + PceFormat.TagSize],
        };
        var aad = WriteHeader(wrapped).AsSpan(0, HeaderAadSize);
        var key = PasswordKdf.Derive(password, wrapped.ForKdf());
        try
        {
            using var cipher = new PceCipher(wrapped.Cipher, key);
            cipher.Encrypt(wrapped.WrapNonce, masterKey, wrapped.WrappedKey.AsSpan(0, PceFormat.KeySize), wrapped.WrappedKey.AsSpan(PceFormat.KeySize), aad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return wrapped;
    }

    /// <summary>The master key, or null when the password (or any byte of the header) is wrong.</summary>
    public static byte[]? Unwrap(PceContainerHeader header, string password)
    {
        var aad = WriteHeader(header).AsSpan(0, HeaderAadSize);
        var key = PasswordKdf.Derive(password, header.ForKdf());
        var master = new byte[PceFormat.KeySize];
        try
        {
            using var cipher = new PceCipher(header.Cipher, key);
            cipher.Decrypt(header.WrapNonce, header.WrappedKey.AsSpan(0, PceFormat.KeySize), header.WrappedKey.AsSpan(PceFormat.KeySize), master, aad);
            return master;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(master);
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static void WriteFooter(Span<byte> buffer, long indexOffset, long indexLength, ulong generation, ReadOnlySpan<byte> salt)
    {
        BinaryPrimitives.WriteInt64LittleEndian(buffer, indexOffset);
        BinaryPrimitives.WriteInt64LittleEndian(buffer[8..], indexLength);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer[16..], generation);
        salt.CopyTo(buffer[24..]);
        FooterMagic.CopyTo(buffer[40..]);
    }

    /// <summary>
    /// The index as it goes to disk: ciphertext, tag, the header's second copy, footer. Every call draws a new salt,
    /// so no index key is used twice.
    /// </summary>
    public static byte[] SealIndex(PceContainerHeader header, ReadOnlySpan<byte> masterKey, IReadOnlyList<PceEntry> entries, long offset, ulong generation)
    {
        var kind = header.Cipher;
        var plain = EncodeIndex(entries, header.IsSplit);
        var sealedIndex = new byte[plain.Length + PceFormat.TagSize + HeaderSize + FooterSize];
        WriteHeader(header).CopyTo(sealedIndex.AsSpan(plain.Length + PceFormat.TagSize));
        var footer = sealedIndex.AsSpan(plain.Length + PceFormat.TagSize + HeaderSize);
        Span<byte> salt = stackalloc byte[PceFormat.SaltSize];
        RandomNumberGenerator.Fill(salt);
        WriteFooter(footer, offset, plain.Length + PceFormat.TagSize, generation, salt);
        var key = IndexKey(masterKey, salt);
        try
        {
            using var cipher = new PceCipher(kind, key);
            Span<byte> nonce = stackalloc byte[PceFormat.NonceSize]; // zero: the key is used for this one message
            nonce.Clear();
            cipher.Encrypt(nonce, plain, sealedIndex.AsSpan(0, plain.Length), sealedIndex.AsSpan(plain.Length, PceFormat.TagSize), footer);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }

        return sealedIndex;
    }

    /// <summary>Bytes <see cref="SealIndex"/> produces for these entries.</summary>
    public static long SealedIndexSize(IReadOnlyList<PceEntry> entries, bool split = false) =>
        PaddedIndexSize(entries, split) + PceFormat.TagSize + HeaderSize + FooterSize;

    /// <summary>The entries of a sealed index, or null when it does not authenticate with this key and footer.</summary>
    /// <param name="split">Whether the index belongs to a container kept as objects (its extents name data objects).</param>
    public static List<PceEntry>? OpenIndex(PceCipherKind kind, ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> sealedIndex, ReadOnlySpan<byte> footer, bool split = false)
    {
        if (sealedIndex.Length < PceFormat.TagSize || footer.Length != FooterSize)
        {
            return null;
        }

        var length = sealedIndex.Length - PceFormat.TagSize;
        var plain = new byte[length];
        var key = IndexKey(masterKey, footer.Slice(24, PceFormat.SaltSize));
        try
        {
            using var cipher = new PceCipher(kind, key);
            Span<byte> nonce = stackalloc byte[PceFormat.NonceSize];
            nonce.Clear();
            cipher.Decrypt(nonce, sealedIndex[..length], sealedIndex[length..], plain, footer);
            return DecodeIndex(plain, split);
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public static void FillNonce(Span<byte> nonce, long block, bool last)
    {
        BinaryPrimitives.WriteInt64LittleEndian(nonce, block);
        BinaryPrimitives.WriteUInt32LittleEndian(nonce[8..], last ? 1u : 0u);
    }

    private static byte[] IndexKey(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> salt)
    {
        var key = new byte[PceFormat.KeySize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, key, salt, IndexInfo);
        return key;
    }

    private static int PaddedIndexSize(IReadOnlyList<PceEntry> entries, bool split)
    {
        long size = 1 + 4;
        long extentSize = split ? PceObjectId.Size + 16 : 16;
        foreach (var entry in entries)
        {
            size += 2 + Encoding.UTF8.GetByteCount(entry.Path) + 1 + 1 + 8 + 8 + 4 + PceFormat.KeySize + 4 + extentSize * entry.Extents.Count;
        }

        size = (size + IndexPadding - 1) / IndexPadding * IndexPadding;
        return size <= MaxIndexSize ? (int)size : throw new IOException("Too many entries for one container.");
    }

    private static byte[] EncodeIndex(IReadOnlyList<PceEntry> entries, bool split)
    {
        var buffer = new byte[PaddedIndexSize(entries, split)];
        var span = buffer.AsSpan();
        span[0] = split ? SplitIndexVersion : IndexVersion;
        BinaryPrimitives.WriteUInt32LittleEndian(span[1..], (uint)entries.Count);
        var at = 5;
        foreach (var entry in entries)
        {
            var name = Encoding.UTF8.GetBytes(entry.Path, span[(at + 2)..]);
            BinaryPrimitives.WriteUInt16LittleEndian(span[at..], (ushort)name);
            at += 2 + name;
            span[at++] = entry.IsDirectory ? (byte)1 : (byte)0;
            span[at++] = entry.Compressed ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt64LittleEndian(span[at..], entry.Size);
            BinaryPrimitives.WriteInt64LittleEndian(span[(at + 8)..], entry.ModifiedUtc.Ticks);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(at + 16)..], (uint)entry.Attributes);
            at += 20;
            entry.Key.CopyTo(span[at..]);
            at += PceFormat.KeySize;
            BinaryPrimitives.WriteUInt32LittleEndian(span[at..], (uint)entry.Extents.Count);
            at += 4;
            foreach (var extent in entry.Extents)
            {
                if (split)
                {
                    extent.Object.Write(span[at..]);
                    at += PceObjectId.Size;
                }

                BinaryPrimitives.WriteInt64LittleEndian(span[at..], extent.Offset);
                BinaryPrimitives.WriteInt64LittleEndian(span[(at + 8)..], extent.Length);
                at += 16;
            }
        }

        return buffer;
    }

    private static List<PceEntry> DecodeIndex(ReadOnlySpan<byte> plain, bool split)
    {
        if (plain.Length < 5 || plain[0] != (split ? SplitIndexVersion : IndexVersion))
        {
            throw new PceFormatException("The file was written by a newer version (unknown index).");
        }

        var extentSize = split ? PceObjectId.Size + 16 : 16;
        var count = BinaryPrimitives.ReadUInt32LittleEndian(plain[1..]);
        var at = 5;
        var entries = new List<PceEntry>((int)Math.Min(count, 1 << 20));
        for (var i = 0u; i < count; i++)
        {
            Need(plain, at, 2);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(plain[at..]);
            at += 2;
            Need(plain, at, nameLength + 22 + PceFormat.KeySize + 4);
            var path = Encoding.UTF8.GetString(plain.Slice(at, nameLength));
            at += nameLength;
            var kind = plain[at++];
            var compression = plain[at++];
            if (kind > 1 || compression > 1)
            {
                throw new PceFormatException("The file was written by a newer version (unknown entry kind).");
            }

            var size = BinaryPrimitives.ReadInt64LittleEndian(plain[at..]);
            var ticks = BinaryPrimitives.ReadInt64LittleEndian(plain[(at + 8)..]);
            var attributes = (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(plain[(at + 16)..]);
            at += 20;
            var key = plain.Slice(at, PceFormat.KeySize).ToArray();
            at += PceFormat.KeySize;
            var extentCount = BinaryPrimitives.ReadUInt32LittleEndian(plain[at..]);
            at += 4;
            if (size < 0 || ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks || extentCount > (uint)((plain.Length - at) / extentSize))
            {
                throw new PceFormatException("Damaged index.");
            }

            var extents = new List<PceExtent>((int)extentCount);
            for (var x = 0u; x < extentCount; x++)
            {
                var id = default(PceObjectId);
                if (split)
                {
                    id = PceObjectId.Read(plain[at..]);
                    at += PceObjectId.Size;
                }

                extents.Add(new PceExtent(BinaryPrimitives.ReadInt64LittleEndian(plain[at..]), BinaryPrimitives.ReadInt64LittleEndian(plain[(at + 8)..])) { Object = id });
                at += 16;
            }

            entries.Add(new PceEntry(path, kind == 1, size, new DateTime(ticks, DateTimeKind.Utc), attributes, compression == 1, key, extents));
        }

        return entries;
    }

    private static void Need(ReadOnlySpan<byte> plain, int at, int bytes)
    {
        if (plain.Length - at < bytes)
        {
            throw new PceFormatException("Damaged index.");
        }
    }
}

/// <param name="KdfCost">Argon2: memory in KiB; PBKDF2: iterations.</param>
/// <param name="WrappedKey">The master key encrypted with the password key, then the tag.</param>
internal sealed record PceContainerHeader(
    PceLayout Flags,
    PceKdf Kdf,
    PceCipherKind Cipher,
    uint KdfCost,
    byte KdfTime,
    byte KdfParallelism,
    byte[] Salt,
    int ChunkSize,
    byte[] WrapNonce,
    byte[] WrappedKey)
{
    /// <summary>The header of a container kept as separate objects.</summary>
    public bool IsSplit => (Flags & PceLayout.Split) != 0;

    /// <summary>The fields the password derivation reads, in the shape it takes.</summary>
    public PceHeader ForKdf() => new(Flags, Kdf, Cipher, KdfCost, KdfTime, KdfParallelism, Salt, ChunkSize, []);
}

/// <summary>A run of an entry's stored bytes: in the file, or in a data object of a container kept as objects.</summary>
internal sealed class PceExtent(long offset, long length)
{
    /// <summary>The data object the bytes lie in; unset in a container file.</summary>
    public PceObjectId Object { get; init; }

    public long Offset { get; set; } = offset;

    public long Length { get; set; } = length;
}

/// <summary>A file or a folder inside a .pce container.</summary>
public sealed class PceEntry
{
    internal PceEntry(string path, bool isDirectory, long size, DateTime modifiedUtc, FileAttributes attributes, bool compressed, byte[] key, List<PceExtent> extents)
    {
        Path = path;
        IsDirectory = isDirectory;
        Size = size;
        ModifiedUtc = modifiedUtc;
        Attributes = attributes;
        Compressed = compressed;
        Key = key;
        Extents = extents;
    }

    /// <summary>Relative path with '/' between segments.</summary>
    public string Path { get; internal set; }

    public bool IsDirectory { get; }

    /// <summary>Bytes of the content as it was before encryption (and compression).</summary>
    public long Size { get; }

    public DateTime ModifiedUtc { get; }

    public FileAttributes Attributes { get; }

    public bool Compressed { get; }

    /// <summary>Bytes the entry takes in the container.</summary>
    public long StoredSize
    {
        get
        {
            long total = 0;
            foreach (var extent in Extents)
            {
                total += extent.Length;
            }

            return total;
        }
    }

    internal byte[] Key { get; }

    internal List<PceExtent> Extents { get; set; }
}

/// <summary>Something to put into a container: where it goes, and for a file, how to read it.</summary>
/// <param name="Path">Relative path inside the container; '/' or '\' between segments.</param>
/// <param name="Open">Opens the content; null for a folder.</param>
public sealed record PceSource(string Path, bool IsDirectory, Func<Stream>? Open, DateTime ModifiedUtc, FileAttributes Attributes)
{
    /// <summary>
    /// A file, or a folder with everything under it, as entries under <paramref name="name"/> (the item's own name
    /// when null). Folders that would otherwise leave no trace (empty ones) get their own entry; links to folders
    /// are skipped, as they would pack their target under another name.
    /// </summary>
    public static IEnumerable<PceSource> FromPath(string path, string? name = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        name ??= System.IO.Path.GetFileName(full);
        return Directory.Exists(full) ? Walk(new DirectoryInfo(full), name) : [FromFile(new FileInfo(full), name)];
    }

    private static PceSource FromFile(FileInfo file, string entryPath) =>
        new(
            entryPath,
            false,
            () => new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan),
            file.LastWriteTimeUtc,
            file.Attributes);

    private static IEnumerable<PceSource> Walk(DirectoryInfo dir, string entryPath)
    {
        var any = false;
        foreach (var child in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
        {
            var childPath = entryPath + "/" + child.Name;
            if (child is DirectoryInfo sub)
            {
                if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                any = true;
                foreach (var source in Walk(sub, childPath))
                {
                    yield return source;
                }
            }
            else
            {
                any = true;
                yield return FromFile((FileInfo)child, childPath);
            }
        }

        if (!any)
        {
            yield return new PceSource(entryPath, true, null, dir.LastWriteTimeUtc, dir.Attributes & ~FileAttributes.Directory);
        }
    }
}
