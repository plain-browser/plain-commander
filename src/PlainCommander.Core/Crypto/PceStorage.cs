using Microsoft.Win32.SafeHandles;

namespace PlainCommander.Core.Crypto;

/// <summary>
/// The bytes under a .pce container, addressed by offset. The container's crash safety rests on the order of
/// <see cref="Write"/>, <see cref="Flush"/> and <see cref="SetLength"/> calls, so tests swap in a storage that
/// fails at a chosen call.
/// </summary>
internal interface IPceStorage : IDisposable
{
    long Length { get; }

    /// <summary>Reads up to the buffer's size at <paramref name="offset"/>; fewer bytes only at the end.</summary>
    int Read(Span<byte> buffer, long offset);

    void Write(ReadOnlySpan<byte> buffer, long offset);

    /// <summary>Everything written so far reaches the disk before this returns.</summary>
    void Flush();

    void SetLength(long length);
}

/// <summary>A file through positional reads and writes (no shared position, so readers on several threads do not collide).</summary>
internal sealed class FilePceStorage : IPceStorage
{
    private readonly SafeFileHandle _handle;

    public FilePceStorage(string path, FileMode mode, bool writable)
    {
        // One writer at a time; readers come and go beside it. A reader that opens during a change sees the last
        // committed index (a change never overwrites what that index refers to before the next one is committed),
        // and one that reads on across later changes fails on a block that no longer authenticates.
        _handle = File.OpenHandle(
            path,
            mode,
            writable ? FileAccess.ReadWrite : FileAccess.Read,
            writable ? FileShare.Read : FileShare.ReadWrite | FileShare.Delete);
    }

    public long Length => RandomAccess.GetLength(_handle);

    public int Read(Span<byte> buffer, long offset)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = RandomAccess.Read(_handle, buffer[total..], offset + total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    public void Write(ReadOnlySpan<byte> buffer, long offset) => RandomAccess.Write(_handle, buffer, offset);

    public void Flush() => RandomAccess.FlushToDisk(_handle);

    public void SetLength(long length) => RandomAccess.SetLength(_handle, length);

    public void Dispose() => _handle.Dispose();
}
