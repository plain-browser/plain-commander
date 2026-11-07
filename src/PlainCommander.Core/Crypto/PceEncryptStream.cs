using System.Buffers.Binary;
using System.Security.Cryptography;

namespace PlainCommander.Core.Crypto;

/// <summary>
/// Write-only stream: what is written comes out of <c>output</c> as the .pce block chain. The header and the
/// metadata block go out in the constructor; every full chunk is sealed as a block, and <see cref="Complete"/>
/// (or Dispose) seals the rest as the last block, which may be empty. After <see cref="Abort"/> nothing more is
/// sealed, so an interrupted file stays incomplete and is rejected. Memory use is two chunks.
/// </summary>
public sealed class PceEncryptStream : Stream
{
    private readonly Stream _output;
    private readonly byte[] _header;
    private readonly byte[] _noncePrefix;
    private readonly byte[] _plain;
    private readonly byte[] _cipherBlock;
    private readonly byte[] _nonce = new byte[PceFormat.NonceSize];
    private readonly byte[] _key;
    private PceCipher? _cipher;
    private int _filled;
    private uint _counter;
    private bool _completed;
    private bool _aborted;

    public PceEncryptStream(Stream output, string password, PceMetadata metadata, PceOptions? options = null, PceLayout flags = PceLayout.None)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrEmpty(password);
        ArgumentNullException.ThrowIfNull(metadata);
        _output = output;
        Header = PceFormat.NewHeader(options ?? PceOptions.Default, flags);
        _header = Header.ToBytes();
        _noncePrefix = Header.NoncePrefix;
        _key = PasswordKdf.Derive(password, Header);
        _cipher = new PceCipher(Header.Cipher, _key);
        _plain = new byte[Header.ChunkSize];
        _cipherBlock = new byte[4 + Header.ChunkSize + PceFormat.TagSize];
        _output.Write(_header);
        var meta = PceFormat.EncodeMetadata(metadata);
        if (meta.Length > Header.ChunkSize)
        {
            throw new ArgumentException("Metadata larger than a block.", nameof(metadata));
        }

        meta.CopyTo(_plain, 0);
        _filled = meta.Length;
        Seal(last: false); // block 0
    }

    public PceHeader Header { get; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_completed;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_completed || _aborted, this);
        while (buffer.Length > 0)
        {
            var take = Math.Min(buffer.Length, _plain.Length - _filled);
            buffer[..take].CopyTo(_plain.AsSpan(_filled));
            _filled += take;
            buffer = buffer[take..];
            if (_filled == _plain.Length)
            {
                Seal(last: false);
            }
        }
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush() => _output.Flush();

    /// <summary>Seals the last block. Without it the file is incomplete and will be rejected as truncated.</summary>
    public void Complete()
    {
        if (_completed)
        {
            return;
        }

        ObjectDisposedException.ThrowIf(_aborted, this);
        Seal(last: true);
        _completed = true;
        _output.Flush();
        Wipe();
    }

    /// <summary>Writing failed: Dispose must not seal what was written so far as a complete file.</summary>
    public void Abort()
    {
        _aborted = true;
        Wipe();
    }

    private void Seal(bool last)
    {
        if (_counter >= PceFormat.LastBlockFlag)
        {
            throw new IOException("Too many blocks for one file.");
        }

        var counter = last ? _counter | PceFormat.LastBlockFlag : _counter;
        PceFormat.FillNonce(_nonce, _noncePrefix, counter);
        BinaryPrimitives.WriteUInt32LittleEndian(_cipherBlock, (uint)_filled);
        _cipher!.Encrypt(_nonce, _plain.AsSpan(0, _filled), _cipherBlock.AsSpan(4, _filled), _cipherBlock.AsSpan(4 + _filled, PceFormat.TagSize), _header);
        _output.Write(_cipherBlock, 0, 4 + _filled + PceFormat.TagSize);
        _counter++;
        _filled = 0;
    }

    private void Wipe()
    {
        _cipher?.Dispose();
        _cipher = null;
        CryptographicOperations.ZeroMemory(_key);
        CryptographicOperations.ZeroMemory(_plain);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                if (!_completed && !_aborted && _cipher is not null)
                {
                    Complete();
                }
            }
            finally
            {
                Wipe();
            }
        }

        base.Dispose(disposing);
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
