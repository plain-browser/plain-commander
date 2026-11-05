using System.Runtime.InteropServices.Marshalling;

namespace PlainCommander.Core.Archives.SevenZip;

/// <summary>A seekable file (or any seekable stream) as 7-Zip's input stream.</summary>
[GeneratedComClass]
internal sealed unsafe partial class InStreamAdapter(Stream stream, Action<int>? onRead = null, Func<bool>? cancelled = null) : IInStream, IStreamGetSize
{

    public Stream Stream => stream;

    public int Read(byte* data, uint size, uint* processedSize)
    {
        if (processedSize is not null)
        {
            *processedSize = 0;
        }

        if (cancelled?.Invoke() == true)
        {
            return HResult.Abort;
        }

        try
        {
            var read = stream.Read(new Span<byte>(data, (int)Math.Min(size, int.MaxValue)));
            if (processedSize is not null)
            {
                *processedSize = (uint)read;
            }

            onRead?.Invoke(read);
            return HResult.Ok;
        }
        catch (OperationCanceledException)
        {
            return HResult.Abort;
        }
        catch (IOException ex)
        {
            return ex.HResult < 0 ? ex.HResult : HResult.Fail;
        }
    }

    public int Seek(long offset, uint seekOrigin, ulong* newPosition)
    {
        try
        {
            var position = stream.Seek(offset, (SeekOrigin)seekOrigin);
            if (newPosition is not null)
            {
                *newPosition = (ulong)position;
            }

            return HResult.Ok;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return HResult.Fail;
        }
    }

    public int GetSize(ulong* size)
    {
        try
        {
            *size = (ulong)stream.Length;
            return HResult.Ok;
        }
        catch (NotSupportedException)
        {
            return HResult.NotImplemented;
        }
    }
}

/// <summary>A seekable file as 7-Zip's output stream (a new 7z archive goes back to write its header).</summary>
[GeneratedComClass]
internal sealed unsafe partial class OutStreamAdapter(Stream stream) : IOutStream
{
    public int Write(byte* data, uint size, uint* processedSize)
    {
        try
        {
            stream.Write(new ReadOnlySpan<byte>(data, (int)size));
            if (processedSize is not null)
            {
                *processedSize = size;
            }

            return HResult.Ok;
        }
        catch (IOException ex)
        {
            if (processedSize is not null)
            {
                *processedSize = 0;
            }

            return ex.HResult < 0 ? ex.HResult : HResult.Fail;
        }
    }

    public int Seek(long offset, uint seekOrigin, ulong* newPosition)
    {
        try
        {
            var position = stream.Seek(offset, (SeekOrigin)seekOrigin);
            if (newPosition is not null)
            {
                *newPosition = (ulong)position;
            }

            return HResult.Ok;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return HResult.Fail;
        }
    }

    public int SetSize(ulong newSize)
    {
        try
        {
            stream.SetLength((long)newSize);
            return HResult.Ok;
        }
        catch (IOException)
        {
            return HResult.Fail;
        }
    }
}

/// <summary>
/// Bytes 7-Zip pushes (one entry being extracted) handed over to a reader that pulls them, with a bounded buffer
/// in between so a large entry never sits in memory whole. The writer side runs on 7-Zip's thread.
/// </summary>
internal sealed class EntryPipe
{
    private const int MaxBuffered = 4 << 20;

    private readonly Queue<byte[]> _chunks = new();
    private readonly object _lock = new();
    private int _offset;
    private long _buffered;
    private bool _completed;
    private bool _abandoned;
    private bool _cancelled;
    private Exception? _error;

    /// <summary>Producer: false when the whole extraction should stop (the reader was disposed).</summary>
    public bool Write(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            while (_buffered >= MaxBuffered && !_abandoned && !_cancelled)
            {
                Monitor.Wait(_lock);
            }

            if (_cancelled)
            {
                return false;
            }

            if (_abandoned || data.IsEmpty)
            {
                return true; // nobody wants the rest of this entry: let 7-Zip move on
            }

            _chunks.Enqueue(data.ToArray());
            _buffered += data.Length;
            Monitor.PulseAll(_lock);
            return true;
        }
    }

    public void Complete(Exception? error)
    {
        lock (_lock)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _error = error;
            Monitor.PulseAll(_lock);
        }
    }

    /// <summary>The reader moved on (or skipped the entry): what 7-Zip still writes is dropped.</summary>
    public void Abandon()
    {
        lock (_lock)
        {
            _abandoned = true;
            _chunks.Clear();
            _buffered = 0;
            Monitor.PulseAll(_lock);
        }
    }

    public void Cancel()
    {
        lock (_lock)
        {
            _cancelled = true;
            Monitor.PulseAll(_lock);
        }
    }

    public int Read(Span<byte> buffer)
    {
        lock (_lock)
        {
            while (_chunks.Count == 0 && !_completed)
            {
                Monitor.Wait(_lock);
            }

            if (_chunks.Count == 0)
            {
                if (_error is not null)
                {
                    throw _error;
                }

                return 0;
            }

            var total = 0;
            while (total < buffer.Length && _chunks.Count > 0)
            {
                var head = _chunks.Peek();
                var n = Math.Min(head.Length - _offset, buffer.Length - total);
                head.AsSpan(_offset, n).CopyTo(buffer[total..]);
                total += n;
                _offset += n;
                if (_offset == head.Length)
                {
                    _chunks.Dequeue();
                    _offset = 0;
                }
            }

            _buffered -= total;
            Monitor.PulseAll(_lock);
            return total;
        }
    }
}

/// <summary>The reader's end of an <see cref="EntryPipe"/>.</summary>
internal sealed class EntryPipeStream(EntryPipe pipe, long length) : Stream
{
    private long _position;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position { get => _position; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = pipe.Read(buffer);
        _position += read;
        return read;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>What 7-Zip writes for one entry goes into its pipe.</summary>
[GeneratedComClass]
internal sealed unsafe partial class PipeOutStream(EntryPipe pipe) : ISequentialOutStream
{
    public int Write(byte* data, uint size, uint* processedSize)
    {
        var ok = pipe.Write(new ReadOnlySpan<byte>(data, (int)size));
        if (processedSize is not null)
        {
            *processedSize = ok ? size : 0;
        }

        return ok ? HResult.Ok : HResult.Abort;
    }
}
