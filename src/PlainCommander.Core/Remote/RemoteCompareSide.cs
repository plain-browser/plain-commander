using PlainCommander.Core.Compare;

namespace PlainCommander.Core.Remote;

/// <summary>
/// A server folder as one side of Shift+F2. One connection for the whole comparison. FTP listings round times
/// (minutes, or the day for older files), so files present on both sides get their exact time by MDTM.
/// A folder that is not there counts as empty; any other failure stops the comparison, because "empty" would
/// make the synchronize window offer to copy everything.
/// </summary>
public sealed class RemoteCompareSide : ICompareSide, IAsyncDisposable
{
    private readonly RemoteConnections _connections;
    private readonly RemoteLocation _folder;
    private IRemoteClient? _client;

    public RemoteCompareSide(RemoteConnections connections, string virtualFolder)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _folder = RemotePath.Parse(virtualFolder) ?? throw new ArgumentException($"Not a remote path: {virtualFolder}", nameof(virtualFolder));
    }

    public async Task<IReadOnlyList<CompareEntry>> ListAsync(string relative, CancellationToken cancellationToken)
    {
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = await client.ListAsync(Inner(relative), cancellationToken).ConfigureAwait(false);
            return items.Select(i => new CompareEntry(i.Name, i.IsDirectory, i.Size, i.Modified, i.IsLink)).ToList();
        }
        catch (FileNotFoundException)
        {
            return [];
        }
    }

    public async Task<DateTimeOffset?> ExactModifiedAsync(string relative, CancellationToken cancellationToken)
    {
        if (_folder.Protocol == RemoteProtocol.Sftp)
        {
            return null; // SFTP listings carry the time to the second
        }

        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        return await client.GetModifiedAsync(Inner(relative), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Downloads to a temp file that goes away when the stream is closed.</summary>
    public async Task<Stream> OpenReadAsync(string relative, CancellationToken cancellationToken)
    {
        var client = await ClientAsync(cancellationToken).ConfigureAwait(false);
        var temp = Path.Combine(Path.GetTempPath(), "PlainCommander", "compare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await client.DownloadAsync(Inner(relative), stream, 0, cancellationToken).ConfigureAwait(false);
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }
    }

    private string Inner(string relative) =>
        relative.Length == 0 ? _folder.Inner : RemotePath.JoinInner(_folder.Inner, relative.Replace('\\', '/'));

    private async Task<IRemoteClient> ClientAsync(CancellationToken cancellationToken) =>
        _client ??= await _connections.OpenAsync(_folder.Root, cancellationToken).ConfigureAwait(false);
}
