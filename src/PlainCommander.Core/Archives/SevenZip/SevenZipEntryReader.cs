using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace PlainCommander.Core.Archives.SevenZip;

/// <summary>
/// Turns 7-Zip's extraction, which pushes the content of each entry into a stream we hand it, into a reader that
/// pulls: 7-Zip runs on its own thread and every entry it extracts arrives here through a bounded pipe.
/// Leaving an entry unread just drops its bytes; disposing stops the extraction.
/// </summary>
internal sealed partial class SevenZipEntryReader : IArchiveEntryReader
{
    private readonly SevenZipArchive _archive;
    private readonly Dictionary<uint, SevenZipItem> _byIndex;
    private readonly Queue<(SevenZipItem Item, EntryPipe? Pipe)> _ready = new();
    private readonly object _lock = new();
    private readonly Thread _thread;
    private bool _done;
    private bool _cancelled;
    private Exception? _error;
    private (SevenZipItem Item, EntryPipe? Pipe)? _current;
    private bool _opened;

    public SevenZipEntryReader(string archiveFile, IReadOnlySet<string>? wanted)
    {
        _archive = SevenZipArchive.Open(archiveFile);
        try
        {
            var items = _archive.Items.Where(i => wanted is null || (!i.IsDirectory && wanted.Contains(i.Key))).ToList();
            if (items.Any(static i => i.Encrypted))
            {
                // Asked and checked before extracting: a wrong password is asked again instead of failing half-way.
                _archive.RequirePassword();
            }

            _byIndex = items.ToDictionary(i => i.Index);
            var indices = items.Select(i => i.Index).Order().ToArray();
            _thread = new Thread(() => Extract(indices)) { IsBackground = true, Name = "7-Zip extract" };
            _thread.Start();
        }
        catch
        {
            _archive.Dispose();
            throw;
        }
    }

    public string Key => _current?.Item.Key ?? string.Empty;

    public bool IsDirectory => _current?.Item.IsDirectory ?? false;

    public long Size => _current?.Item.Size ?? 0;

    public DateTime? Modified => _current?.Item.Modified;

    private unsafe void Extract(uint[] indices)
    {
        var callback = new ExtractCallback(this);
        try
        {
            int hr;
            fixed (uint* p = indices)
            {
                hr = indices.Length == 0 ? HResult.Ok : _archive.Handler.Extract(p, (uint)indices.Length, 0, callback);
            }

            callback.FinishCurrent(hr == HResult.Ok ? null : new IOException(Describe(hr)));
            lock (_lock)
            {
                if (hr != HResult.Ok && !_cancelled)
                {
                    _error = hr == HResult.Abort && callback.PasswordCancelled
                        ? new ArchivePasswordException(_archive.File, false)
                        : hr == HResult.Abort ? new OperationCanceledException() : new IOException(Describe(hr));
                }
            }
        }
        catch (Exception ex)
        {
            callback.FinishCurrent(ex);
            lock (_lock)
            {
                _error = ex;
            }
        }
        finally
        {
            lock (_lock)
            {
                _done = true;
                Monitor.PulseAll(_lock);
            }
        }
    }

    private static string Describe(int hr) => Marshal.GetExceptionForHR(hr)?.Message ?? $"7-Zip error 0x{hr:X8}";

    public bool MoveNext()
    {
        if (_current is { Pipe: { } previous })
        {
            previous.Abandon();
        }

        _opened = false;
        lock (_lock)
        {
            while (_ready.Count == 0 && !_done)
            {
                Monitor.Wait(_lock);
            }

            if (_ready.Count > 0)
            {
                _current = _ready.Dequeue();
                return true;
            }

            _current = null;
            if (_error is not null)
            {
                throw _error;
            }

            return false;
        }
    }

    public Stream OpenEntryStream()
    {
        if (_current is not { } current || _opened)
        {
            throw new InvalidOperationException("No entry to read.");
        }

        _opened = true;
        if (current.Pipe is null)
        {
            return Stream.Null; // a folder
        }

        return new EntryPipeStream(current.Pipe, current.Item.Size);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _cancelled = true;
            foreach (var (_, pipe) in _ready)
            {
                pipe?.Cancel();
            }

            Monitor.PulseAll(_lock);
        }

        _current?.Pipe?.Cancel();
        _thread.Join();
        _archive.Dispose();
    }

    private void Announce(SevenZipItem item, EntryPipe? pipe)
    {
        lock (_lock)
        {
            _ready.Enqueue((item, pipe));
            Monitor.PulseAll(_lock);
        }
    }

    private bool Cancelled
    {
        get
        {
            lock (_lock)
            {
                return _cancelled;
            }
        }
    }

    [GeneratedComClass]
    private sealed unsafe partial class ExtractCallback(SevenZipEntryReader owner) : IArchiveExtractCallback, ICryptoGetTextPassword
    {
        private const int AskExtract = 0;
        private EntryPipe? _pipe;
        private SevenZipItem? _item;

        public bool PasswordCancelled { get; private set; }

        public int SetTotal(ulong total) => HResult.Ok;

        public int SetCompleted(ulong* completeValue) => owner.Cancelled ? HResult.Abort : HResult.Ok;

        public int GetStream(uint index, out ISequentialOutStream? outStream, int askExtractMode)
        {
            outStream = null;
            if (owner.Cancelled)
            {
                return HResult.Abort;
            }

            if (askExtractMode != AskExtract || !owner._byIndex.TryGetValue(index, out var item))
            {
                return HResult.Ok; // skipped or tested: 7-Zip decompresses past it in a solid block
            }

            _item = item;
            if (item.IsDirectory)
            {
                _pipe = null;
                owner.Announce(item, null);
                return HResult.Ok;
            }

            _pipe = new EntryPipe();
            owner.Announce(item, _pipe);
            outStream = new PipeOutStream(_pipe);
            return HResult.Ok;
        }

        public int PrepareOperation(int askExtractMode) => HResult.Ok;

        public int SetOperationResult(int opRes)
        {
            var result = (ExtractResult)opRes;
            Exception? error = null;
            if (result != ExtractResult.Ok && _item is { } item)
            {
                var badPassword = result == ExtractResult.WrongPassword || (item.Encrypted && result is ExtractResult.DataError or ExtractResult.CrcError);
                if (badPassword)
                {
                    ArchivePasswords.Forget(owner._archive.File);
                    error = new ArchivePasswordException(owner._archive.File, true);
                }
                else
                {
                    error = new InvalidDataException(result switch
                    {
                        ExtractResult.UnsupportedMethod => $"{item.Key}: unsupported compression method.",
                        ExtractResult.CrcError => $"{item.Key}: CRC error, the archive is damaged.",
                        ExtractResult.UnexpectedEnd => $"{item.Key}: unexpected end of the archive.",
                        ExtractResult.Unavailable => $"{item.Key}: data is in a missing volume.",
                        _ => $"{item.Key}: data error, the archive is damaged.",
                    });
                }
            }

            FinishCurrent(error);
            return HResult.Ok;
        }

        public void FinishCurrent(Exception? error)
        {
            _pipe?.Complete(error);
            _pipe = null;
            _item = null;
        }

        public int CryptoGetTextPassword(IntPtr* password)
        {
            var answer = ArchivePasswords.Get(owner._archive.File, wrong: false);
            if (answer is null)
            {
                PasswordCancelled = true;
                *password = IntPtr.Zero;
                return HResult.Abort;
            }

            *password = Marshal.StringToBSTR(answer);
            return HResult.Ok;
        }
    }
}
