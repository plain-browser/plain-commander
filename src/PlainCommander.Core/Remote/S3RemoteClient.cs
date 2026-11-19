using System.Globalization;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using PlainCommander.Core.Crypto;

namespace PlainCommander.Core.Remote;

/// <summary>
/// Amazon S3 and compatible stores (MinIO, Wasabi, Cloudflare R2, Backblaze B2) through the AWS SDK. The server
/// root lists the buckets; below that "/" in keys makes the folders, as the AWS console shows them (an empty
/// folder is a "name/" object). S3 cannot set a file's time, so it is kept in the "mtime" metadata as rclone
/// does, and read back from there when an exact time is needed (compare, synchronize).
/// <para>
/// A folder "name.pce" that holds a "head" object is a .pce container kept as objects
/// (<see cref="PceObjectContainer"/>): paths below it are answered from the container, decrypted — its files are
/// listed, fetched, sent, deleted and renamed like files of any folder. The folder itself stays what it is on the
/// server: deleting or renaming it deletes or moves its objects.
/// </para>
/// </summary>
public sealed class S3RemoteClient : IRemoteClient
{
    private const string MtimeKey = "x-amz-meta-mtime";

    /// <summary>
    /// A listing has no metadata, so a folder with up to this many files gets their own times by a HEAD each (8 at a
    /// time); bigger folders show when S3 stored the files, to keep the listing quick.
    /// </summary>
    public const int ExactTimesLimit = 500;
    private static readonly TimeSpan NotAContainerFor = TimeSpan.FromSeconds(3);
    private readonly RemoteSite _site;
    private readonly RemoteCredentials _credentials;
    private readonly Dictionary<string, PceRemoteView> _containers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _notContainers = new(StringComparer.Ordinal);
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

    public async Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken)
    {
        if (await ContainerAsync(path, cancellationToken).ConfigureAwait(false) is { } inside)
        {
            return await InContainerAsync(() => inside.View.List(inside.Inner) ?? throw new DirectoryNotFoundException(path), cancellationToken).ConfigureAwait(false);
        }

        return await ListPlainAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private Task<IReadOnlyList<RemoteItem>> ListPlainAsync(string path, CancellationToken cancellationToken) => CallAsync<IReadOnlyList<RemoteItem>>(async c =>
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

        // a folder "name.pce" holding "head" is a container: the panel draws it as encrypted
        var candidates = items.Where(i => i.IsDirectory && PceFormat.IsPceName(i.Name)).Take(ExactTimesLimit).ToList();
        if (candidates.Count > 0)
        {
            var containers = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.Ordinal);
            await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, async (folder, token) =>
            {
                if (await HeadAsync(c, bucket, prefix + folder.Name + "/" + PceObjectNames.Head, token).ConfigureAwait(false) is not null)
                {
                    containers[folder.Name] = true;
                }
            }).ConfigureAwait(false);
            for (var i = 0; i < items.Count; i++)
            {
                if (items[i].IsDirectory && containers.ContainsKey(items[i].Name))
                {
                    items[i] = items[i] with { IsContainer = true };
                }
            }
        }

        return items;
    }, cancellationToken);

    public async Task<RemoteItem?> StatAsync(string path, CancellationToken cancellationToken)
    {
        if (await ContainerAsync(path, cancellationToken).ConfigureAwait(false) is { Inner.Length: > 0 } inside)
        {
            return await InContainerAsync(() => inside.View.Stat(inside.Inner), cancellationToken).ConfigureAwait(false);
        }

        return await StatPlainAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private Task<RemoteItem?> StatPlainAsync(string path, CancellationToken cancellationToken) => CallAsync(async c =>
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
        if (await ContainerAsync(path, cancellationToken).ConfigureAwait(false) is { Inner.Length: > 0 } inside)
        {
            return await InContainerAsync(() => inside.View.Stat(inside.Inner)?.Modified, cancellationToken).ConfigureAwait(false);
        }

        var (bucket, key) = Split(path);
        if (bucket is null || key.Length == 0)
        {
            return null;
        }

        return await CallAsync(async c => await HeadAsync(c, bucket, key, cancellationToken).ConfigureAwait(false) is { } head ? MtimeOf(head) : (DateTimeOffset?)null, cancellationToken).ConfigureAwait(false);
    }

    public async Task DownloadAsync(string path, Stream destination, long offset, CancellationToken cancellationToken)
    {
        if (await ContainerAsync(path, cancellationToken).ConfigureAwait(false) is { Inner.Length: > 0 } inside)
        {
            await InContainerAsync(() => inside.View.Download(inside.Inner, destination, offset, cancellationToken), cancellationToken).ConfigureAwait(false);
            return;
        }

        await DownloadPlainAsync(path, destination, offset, cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> DownloadPlainAsync(string path, Stream destination, long offset, CancellationToken cancellationToken) => CallAsync(async c =>
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
    public async Task UploadAsync(Stream source, string path, bool append, CancellationToken cancellationToken)
    {
        if (!append && await ContainerAsync(path, cancellationToken).ConfigureAwait(false) is { Inner.Length: > 0 } inside)
        {
            await InContainerAsync(() => inside.View.Upload(source, inside.Inner, cancellationToken), cancellationToken).ConfigureAwait(false);
            return;
        }

        await UploadPlainAsync(source, path, append, cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> UploadPlainAsync(Stream source, string path, bool append, CancellationToken cancellationToken) => CallAsync(async c =>
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

    public async Task DeleteFileAsync(string path, CancellationToken cancellationToken)
    {
        if (await ContainerAsync(path, cancellationToken).ConfigureAwait(false) is { Inner.Length: > 0 } inside)
        {
            await InContainerAsync(() => inside.View.DeleteFile(inside.Inner), cancellationToken).ConfigureAwait(false);
            return;
        }

        await DeleteFilePlainAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> DeleteFilePlainAsync(string path, CancellationToken cancellationToken) => CallAsync(async c =>
    {
        var (bucket, key) = FileKey(path);
        await c.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = key }, cancellationToken).ConfigureAwait(false);
        return true;
    }, cancellationToken);

    /// <summary>A bucket is removed (it must be empty); a folder loses its "name/" marker, if it had one.</summary>
    public async Task DeleteDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        if (await ContainerAsync(path, cancellationToken).ConfigureAwait(false) is { } inside)
        {
            if (inside.Inner.Length > 0)
            {
                await InContainerAsync(() => inside.View.DeleteDirectory(inside.Inner), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await DeleteContainerAsync(path, inside.View, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await DeleteDirectoryPlainAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> DeleteDirectoryPlainAsync(string path, CancellationToken cancellationToken) => CallAsync(async c =>
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
    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        if (await ContainerAsync(path, cancellationToken).ConfigureAwait(false) is { Inner.Length: > 0 } inside)
        {
            await InContainerAsync(() => inside.View.CreateDirectory(inside.Inner), cancellationToken).ConfigureAwait(false);
            return;
        }

        await CreateDirectoryPlainAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> CreateDirectoryPlainAsync(string path, CancellationToken cancellationToken) => CallAsync(async c =>
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
    public async Task RenameAsync(string source, string target, CancellationToken cancellationToken)
    {
        var from = await ContainerAsync(source, cancellationToken).ConfigureAwait(false);
        var to = await ContainerAsync(target, cancellationToken).ConfigureAwait(false);
        if (from is { Inner.Length: > 0 } || to is { Inner.Length: > 0 })
        {
            if (from is not { Inner.Length: > 0 } a || to is not { Inner.Length: > 0 } b || !ReferenceEquals(a.View, b.View))
            {
                throw new RemoteException("A file cannot be moved into or out of an encrypted container by renaming; copy it instead.");
            }

            await InContainerAsync(() => a.View.Rename(a.Inner, b.Inner), cancellationToken).ConfigureAwait(false);
            return;
        }

        // The container itself moves as the objects it is: whatever is pending goes in first, and its view is
        // dropped, because the place it stood for is about to be gone.
        if (from is { } moved)
        {
            await InContainerAsync(moved.View.Flush, cancellationToken).ConfigureAwait(false);
            Forget(moved.View);
        }

        await RenamePlainAsync(source, target, cancellationToken).ConfigureAwait(false);
        _notContainers.Clear();
    }

    private Task<bool> RenamePlainAsync(string source, string target, CancellationToken cancellationToken) => CallAsync(async c =>
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
    public async Task SetModifiedAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken)
    {
        if (await ContainerAsync(path, cancellationToken).ConfigureAwait(false) is { Inner.Length: > 0 } inside)
        {
            await InContainerAsync(() => inside.View.SetModified(inside.Inner, modified), cancellationToken).ConfigureAwait(false);
            return;
        }

        await SetModifiedPlainAsync(path, modified, cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> SetModifiedPlainAsync(string path, DateTimeOffset modified, CancellationToken cancellationToken) => CallAsync(async c =>
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

    /// <summary>
    /// Commits what the containers used through this client have gathered. A failure here is a failure of the work
    /// that was done through the client: it is thrown once everything is closed.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        IOException? failed = null;
        if (_containers.Count > 0)
        {
            var views = _containers.Values.ToList();
            _containers.Clear();
            await Task.Run(() =>
            {
                foreach (var view in views)
                {
                    try
                    {
                        view.Flush();
                    }
                    catch (IOException ex)
                    {
                        failed ??= ex;
                    }

                    view.Dispose();
                }
            }).ConfigureAwait(false);
        }

        _notContainers.Clear();
        _client?.Dispose();
        _client = null;
        if (failed is not null)
        {
            throw new RemoteException("The last changes to the encrypted container were not saved: " + failed.Message, failed);
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken) =>
        _containers.Count == 0
            ? Task.CompletedTask
            : InContainerAsync(
                () =>
                {
                    foreach (var view in _containers.Values)
                    {
                        view.Flush();
                    }
                },
                cancellationToken);

    /// <summary>Commits the changes gathered for the container at <paramref name="path"/> (or holding it) now.</summary>
    public async Task FlushContainerAsync(string path, CancellationToken cancellationToken)
    {
        if (await ContainerAsync(path, cancellationToken).ConfigureAwait(false) is { } inside)
        {
            await InContainerAsync(inside.View.Flush, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Replaces the password of the container at <paramref name="path"/>; false when there is no container there.
    /// <see cref="PceWrongPasswordException"/> when <paramref name="current"/> does not open it.
    /// </summary>
    public async Task<bool> ChangeContainerPasswordAsync(string path, string current, string newPassword, CancellationToken cancellationToken)
    {
        if (await ContainerAsync(path, cancellationToken).ConfigureAwait(false) is not { Inner.Length: 0 } inside)
        {
            return false;
        }

        var (bucket, key) = FileKey(path);
        var store = new S3PceObjectStore(_client!, bucket, key);
        await InContainerAsync(
            () =>
            {
                inside.View.Flush();
                using var container = PceObjectContainer.Open(store, current, writable: true, RemotePath.NameOf(path));
                container.ChangePassword(newPassword);
            },
            cancellationToken).ConfigureAwait(false);
        Forget(inside.View); // it holds the old header; the next use opens the container afresh
        return true;
    }

    /// <summary>Whether <paramref name="path"/> is a container kept as objects (the folder itself, not something inside it).</summary>
    public async Task<bool> IsContainerAsync(string path, CancellationToken cancellationToken) =>
        await ContainerAsync(path, cancellationToken).ConfigureAwait(false) is { Inner.Length: 0 };

    /// <summary>The store under a container folder, for making a container there or reading one whole.</summary>
    internal async Task<IPceObjectStore> ContainerStoreAsync(string path, CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            await ConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        var (bucket, key) = FileKey(path);
        return new S3PceObjectStore(_client!, bucket, key);
    }

    /// <summary>
    /// The container <paramref name="path"/> leads into (or is) and the path inside it; null for every other path.
    /// The candidate is the first folder on the path called "*.pce"; it is a container when it holds a "head" object.
    /// </summary>
    private async Task<(PceRemoteView View, string Inner)?> ContainerAsync(string path, CancellationToken cancellationToken)
    {
        var (bucket, key) = Split(path);
        if (bucket is null || key.Length == 0)
        {
            return null;
        }

        var segments = key.Split('/');
        var at = Array.FindIndex(segments, PceFormat.IsPceName);
        if (at < 0)
        {
            return null;
        }

        var rootKey = string.Join('/', segments[..(at + 1)]);
        var id = bucket + "/" + rootKey;
        var inner = string.Join('/', segments[(at + 1)..]);
        if (_containers.TryGetValue(id, out var known))
        {
            return (known, inner);
        }

        if (_notContainers.TryGetValue(id, out var checkedAt) && DateTime.UtcNow - checkedAt < NotAContainerFor && DateTime.UtcNow >= checkedAt)
        {
            return null;
        }

        var isContainer = await CallAsync(async c => await HeadAsync(c, bucket, rootKey + "/" + PceObjectNames.Head, cancellationToken).ConfigureAwait(false) is not null, cancellationToken).ConfigureAwait(false);
        if (!isContainer)
        {
            _notContainers[id] = DateTime.UtcNow;
            return null;
        }

        _notContainers.Remove(id);
        var root = RemotePath.RootOf(RemoteProtocol.S3, _site.User, _site.Host.Trim(), _site.EffectivePort) + "/" + id;
        var view = new PceRemoteView(new S3PceObjectStore(_client!, bucket, rootKey), root);
        _containers[id] = view;
        return (view, inner);
    }

    /// <summary>
    /// Container work blocks, so it runs off the caller's thread; its failures (a refused password, damage, a part
    /// gone) pass as they are, since they say more than a server's message would.
    /// </summary>
    private static async Task<T> InContainerAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(work, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not IOException)
        {
            throw new RemoteException(ex.Message, ex);
        }
    }

    private static Task<bool> InContainerAsync(Action work, CancellationToken cancellationToken) =>
        InContainerAsync(
            () =>
            {
                work();
                return true;
            },
            cancellationToken);

    private void Forget(PceRemoteView view)
    {
        foreach (var id in _containers.Where(p => ReferenceEquals(p.Value, view)).Select(p => p.Key).ToList())
        {
            _containers.Remove(id);
        }

        view.Dispose();
    }

    /// <summary>
    /// Deleting the container's folder: allowed once nothing is left in it (as for any folder), and then every
    /// object under it goes — the header, the indexes, what was not yet tidied away.
    /// </summary>
    private async Task DeleteContainerAsync(string path, PceRemoteView view, CancellationToken cancellationToken)
    {
        await InContainerAsync(
            () =>
            {
                if (view.List(string.Empty) is { Count: > 0 })
                {
                    throw new IOException($"\"{RemotePath.NameOf(path)}\" is not empty.");
                }

                view.Flush();
            },
            cancellationToken).ConfigureAwait(false);
        Forget(view);
        var (bucket, key) = FileKey(path);
        var store = new S3PceObjectStore(_client!, bucket, key);
        await InContainerAsync(
            () =>
            {
                store.DiscardUploads(DateTime.MaxValue, cancellationToken);
                foreach (var item in store.List(string.Empty, int.MaxValue, cancellationToken))
                {
                    store.Delete(item.Name, cancellationToken);
                }
            },
            cancellationToken).ConfigureAwait(false);
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
