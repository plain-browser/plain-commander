using System.Security.Cryptography;

namespace PlainCommander.Core.Crypto;

/// <summary>Where an entry's sealed blocks go, one after another.</summary>
internal interface IPceBlockSink
{
    void Write(ReadOnlySpan<byte> sealedBlock);
}

/// <summary>An entry's stored bytes (its sealed blocks end to end), read by position in that stream.</summary>
internal interface IPceStoredBytes : IDisposable
{
    /// <summary>Fills <paramref name="target"/> from <paramref name="start"/>; throws when the bytes are not there.</summary>
    void Read(long start, Span<byte> target);
}

/// <summary>
/// Write-only: what is written goes to the sink (a file at a rising offset, or data objects) as one entry's blocks. A block is sealed
/// when the next byte arrives, so the block sealed by <see cref="Complete"/> carries the "last" flag; an entry
/// without content has no blocks at all.
/// </summary>
internal sealed class PceBlockWriter : Stream
{
    private readonly IPceBlockSink _sink;
    private readonly PceCipher _cipher;
    private readonly byte[] _plain;
    private readonly byte[] _sealed;
    private readonly byte[] _nonce = new byte[PceFormat.NonceSize];
    private int _filled;
    private long _block;
    private bool _completed;

    public PceBlockWriter(IPceStorage storage, long offset, PceCipherKind kind, byte[] key, int chunkSize)
        : this(new StorageSink(storage, offset), kind, key, chunkSize)
    {
    }

    public PceBlockWriter(IPceBlockSink sink, PceCipherKind kind, byte[] key, int chunkSize)
    {
        _sink = sink;
        _cipher = new PceCipher(kind, key);
        _plain = new byte[chunkSize];
        _sealed = new byte[chunkSize + PceFormat.TagSize];
    }

    /// <summary>Bytes handed to the sink so far.</summary>
    public long Stored { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_completed;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_completed, this);
        while (buffer.Length > 0)
        {
            if (_filled == _plain.Length)
            {
                Seal(last: false);
            }

            var take = Math.Min(buffer.Length, _plain.Length - _filled);
            buffer[..take].CopyTo(_plain.AsSpan(_filled));
            _filled += take;
            buffer = buffer[take..];
        }
    }

    public void Complete()
    {
        if (_completed)
        {
            return;
        }

        if (_filled > 0)
        {
            Seal(last: true);
        }

        _completed = true;
    }

    private void Seal(bool last)
    {
        PceContainerFormat.FillNonce(_nonce, _block, last);
        _cipher.Encrypt(_nonce, _plain.AsSpan(0, _filled), _sealed.AsSpan(0, _filled), _sealed.AsSpan(_filled, PceFormat.TagSize), default);
        _sink.Write(_sealed.AsSpan(0, _filled + PceFormat.TagSize));
        Stored += _filled + PceFormat.TagSize;
        _block++;
        _filled = 0;
    }

    public override void Flush()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cipher.Dispose();
            CryptographicOperations.ZeroMemory(_plain);
        }

        base.Dispose(disposing);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private sealed class StorageSink(IPceStorage storage, long offset) : IPceBlockSink
    {
        private long _at = offset;

        public void Write(ReadOnlySpan<byte> sealedBlock)
        {
            storage.Write(sealedBlock, _at);
            _at += sealedBlock.Length;
        }
    }
}

/// <summary>
/// An entry's extents in a container file. Stops working when the container is changed under it, because the
/// extents may have moved.
/// </summary>
internal sealed class PceFileExtents : IPceStoredBytes
{
    private readonly PceContainer _container;
    private readonly IPceStorage _storage;
    private readonly int _version;
    private readonly long[] _offsets;
    private readonly long[] _starts; // position of each extent in the entry's stored stream

    public PceFileExtents(PceContainer container, IPceStorage storage, int version, PceEntry entry)
    {
        _container = container;
        _storage = storage;
        _version = version;
        _offsets = new long[entry.Extents.Count];
        _starts = new long[entry.Extents.Count + 1];
        for (var i = 0; i < entry.Extents.Count; i++)
        {
            _offsets[i] = entry.Extents[i].Offset;
            _starts[i + 1] = _starts[i] + entry.Extents[i].Length;
        }
    }

    public void Read(long start, Span<byte> target)
    {
        if (_container.Version != _version)
        {
            throw new IOException("The encrypted file was changed while this entry was being read.");
        }

        var extent = Array.BinarySearch(_starts, 0, _offsets.Length, start);
        if (extent < 0)
        {
            extent = ~extent - 1;
        }

        while (target.Length > 0)
        {
            while (_starts[extent + 1] <= start)
            {
                extent++; // also steps over empty extents
            }

            var within = start - _starts[extent];
            var take = (int)Math.Min(target.Length, _starts[extent + 1] - start);
            if (_storage.Read(target[..take], _offsets[extent] + within) != take)
            {
                throw new PceFormatException("The encrypted file is cut short.");
            }

            target = target[take..];
            start += take;
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Read-only and seekable: one entry's blocks, decrypted on demand (one block in memory). A block that does not
/// authenticate raises <see cref="PceFormatException"/>. Where the stored bytes lie is the business of
/// <see cref="IPceStoredBytes"/>.
/// </summary>
internal sealed class PceBlockReader : Stream
{
    private readonly IPceStoredBytes _bytes;
    private readonly long _stored;
    private readonly int _chunk;
    private readonly long _blocks;
    private readonly PceCipher _cipher;
    private readonly byte[] _plain;
    private readonly byte[] _sealed;
    private readonly byte[] _nonce = new byte[PceFormat.NonceSize];
    private long _loaded = -1;
    private int _loadedLength;
    private long _position;

    public PceBlockReader(IPceStoredBytes bytes, PceCipherKind kind, PceEntry entry, int chunkSize)
    {
        _bytes = bytes;
        _chunk = chunkSize;
        _stored = entry.StoredSize;
        var blockSize = (long)chunkSize + PceFormat.TagSize;
        _blocks = (_stored + blockSize - 1) / blockSize;
        if (_blocks > 0 && _stored - (_blocks - 1) * blockSize < PceFormat.TagSize)
        {
            throw new PceFormatException($"Entry \"{entry.Path}\" is damaged (a block is cut short).");
        }

        Length = _stored - _blocks * PceFormat.TagSize;
        _cipher = new PceCipher(kind, entry.Key);
        _plain = new byte[chunkSize];
        _sealed = new byte[chunkSize + PceFormat.TagSize];
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set => _position = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var total = 0;
        while (buffer.Length > 0 && _position < Length)
        {
            var block = _position / _chunk;
            if (block != _loaded)
            {
                Load(block);
            }

            var at = (int)(_position - block * _chunk);
            var take = Math.Min(buffer.Length, _loadedLength - at);
            _plain.AsSpan(at, take).CopyTo(buffer);
            buffer = buffer[take..];
            _position += take;
            total += take;
        }

        return total;
    }

    private void Load(long block)
    {
        var blockSize = (long)_chunk + PceFormat.TagSize;
        var start = block * blockSize;
        var length = (int)Math.Min(blockSize, _stored - start);
        _bytes.Read(start, _sealed.AsSpan(0, length));
        var plainLength = length - PceFormat.TagSize;
        PceContainerFormat.FillNonce(_nonce, block, last: block == _blocks - 1);
        try
        {
            _cipher.Decrypt(_nonce, _sealed.AsSpan(0, plainLength), _sealed.AsSpan(plainLength, PceFormat.TagSize), _plain.AsSpan(0, plainLength), default);
        }
        catch (CryptographicException ex)
        {
            _loaded = -1;
            throw new PceFormatException("The encrypted file is damaged (a block does not authenticate).", ex);
        }

        _loaded = block;
        _loadedLength = plainLength;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => Length + offset,
        };
        return _position;
    }

    public override void Flush()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cipher.Dispose();
            _bytes.Dispose();
            CryptographicOperations.ZeroMemory(_plain);
        }

        base.Dispose(disposing);
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
