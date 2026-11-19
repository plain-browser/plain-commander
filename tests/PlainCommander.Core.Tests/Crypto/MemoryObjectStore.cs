using PlainCommander.Core.Crypto;

namespace PlainCommander.Core.Tests.Crypto;

/// <summary>
/// Objects in memory, with the rules of S3: an object appears whole or not at all and an upload in parts is
/// invisible until completed. Every request is counted and logged, and one chosen request can fail: either lost on
/// the way (nothing happened) or done with the answer lost (it happened, the caller sees an error).
/// </summary>
internal sealed class MemoryObjectStore : IPceObjectStore
{
    public const string Crash = "injected failure";
    private readonly Lock _gate = new();
    private readonly SortedDictionary<string, (byte[] Content, DateTime ModifiedUtc)> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<Upload, DateTime> _uploads = [];

    public bool ConditionalPut { get; set; } = true;

    /// <summary>The clock objects are stamped with; tests move it to age them.</summary>
    public DateTime Now { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Requests made so far (each part of an upload is one).</summary>
    public int Requests { get; private set; }

    /// <summary>The request, counted from 1, that fails; none when not positive.</summary>
    public int FailAt { get; set; }

    /// <summary>The failing request takes effect before it throws: the answer was lost, not the request.</summary>
    public bool FailAfterEffect { get; set; }

    /// <summary>One line per request: "GET head", "PUT index/…", "PART data/… 2".</summary>
    public List<string> Log { get; } = [];

    public IReadOnlyCollection<string> Names
    {
        get
        {
            lock (_gate)
            {
                return [.. _objects.Keys];
            }
        }
    }

    /// <summary>Uploads begun and neither completed nor discarded.</summary>
    public int OpenUploads => _uploads.Count;

    public byte[]? Content(string name)
    {
        lock (_gate)
        {
            return _objects.TryGetValue(name, out var found) ? found.Content : null;
        }
    }

    /// <summary>Puts bytes under a name behind the container's back, as someone with access to the bucket could.</summary>
    public void Plant(string name, byte[] content)
    {
        lock (_gate)
        {
            _objects[name] = (content, Now);
        }
    }

    public bool Remove(string name)
    {
        lock (_gate)
        {
            return _objects.Remove(name);
        }
    }

    public MemoryObjectStore Clone()
    {
        var copy = new MemoryObjectStore { ConditionalPut = ConditionalPut, Now = Now };
        lock (_gate)
        {
            foreach (var (name, value) in _objects)
            {
                copy._objects[name] = value;
            }
        }

        return copy;
    }

    public byte[]? Get(string name, CancellationToken cancellationToken = default) =>
        Request("GET " + name, () => _objects.TryGetValue(name, out var found) ? (byte[])found.Content.Clone() : null, cancellationToken);

    public int Read(string name, long offset, Span<byte> buffer, CancellationToken cancellationToken = default)
    {
        var length = buffer.Length;
        var slice = Request($"GET {name} {offset}+{length}", () =>
        {
            if (!_objects.TryGetValue(name, out var found))
            {
                throw new PceObjectMissingException(name);
            }

            var count = (int)Math.Max(0, Math.Min(length, found.Content.Length - offset));
            return found.Content.AsSpan((int)Math.Min(offset, found.Content.Length), count).ToArray();
        }, cancellationToken);
        slice.CopyTo(buffer);
        return slice.Length;
    }

    public void Put(string name, ReadOnlySpan<byte> content, CancellationToken cancellationToken = default)
    {
        var copy = content.ToArray();
        Request("PUT " + name, () => _objects[name] = (copy, Now), cancellationToken);
    }

    public bool PutNew(string name, ReadOnlySpan<byte> content, CancellationToken cancellationToken = default)
    {
        var copy = content.ToArray();
        return Request("PUTNEW " + name, () => _objects.TryAdd(name, (copy, Now)), cancellationToken);
    }

    public IPceObjectUpload BeginUpload(string name, CancellationToken cancellationToken = default) =>
        Request("BEGIN " + name, () =>
        {
            var upload = new Upload(this, name);
            _uploads[upload] = Now;
            return upload;
        }, cancellationToken);

    public int DiscardUploads(DateTime startedBeforeUtc, CancellationToken cancellationToken = default) =>
        Request("DISCARD uploads", () =>
        {
            var stale = _uploads.Where(u => u.Value < startedBeforeUtc).Select(u => u.Key).ToList();
            stale.ForEach(u => _uploads.Remove(u));
            return stale.Count;
        }, cancellationToken);

    public void Delete(string name, CancellationToken cancellationToken = default) =>
        Request("DELETE " + name, () => _objects.Remove(name), cancellationToken);

    public IReadOnlyList<PceObjectInfo> List(string prefix, int max = int.MaxValue, CancellationToken cancellationToken = default) =>
        Request($"LIST {prefix}", () => (IReadOnlyList<PceObjectInfo>)_objects
            .Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Take(max)
            .Select(o => new PceObjectInfo(o.Key, o.Value.Content.Length, o.Value.ModifiedUtc))
            .ToList(), cancellationToken);

    private T Request<T>(string line, Func<T> effect, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Log.Add(line);
            if (++Requests != FailAt)
            {
                return effect();
            }

            if (FailAfterEffect)
            {
                effect();
            }

            throw new IOException(Crash);
        }
    }

    private sealed class Upload(MemoryObjectStore store, string name) : IPceObjectUpload
    {
        private readonly SortedDictionary<int, byte[]> _parts = [];

        public void Write(int number, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
            var copy = content.ToArray();
            store.Request($"PART {name} {number}", () => _parts[number] = copy, cancellationToken);
        }

        public void Complete(CancellationToken cancellationToken = default) =>
            store.Request("COMPLETE " + name, () =>
            {
                store._objects[name] = (_parts.Values.SelectMany(p => p).ToArray(), store.Now);
                Close();
                return true;
            }, cancellationToken);

        public void Dispose()
        {
            lock (store._gate)
            {
                Close();
            }
        }

        private void Close() => store._uploads.Remove(this);
    }
}
