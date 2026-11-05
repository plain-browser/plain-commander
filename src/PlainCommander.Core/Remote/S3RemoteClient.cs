using System.Globalization;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;

namespace PlainCommander.Core.Remote;

/// <summary>
/// Amazon S3 and compatible stores (MinIO, Wasabi, Cloudflare R2, Backblaze B2) through the AWS SDK. The server
/// root lists the buckets; below that "/" in keys makes the folders, as the AWS console shows them (an empty
/// folder is a "name/" object). S3 cannot set a file's time, so it is kept in the "mtime" metadata as rclone
/// does, and read back from there when an exact time is needed (compare, synchronize).
/// </summary>
public sealed class S3RemoteClient : IRemoteClient
{
    private const string MtimeKey = "x-amz-meta-mtime";

    /// <summary>
    /// A listing has no metadata, so a folder with up to this many files gets their own times by a HEAD each (8 at a
    /// time); bigger folders show when S3 stored the files, to keep the listing quick.
    /// </summary>
    public const int ExactTimesLimit = 500;
    private readonly RemoteSite _site;
    private readonly RemoteCredentials _credentials;
    private AmazonS3Client? _client;

    public S3RemoteClient(RemoteSite site, RemoteCredentials credentials)
    {
        _site = site ?? throw new ArgumentNullException(nameof(site));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    public bool IsConnected => _client is not null;

    public bool AtomicUploads => true;

    /// <summary>"s3.eu-central-1.amazonaws.com" → "eu-central-1"; the site's region wins; otherwise us-east-1.</summary>
    public static string RegionOf(RemoteSite site)
    {
        ArgumentNullException.ThrowIfNull(site);
        if (!string.IsNullOrWhiteSpace(site.Region))
        {
            return site.Region.Trim();
        }

        var host = site.Host.Trim().ToLowerInvariant();
        if (host.EndsWith(".amazonaws.com", StringComparison.Ordinal))
        {
            foreach (var part in host.Split('.'))
            {
                var name = part.StartsWith("s3-", StringComparison.Ordinal) ? part[3..] : part;
                if (name.Count(c => c == '-') >= 2 && name.Any(char.IsDigit))
                {
                    return name; // eu-central-1, us-west-2 …
                }
            }
        }

        return "us-east-1";
    }

    public static bool IsAws(RemoteSite site) =>
        site?.Host.Trim().EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase) == true;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await DisposeAsync().ConfigureAwait(false);
        var http = _site.PlainHttp || _site.EffectivePort == 80;
        var config = new AmazonS3Config
        {
            ServiceURL = string.Create(CultureInfo.InvariantCulture, $"{(http ? "http" : "https")}://{_site.Host.Trim()}:{_site.EffectivePort}"),
            AuthenticationRegion = RegionOf(_site),
            ForcePathStyle = !IsAws(_site), // MinIO and most compatible stores want bucket names in the path
            Timeout = TimeSpan.FromSeconds(60),
            MaxErrorRetry = 2,
        };
        AWSCredentials credentials = string.IsNullOrEmpty(_site.User)
            ? new AnonymousAWSCredentials()
            : new BasicAWSCredentials(_site.User, _credentials.Secret);
        var client = new AmazonS3Client(credentials, config);
        try
        {
            await client.ListBucketsAsync(new ListBucketsRequest(), cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.Forbidden && !IsLoginError(ex))
        {
            // a key limited to some buckets may not list them all: the login itself was fine
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            client.Dispose();
            throw Wrap(ex);
        }

        _client = client;
    }

    public Task<string> GetHomeAsync(CancellationToken cancellationToken) => Task.FromResult("/");

    public Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken) => CallAsync<IReadOnlyList<RemoteItem>>(async c =>
    {
        var (bucket, key) = Split(path);
        if (bucket is null)
        {
            var buckets = await c.ListBucketsAsync(new ListBucketsRequest(), cancellationToken).ConfigureAwait(false);
            return (buckets.Buckets ?? []).Select(b => new RemoteItem(b.BucketName, true, 0, Utc(b.CreationDate))).ToList();
        }

        var prefix = key.Length == 0 ? string.Empty : key + "/";
        var items = new List<RemoteItem>();
        var request = new ListObjectsV2Request { BucketName = bucket, Prefix = prefix, Delimiter = "/" };
        ListObjectsV2Response response;
        do
        {
            response = await c.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            foreach (var folder in response.CommonPrefixes ?? [])
            {
                var name = folder[prefix.Length..].TrimEnd('/');
                if (name.Length > 0)
                {
                    items.Add(new RemoteItem(name, true, 0, DateTimeOffset.MinValue));
                }
            }

            foreach (var file in response.S3Objects ?? [])
            {
                var name = file.Key[prefix.Length..];
                if (name.Length > 0 && !name.Contains('/', StringComparison.Ordinal))
                {
                    items.Add(new RemoteItem(name, false, file.Size ?? 0, Utc(file.LastModified)));
                }
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);

        var files = items.Where(i => !i.IsDirectory).ToList();
        if (files.Count is > 0 and <= ExactTimesLimit)
        {
            var exact = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, async (file, token) =>
            {
                if (await HeadAsync(c, bucket, prefix + file.Name, token).ConfigureAwait(false) is { } head && head.Metadata[MtimeKey] is not null)
                {
                    exact[file.Name] = MtimeOf(head);
                }
            }).ConfigureAwait(false);
            for (var i = 0; i < items.Count; i++)
            {
                if (!items[i].IsDirectory && exact.TryGetValue(items[i].Name, out var time))
                {
                    items[i] = items[i] with { Modified = time };
                }
            }
        }

        return items;
    }, cancellationToken);

    public Task<RemoteItem?> StatAsync(string path, CancellationToken cancellationToken) => CallAsync(async c =>
    {
        var (bucket, key) = Split(path);
        if (bucket is null)
        {
            return new RemoteItem("/", true, 0, DateTimeOffset.MinValue);
        }

        if (key.Length == 0)
        {
            return await BucketExistsAsync(c, bucket, cancellationToken).ConfigureAwait(false)
                ? new RemoteItem(bucket, true, 0, DateTimeOffset.MinValue)
                : null;
        }

        var name = RemotePath.NameOf(path);
        if (await HeadAsync(c, bucket, key, cancellationToken).ConfigureAwait(false) is { } head)
        {
            return new RemoteItem(name, false, head.ContentLength, MtimeOf(head));
        }

        var inside = await c.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = key + "/", MaxKeys = 1 }, cancellationToken).ConfigureAwait(false);
        return (inside.KeyCount ?? 0) > 0 ? new RemoteItem(name, true, 0, DateTimeOffset.MinValue) : (RemoteItem?)null;
    }, cancellationToken);

    /// <summary>The time the file had when it was uploaded (metadata), else when S3 stored it.</summary>
    public async Task<DateTimeOffset?> GetModifiedAsync(string path, CancellationToken cancellationToken)
    {
        var (bucket, key) = Split(path);
        if (bucket is null || key.Length == 0)
        {
            return null;
        }

        return await CallAsync(async c => await HeadAsync(c, bucket, key, cancellationToken).ConfigureAwait(false) is { } head ? MtimeOf(head) : (DateTimeOffset?)null, cancellationToken).ConfigureAwait(false);
    }

    public Task DownloadAsync(string path, Stream destination, long offset, CancellationToken cancellationToken) => CallAsync(async c =>
    {
        var (bucket, key) = FileKey(path);
        var request = new GetObjectRequest { BucketName = bucket, Key = key };
        if (offset > 0)
        {
            request.ByteRange = new ByteRange(string.Create(CultureInfo.InvariantCulture, $"bytes={offset}-"));
        }

        using var response = await c.GetObjectAsync(request, cancellationToken).ConfigureAwait(false);
        await response.ResponseStream.CopyToAsync(destination, 1 << 16, cancellationToken).ConfigureAwait(false);
        return true;
    }, cancellationToken);

    /// <summary>One request for small files, parts in parallel for big ones (the SDK decides). S3 has no append.</summary>
    public Task UploadAsync(Stream source, string path, bool append, CancellationToken cancellationToken) => CallAsync(async c =>
    {
        if (append)
        {
            throw new RemoteException("S3 cannot add to an existing object.");
        }

        var (bucket, key) = FileKey(path);
        using var transfer = new TransferUtility(c);
        await transfer.UploadAsync(new TransferUtilityUploadRequest { BucketName = bucket, Key = key, InputStream = source, AutoCloseStream = false }, cancellationToken).ConfigureAwait(false);
        return true;
    }, cancellationToken);

    public Task DeleteFileAsync(string path, CancellationToken cancellationToken) => CallAsync(async c =>
    {
        var (bucket, key) = FileKey(path);
        await c.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }, cancellationToken).ConfigureAwait(false);
        return true;
    }, cancellationToken);

    /// <summary>A bucket is removed (it must be empty); a folder loses its "name/" marker, if it had one.</summary>
    public Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken) => CallAsync(async c =>
    {
        var (bucket, key) = Split(path);
        if (bucket is null)
        {
            throw new RemoteException("The server root cannot be deleted.");
        }

        if (key.Length == 0)
        {
            await c.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await c.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key + "/" }, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }, cancellationToken);

    /// <summary>At the root a new bucket; below, an empty "name/" object, as the AWS console makes folders.</summary>
    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) => CallAsync(async c =>
    {
        var (bucket, key) = Split(path);
        if (bucket is null)
        {
            throw new RemoteException("The server root cannot be created.");
        }

        if (key.Length == 0)
        {
            await c.PutBucketAsync(new PutBucketRequest { BucketName = bucket }, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await c.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key + "/", InputStream = new MemoryStream() }, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }, cancellationToken);

    /// <summary>S3 has no rename: copy then delete, every object of a folder. Buckets cannot be renamed.</summary>
    public Task RenameAsync(string source, string target, CancellationToken cancellationToken) => CallAsync(async c =>
    {
        var (fromBucket, fromKey) = FileKey(source);
        var (toBucket, toKey) = FileKey(target);
        if (await HeadAsync(c, fromBucket, fromKey, cancellationToken).ConfigureAwait(false) is not null)
        {
            await MoveObjectAsync(c, fromBucket, fromKey, toBucket, toKey, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var request = new ListObjectsV2Request { BucketName = fromBucket, Prefix = fromKey + "/" };
        ListObjectsV2Response response;
        var moved = 0;
        do
        {
            response = await c.ListObjectsV2Async(request, cancellationToken).ConfigureAwait(false);
            foreach (var item in response.S3Objects ?? [])
            {
                await MoveObjectAsync(c, fromBucket, item.Key, toBucket, toKey + item.Key[fromKey.Length..], cancellationToken).ConfigureAwait(false);
                moved++;
            }

            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated == true);

        return moved > 0 ? true : throw new FileNotFoundException(null, source);
    }, cancellationToken);

    /// <summary>Stores the time as metadata: the object is copied onto itself with the new metadata (server side).</summary>
    public Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken) => CallAsync(async c =>
    {
        var (bucket, key) = FileKey(path);
        try
        {
            var head = await HeadAsync(c, bucket, key, cancellationToken).ConfigureAwait(false);
            if (head is null)
            {
                return false;
            }

            var copy = new CopyObjectRequest
            {
                SourceBucket = bucket,
                SourceKey = key,
                DestinationBucket = bucket,
                DestinationKey = key,
                MetadataDirective = S3MetadataDirective.REPLACE,
                ContentType = head.Headers.ContentType,
            };
            foreach (var name in head.Metadata.Keys)
            {
                copy.Metadata.Add(name, head.Metadata[name]);
            }

            copy.Metadata[MtimeKey] = (modified.ToUnixTimeMilliseconds() / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
            await c.CopyObjectAsync(copy, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception)
        {
            // best effort, as setstat/MFMT elsewhere: the content is what matters
        }

        return true;
    }, cancellationToken);

    public Task SetModeAsync(string path, int mode, CancellationToken cancellationToken) =>
        throw new RemoteException("S3 has no Unix permissions.");

    public ValueTask DisposeAsync()
    {
        _client?.Dispose();
        _client = null;
        return ValueTask.CompletedTask;
    }

    /// <summary>"/bucket/a/b" → ("bucket", "a/b"); "/" → (null, "").</summary>
    internal static (string? Bucket, string Key) Split(string path)
    {
        var inner = RemotePath.NormalizeInner(path).Trim('/');
        if (inner.Length == 0)
        {
            return (null, string.Empty);
        }

        var slash = inner.IndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? (inner, string.Empty) : (inner[..slash], inner[(slash + 1)..]);
    }

    private static (string Bucket, string Key) FileKey(string path)
    {
        var (bucket, key) = Split(path);
        return bucket is null || key.Length == 0 ? throw new RemoteException("Files live inside a bucket: /bucket/name.") : (bucket, key);
    }

    private static async Task<bool> BucketExistsAsync(AmazonS3Client c, string bucket, CancellationToken token)
    {
        try
        {
            await c.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, MaxKeys = 0 }, token).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static async Task<GetObjectMetadataResponse?> HeadAsync(AmazonS3Client c, string bucket, string key, CancellationToken token)
    {
        try
        {
            return await c.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key }, token).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static async Task MoveObjectAsync(AmazonS3Client c, string fromBucket, string fromKey, string toBucket, string toKey, CancellationToken token)
    {
        await c.CopyObjectAsync(new CopyObjectRequest { SourceBucket = fromBucket, SourceKey = fromKey, DestinationBucket = toBucket, DestinationKey = toKey }, token).ConfigureAwait(false);
        await c.DeleteObjectAsync(new DeleteObjectRequest { BucketName = fromBucket, Key = fromKey }, token).ConfigureAwait(false);
    }

    private static DateTimeOffset MtimeOf(GetObjectMetadataResponse head)
    {
        var stored = head.Metadata[MtimeKey];
        if (double.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(seconds * 1000));
        }

        return Utc(head.LastModified);
    }

    private static DateTimeOffset Utc(DateTime? time) =>
        time is { } t && t > DateTime.MinValue ? new DateTimeOffset(t.ToUniversalTime(), TimeSpan.Zero) : DateTimeOffset.MinValue;

    private static bool IsLoginError(AmazonS3Exception ex) => ex.ErrorCode is "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "InvalidToken" or "ExpiredToken";

    private async Task<T> CallAsync<T>(Func<AmazonS3Client, Task<T>> call, CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await call(_client!).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not RemoteException and not FileNotFoundException)
        {
            throw Wrap(ex);
        }
    }

    private static Exception Wrap(Exception ex) => ex switch
    {
        AmazonS3Exception { ErrorCode: "NoSuchBucket" or "NoSuchKey" } s3 => new FileNotFoundException(s3.Message, s3),
        AmazonS3Exception s3 when IsLoginError(s3) => new RemoteException(s3.Message, s3) { IsAuthentication = true },
        AmazonS3Exception { StatusCode: HttpStatusCode.Forbidden } s3 => new UnauthorizedAccessException(s3.Message, s3),
        _ => new RemoteException(ex.Message, ex),
    };
}
