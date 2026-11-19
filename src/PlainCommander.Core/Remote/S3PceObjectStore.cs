using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using Amazon.S3;
using Amazon.S3.Model;
using PlainCommander.Core.Crypto;

namespace PlainCommander.Core.Remote;

/// <summary>
/// The objects of one .pce container under a prefix of an S3 bucket ("folder/name.pce/"). Requests block the
/// calling thread, as the container's own work does; uploads in parts are sent from several threads by the
/// container. Every failure of a request surfaces as <see cref="IOException"/>.
/// <para>
/// An index is committed with "If-None-Match: *". Stores that ignore the header are recognised by a probe on first
/// use (<see cref="ConditionalPut"/>); with them the name is checked and then written, which is safe for one
/// writer only.
/// </para>
/// </summary>
internal sealed class S3PceObjectStore : IPceObjectStore
{
    private const int ConflictRetries = 5;
    private readonly IAmazonS3 _client;
    private readonly string _bucket;
    private readonly string _prefix;
    private readonly Lazy<bool> _conditional;

    /// <param name="client">Not owned: whoever made it disposes it.</param>
    /// <param name="prefix">The container's key prefix, with or without the closing '/'; empty for the bucket itself.</param>
    public S3PceObjectStore(IAmazonS3 client, string bucket, string prefix)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrEmpty(bucket);
        ArgumentNullException.ThrowIfNull(prefix);
        _client = client;
        _bucket = bucket;
        _prefix = prefix.Length == 0 || prefix.EndsWith('/') ? prefix : prefix + "/";
        _conditional = new Lazy<bool>(Probe);
    }

    public bool ConditionalPut => _conditional.Value;

    public byte[]? Get(string name, CancellationToken cancellationToken = default) => Call(
        async () =>
        {
            try
            {
                using var response = await _client.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = Key(name) }, cancellationToken).ConfigureAwait(false);
                using var content = new MemoryStream(response.ContentLength > 0 ? (int)Math.Min(response.ContentLength, int.MaxValue) : 0);
                await response.ResponseStream.CopyToAsync(content, cancellationToken).ConfigureAwait(false);
                return content.ToArray();
            }
            catch (AmazonS3Exception ex) when (IsMissing(ex))
            {
                return null;
            }
        },
        cancellationToken);

    public int Read(string name, long offset, Span<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0)
        {
            return 0;
        }

        var length = buffer.Length;
        var bytes = Call(
            async () =>
            {
                var request = new GetObjectRequest { BucketName = _bucket, Key = Key(name), ByteRange = new ByteRange(offset, offset + length - 1) };
                try
                {
                    using var response = await _client.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
                    var content = new byte[length];
                    var read = await response.ResponseStream.ReadAtLeastAsync(content, length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                    return content.AsMemory(0, read);
                }
                catch (AmazonS3Exception ex) when (IsMissing(ex))
                {
                    throw new PceObjectMissingException(name);
                }
                catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    return Memory<byte>.Empty; // the range starts at or past the end
                }
            },
            cancellationToken);
        bytes.Span.CopyTo(buffer);
        return bytes.Length;
    }

    public void Put(string name, ReadOnlySpan<byte> content, CancellationToken cancellationToken = default)
    {
        var bytes = content.ToArray();
        Call(() => PutAsync(Key(name), bytes, onlyNew: false, cancellationToken), cancellationToken);
    }

    public bool PutNew(string name, ReadOnlySpan<byte> content, CancellationToken cancellationToken = default)
    {
        var bytes = content.ToArray();
        var key = Key(name);
        if (ConditionalPut)
        {
            return Call(() => PutAsync(key, bytes, onlyNew: true, cancellationToken), cancellationToken);
        }

        return Call(
            async () => !await ExistsAsync(key, cancellationToken).ConfigureAwait(false)
                && await PutAsync(key, bytes, onlyNew: false, cancellationToken).ConfigureAwait(false),
            cancellationToken);
    }

    public IPceObjectUpload BeginUpload(string name, CancellationToken cancellationToken = default)
    {
        var key = Key(name);
        var id = Call(
            async () => (await _client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = _bucket, Key = key }, cancellationToken).ConfigureAwait(false)).UploadId,
            cancellationToken);
        return new Upload(this, key, id);
    }

    public int DiscardUploads(DateTime startedBeforeUtc, CancellationToken cancellationToken = default) => Call(
        async () =>
        {
            var discarded = 0;
            var request = new ListMultipartUploadsRequest { BucketName = _bucket, Prefix = _prefix };
            while (true)
            {
                var page = await _client.ListMultipartUploadsAsync(request, cancellationToken).ConfigureAwait(false);
                foreach (var upload in page.MultipartUploads ?? [])
                {
                    if (upload.Initiated is { } started && started.ToUniversalTime() < startedBeforeUtc)
                    {
                        await _client.AbortMultipartUploadAsync(
                            new AbortMultipartUploadRequest { BucketName = _bucket, Key = upload.Key, UploadId = upload.UploadId }, cancellationToken).ConfigureAwait(false);
                        discarded++;
                    }
                }

                if (page.IsTruncated != true || string.IsNullOrEmpty(page.NextKeyMarker))
                {
                    return discarded;
                }

                request.KeyMarker = page.NextKeyMarker;
                request.UploadIdMarker = page.NextUploadIdMarker;
            }
        },
        cancellationToken);

    public void Delete(string name, CancellationToken cancellationToken = default) => Call(
        async () =>
        {
            await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = Key(name) }, cancellationToken).ConfigureAwait(false);
            return true;
        },
        cancellationToken);

    public IReadOnlyList<PceObjectInfo> List(string prefix, int max = int.MaxValue, CancellationToken cancellationToken = default) => Call<IReadOnlyList<PceObjectInfo>>(
        async () =>
        {
            var found = new List<PceObjectInfo>();
            var request = new ListObjectsV2Request { BucketName = _bucket, Prefix = _prefix + prefix };
            while (found.Count < max)
            {
                request.MaxKeys = Math.Min(max - found.Count, 1000);
                var page = await _client.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
                foreach (var item in page.S3Objects ?? [])
                {
                    if (found.Count < max)
                    {
                        found.Add(new PceObjectInfo(item.Key[_prefix.Length..], item.Size ?? 0, (item.LastModified ?? DateTime.MinValue).ToUniversalTime()));
                    }
                }

                if (page.IsTruncated != true || string.IsNullOrEmpty(page.NextContinuationToken))
                {
                    break;
                }

                request.ContinuationToken = page.NextContinuationToken;
            }

            // S3 lists in the byte order of the keys; a store that does not is put right here.
            found.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
            return found;
        },
        cancellationToken);

    private static bool IsMissing(AmazonS3Exception ex) => ex.StatusCode == HttpStatusCode.NotFound || ex.ErrorCode is "NoSuchKey" or "NoSuchBucket";

    /// <summary>Runs a request to its end on the calling thread's time. Whatever the SDK or the network throws becomes an <see cref="IOException"/>.</summary>
    private static T Call<T>(Func<Task<T>> request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.Run(request, cancellationToken).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IOException)
        {
            throw new IOException(ex.Message, ex);
        }
    }

    private string Key(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return _prefix + name;
    }

    private async Task<bool> ExistsAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await _client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = _bucket, Key = key }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (IsMissing(ex))
        {
            return false;
        }
    }

    /// <summary>False only for <paramref name="onlyNew"/> when the name is taken.</summary>
    private async Task<bool> PutAsync(string key, byte[] content, bool onlyNew, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var body = new MemoryStream(content, writable: false);
            var request = new PutObjectRequest { BucketName = _bucket, Key = key, InputStream = body, AutoCloseStream = false };
            if (onlyNew)
            {
                request.IfNoneMatch = "*";
            }

            try
            {
                await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (AmazonS3Exception ex) when (onlyNew && ex.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                return false;
            }
            catch (AmazonS3Exception ex) when (onlyNew && ex.StatusCode == HttpStatusCode.Conflict && attempt < ConflictRetries)
            {
                // Another conditional write to the same name is in progress: S3 asks to try again
                await Task.Delay(50 << attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Whether the store honours "If-None-Match: *": a second conditional write to a scratch name must be refused.</summary>
    private bool Probe()
    {
        var key = _prefix + ".probe-" + Guid.NewGuid().ToString("N");
        return Call(
            async () =>
            {
                try
                {
                    return await PutAsync(key, [1], onlyNew: true, CancellationToken.None).ConfigureAwait(false)
                        && !await PutAsync(key, [2], onlyNew: true, CancellationToken.None).ConfigureAwait(false);
                }
                catch (AmazonS3Exception ex) when (ex.StatusCode is HttpStatusCode.NotImplemented or HttpStatusCode.BadRequest)
                {
                    return false; // the header itself is refused
                }
                finally
                {
                    await _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = key }, CancellationToken.None).ConfigureAwait(false);
                }
            },
            CancellationToken.None);
    }

    private sealed class Upload(S3PceObjectStore store, string key, string id) : IPceObjectUpload
    {
        private readonly List<PartETag> _parts = [];
        private bool _done;

        public void Write(int number, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
            var tag = Call(
                async () =>
                {
                    using var body = MemoryMarshal.TryGetArray(content, out var segment)
                        ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
                        : new MemoryStream(content.ToArray(), writable: false);
                    var response = await store._client.UploadPartAsync(
                        new UploadPartRequest { BucketName = store._bucket, Key = key, UploadId = id, PartNumber = number, InputStream = body, PartSize = content.Length },
                        cancellationToken).ConfigureAwait(false);
                    return new PartETag(response);
                },
                cancellationToken);
            lock (_parts)
            {
                _parts.Add(tag);
            }
        }

        public void Complete(CancellationToken cancellationToken = default)
        {
            List<PartETag> parts;
            lock (_parts)
            {
                parts = [.. _parts.OrderBy(p => p.PartNumber)];
            }

            Call(
                async () =>
                {
                    await store._client.CompleteMultipartUploadAsync(
                        new CompleteMultipartUploadRequest { BucketName = store._bucket, Key = key, UploadId = id, PartETags = parts }, cancellationToken).ConfigureAwait(false);
                    return true;
                },
                cancellationToken);
            _done = true;
        }

        public void Dispose()
        {
            if (_done)
            {
                return;
            }

            _done = true;
            try
            {
                Call(
                    async () =>
                    {
                        await store._client.AbortMultipartUploadAsync(
                            new AbortMultipartUploadRequest { BucketName = store._bucket, Key = key, UploadId = id }, CancellationToken.None).ConfigureAwait(false);
                        return true;
                    },
                    CancellationToken.None);
            }
            catch (IOException)
            {
                // DiscardUploads finds it later
            }
        }
    }
}
