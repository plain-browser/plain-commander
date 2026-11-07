using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PlainCommander.Core.Crypto;

/// <summary>
/// Read-only stream over a .pce file: the constructor reads the header, derives the key and opens block 0, so
/// a wrong password is known before any content is read (<see cref="PceWrongPasswordException"/>). Reading then
/// yields the content block by block; a block that fails to authenticate, a missing last block or bytes after it
/// raise <see cref="PceFormatException"/>. The content may still be Deflate-compressed or a TAR: see <see cref="Header"/>.
/// </summary>
public sealed class PceDecryptStream : Stream
{
    private readonly Stream _input;
    private readonly string _file;
    private readonly byte[] _header;
    private readonly byte[] _plain;
    private readonly byte[] _cipherBlock;
    private readonly byte[] _nonce = new byte[PceFormat.NonceSize];
    private readonly byte[] _key;
    private PceCipher? _cipher;
    private int _available;
    private int _at;
    private uint _counter;
    private bool _sawLast;
    private long _position;

    public PceDecryptStream(Stream input, string password, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrEmpty(password);
        _input = input;
        _file = fileName ?? "file";
        _header = new byte[PceFormat.HeaderSize];
        if (!ReadFully(_header))
        {
            throw new PceFormatException("Not a Plain Commander encrypted file (too short).");
        }

        Header = PceFormat.ReadHeader(_header);
        _plain = new byte[Header.ChunkSize];
        _cipherBlock = new byte[Header.ChunkSize + PceFormat.TagSize];
        if (!ReadSealed(out var metaLength))
        {
            throw new PceFormatException("The metadata block is missing.");
        }

        // Block 0 decides the password; an older file may need another Unicode spelling of it (PasswordKdf).
        _key = PasswordKdf.DeriveMatching(password, Header, key =>
        {
            var cipher = new PceCipher(Header.Cipher, key);
            _cipher = cipher;
            if (Open(metaLength))
            {
                return true;
            }

            _cipher = null;
            cipher.Dispose();
            return false;
        }) ?? throw new PceWrongPasswordException(_file);

        Metadata = PceFormat.DecodeMetadata(_plain.AsSpan(0, metaLength));
        _available = 0;
    }

    public PceHeader Header { get; }

    public PceMetadata Metadata { get; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        while (_at == _available)
        {
            if (_sawLast)
            {
                return 0;
            }

            if (!ReadBlock(out _available))
            {
                throw new PceFormatException($"{_file} is cut short: the last block is missing.");
            }

            _at = 0;
        }

        var take = Math.Min(buffer.Length, _available - _at);
        _plain.AsSpan(_at, take).CopyTo(buffer);
        _at += take;
        _position += take;
        return take;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>Reads and authenticates the next block into <see cref="_plain"/>; false at a clean end of input.</summary>
    private bool ReadBlock(out int length)
    {
        if (!ReadSealed(out length))
        {
            return false;
        }

        return Open(length) ? true : throw new PceFormatException($"{_file}: block {_counter} is damaged or was tampered with.");
    }

    /// <summary>Reads the next sealed block into <see cref="_cipherBlock"/>; false at a clean end of input.</summary>
    private bool ReadSealed(out int length)
    {
        length = 0;
        Span<byte> lengthBytes = stackalloc byte[4];
        if (!ReadFully(lengthBytes))
        {
            return false;
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
        if (size > (uint)Header.ChunkSize)
        {
            throw new PceFormatException($"{_file}: block {_counter} is larger than the header allows.");
        }

        length = (int)size;
        if (!ReadFully(_cipherBlock.AsSpan(0, length + PceFormat.TagSize)))
        {
            throw new PceFormatException($"{_file} is cut short inside block {_counter}.");
        }

        return true;
    }

    /// <summary>Authenticates the sealed block into <see cref="_plain"/> and moves to the next counter; false when the tag fails.</summary>
    private bool Open(int length)
    {
        // Try the counter as a middle block first, then as the last one; the tag decides which it was.
        if (!TryOpen(_counter, length))
        {
            if (!TryOpen(_counter | PceFormat.LastBlockFlag, length))
            {
                return false;
            }

            _sawLast = true;
            if (_input.ReadByte() >= 0)
            {
                throw new PceFormatException($"{_file} has data after its last block.");
            }
        }

        _counter++;
        return true;
    }

    private bool TryOpen(uint counter, int length)
    {
        PceFormat.FillNonce(_nonce, Header.NoncePrefix, counter);
        try
        {
            _cipher!.Decrypt(_nonce, _cipherBlock.AsSpan(0, length), _cipherBlock.AsSpan(length, PceFormat.TagSize), _plain.AsSpan(0, length), _header);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private bool ReadFully(Span<byte> target)
    {
        var got = 0;
        while (got < target.Length)
        {
            var n = _input.Read(target[got..]);
            if (n <= 0)
            {
                return got == 0 ? false : throw new PceFormatException($"{_file} is cut short.");
            }

            got += n;
        }

        return true;
    }

    public override void Flush()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cipher?.Dispose();
            _cipher = null;
            CryptographicOperations.ZeroMemory(_key);
            CryptographicOperations.ZeroMemory(_plain);
            _input.Dispose();
        }

        base.Dispose(disposing);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
