using AwesomeAssertions;
using PlainCommander.Core.Archives;
using PlainCommander.Core.Crypto;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Remote;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Remote;

/// <summary>
/// A .pce container kept as objects on a real S3 server, used through the same client, file system and operations
/// as any server folder. Opt-in like <see cref="S3PceObjectStoreTests"/>: PC_PCE_S3 = "http://host:port".
/// </summary>
public sealed class S3PceContainerFolderTests : IAsyncDisposable
{
    private const string Password = "tajne";
    private static readonly string? Endpoint = Environment.GetEnvironmentVariable("PC_PCE_S3");
    private static readonly DateTime T0 = new(2024, 3, 4, 5, 6, 0, DateTimeKind.Utc);
    private readonly Sandbox _local = new();
    private RemoteConnections? _connections;
    private string _folder = string.Empty;

    private string Box => _folder + "/box.pce";

    public async ValueTask DisposeAsync()
    {
        if (_connections is not null)
        {
            try
            {
                var location = RemotePath.Parse(_folder)!;
                var client = (S3RemoteClient)await _connections.OpenAsync(location.Root, CancellationToken.None);
                await using (client)
                {
                    var store = await client.ContainerStoreAsync(location.Inner, CancellationToken.None);
                    store.DiscardUploads(DateTime.MaxValue);
                    foreach (var item in store.List(string.Empty))
                    {
                        store.Delete(item.Name);
                    }
                }
            }
            catch (IOException)
            {
            }

            await _connections.DisposeAsync();
        }

        ArchivePasswords.Forget(Box);
        PceKnownGenerations.Forget(Box);
        _local.Dispose();
    }

    private async Task<RemoteConnections> ConnectAsync()
    {
        Assert.SkipWhen(string.IsNullOrEmpty(Endpoint), "Set PC_PCE_S3 to the endpoint of the S3 test server.");
        var endpoint = new Uri(Endpoint!);
        var root = RemotePath.Parse($"s3://tester@{endpoint.Host}:{endpoint.Port}/pc-test")!;
        _folder = root.Child("pc-box-" + Guid.NewGuid().ToString("N")[..8]).FullPath;
        var secrets = new MemorySecrets();
        secrets.Write(RemoteConnections.SecretKey(root.Root), "secretsecret");
        RemoteSite[] sites = [new RemoteSite { Protocol = RemoteProtocol.S3, Host = root.Host, Port = root.Port, User = root.User, PlainHttp = true }];
        _connections = new RemoteConnections(new ScriptedRemotePrompts { Trust = TrustChoice.Once }, secrets, new Dictionary<string, string>(), () => sites);
        await _connections.UseAsync(root.Root, async c => await c.StatAsync(root.Inner, CancellationToken.None) is null ? await Make(c) : 0, CancellationToken.None);
        async Task<int> Make(IRemoteClient c)
        {
            await c.CreateDirectoryAsync(root.Inner, CancellationToken.None);
            return 0;
        }

        // An empty container where the tests work, as "Encrypt" with the server as the target makes it.
        var client = (S3RemoteClient)await _connections.OpenAsync(root.Root, CancellationToken.None);
        await using (client)
        {
            var store = await client.ContainerStoreAsync(RemotePath.Parse(Box)!.Inner, CancellationToken.None);
            PceObjectContainer.Create(store, Password, PceOptions.Fast).Dispose();
        }

        ArchivePasswords.Remember(Box, Password);
        return _connections;
    }

    private static byte[] Bytes(int count, int seed = 0)
    {
        var data = new byte[count];
        new Random(count * 31 + seed).NextBytes(data);
        return data;
    }

    private async Task<List<string>> NamesAsync(string path)
    {
        var fs = new RemoteFileSystem(new LocalFileSystem(), _connections!);
        var token = TestContext.Current.CancellationToken;
        return await fs.ListAsync(path, includeHidden: true, token).Where(e => !e.IsParentLink).Select(e => e.Name).ToListAsync(token);
    }

    [Fact]
    public async Task Files_go_in_and_out_of_the_container_through_the_ordinary_operations()
    {
        var connections = await ConnectAsync();
        var token = TestContext.Current.CancellationToken;
        var big = Bytes(20 * (1 << 20), 1);
        Directory.CreateDirectory(_local.P("src", "deep", "pusty"));
        File.WriteAllBytes(_local.P("src", "duży.bin"), big);
        File.WriteAllText(_local.P("src", "zażółć gęślą.txt"), string.Concat(Enumerable.Repeat("tajna treść umowy\n", 5000)));
        for (var i = 0; i < 30; i++)
        {
            File.WriteAllBytes(_local.P("src", "deep", $"f{i:D2}.bin"), Bytes(2000 + i, i));
        }

        foreach (var file in Directory.GetFiles(_local.P("src"), "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, T0);
        }

        (await NamesAsync(_folder)).Should().Equal("box.pce");
        (await NamesAsync(Box)).Should().BeEmpty();

        var up = await Run.Async(new RemoteUploadOperation(connections, [_local.P("src")], Box), token: token);
        up.Outcome.Should().Be(OperationOutcome.Completed);
        (await NamesAsync(Box)).Should().Equal("src");
        (await NamesAsync(Box + "/src")).Should().BeEquivalentTo("duży.bin", "zażółć gęślą.txt", "deep");
        (await NamesAsync(Box + "/src/deep")).Should().HaveCount(31).And.Contain("pusty");

        // On the server there is only the container's own objects, and few of them: 33 files, one small pack.
        var location = RemotePath.Parse(Box)!;
        var raw = (S3RemoteClient)await connections.OpenAsync(location.Root, token);
        await using (raw)
        {
            var store = await raw.ContainerStoreAsync(location.Inner, token);
            var objects = store.List(string.Empty);
            objects.Select(o => o.Name).Should().OnlyContain(n => n == "head" || n.StartsWith("index/", StringComparison.Ordinal) || n.StartsWith("data/", StringComparison.Ordinal));
            objects.Count(o => o.Name.StartsWith("data/", StringComparison.Ordinal)).Should().Be(2, "the large file and one pack");
            objects.Count(o => o.Name.StartsWith("index/", StringComparison.Ordinal)).Should().BeLessThanOrEqualTo(4, "not an index per file");
        }

        var down = await Run.Async(new RemoteDownloadOperation(connections, [Box + "/src"], new TransferTarget(_local.Root, "back")), token: token);
        down.Outcome.Should().Be(OperationOutcome.Completed);
        File.ReadAllBytes(_local.P("back", "duży.bin")).AsSpan().SequenceEqual(big).Should().BeTrue();
        File.ReadAllText(_local.P("back", "zażółć gęślą.txt")).Should().StartWith("tajna treść umowy").And.HaveLength(18 * 5000);
        File.ReadAllBytes(_local.P("back", "deep", "f17.bin")).Should().Equal(Bytes(2017, 17));
        File.GetLastWriteTimeUtc(_local.P("back", "deep", "f17.bin")).Should().Be(T0);
        Directory.Exists(_local.P("back", "deep", "pusty")).Should().BeTrue();

        var delete = await Run.Async(new RemoteDeleteOperation(connections, [Box + "/src/deep", Box + "/src/duży.bin"]), token: token);
        delete.Outcome.Should().Be(OperationOutcome.Completed);
        (await NamesAsync(Box + "/src")).Should().Equal("zażółć gęślą.txt");

        await connections.UseAsync(location.Root, async c =>
        {
            await c.RenameAsync(location.Inner + "/src", location.Inner + "/umowy", token);
            await ((S3RemoteClient)c).FlushContainerAsync(location.Inner, token);
            return 0;
        }, token);
        (await NamesAsync(Box)).Should().Equal("umowy");

        // The folder itself is the container: deleting it takes its objects, and nothing is left of the test folder.
        var remove = await Run.Async(new RemoteDeleteOperation(connections, [Box]), token: token);
        remove.Outcome.Should().Be(OperationOutcome.Completed);
        (await connections.UseAsync(location.Root, c => c.StatAsync(location.Inner, token), token)).Should().BeNull();
    }

    [Fact]
    public async Task The_container_folder_moves_as_its_objects_and_a_wrong_password_opens_nothing()
    {
        var connections = await ConnectAsync();
        var token = TestContext.Current.CancellationToken;
        var location = RemotePath.Parse(Box)!;
        File.WriteAllBytes(_local.P("a.bin"), Bytes(5000));
        (await Run.Async(new RemoteUploadOperation(connections, [_local.P("a.bin")], Box), token: token)).Outcome.Should().Be(OperationOutcome.Completed);

        var moved = _folder + "/przeniesiony.pce";
        var movedInner = RemotePath.Parse(moved)!.Inner;
        await connections.UseAsync(location.Root, async c =>
        {
            (await ((S3RemoteClient)c).IsContainerAsync(location.Inner, token)).Should().BeTrue();
            await c.RenameAsync(location.Inner, movedInner, token);
            (await ((S3RemoteClient)c).IsContainerAsync(location.Inner, token)).Should().BeFalse();
            (await ((S3RemoteClient)c).IsContainerAsync(movedInner, token)).Should().BeTrue();
            return 0;
        }, token);

        try
        {
            ArchivePasswords.Remember(moved, "złe");
            var refused = async () => await NamesAsync(moved);
            (await refused.Should().ThrowAsync<ArchivePasswordException>()).Which.Wrong.Should().BeTrue();

            ArchivePasswords.Remember(moved, Password);
            (await NamesAsync(moved)).Should().Equal("a.bin");
            await connections.UseAsync(location.Root, async c =>
            {
                (await ((S3RemoteClient)c).ChangeContainerPasswordAsync(movedInner, "nowe", token)).Should().BeTrue();
                (await ((S3RemoteClient)c).ChangeContainerPasswordAsync(movedInner + "/a.bin", "x", token)).Should().BeFalse();
                return 0;
            }, token);
            ArchivePasswords.Remembered(moved).Should().Be("nowe");

            var down = await Run.Async(new RemoteDownloadOperation(connections, [moved + "/a.bin"], new TransferTarget(_local.Root, "back.bin")), token: token);
            down.Outcome.Should().Be(OperationOutcome.Completed);
            File.ReadAllBytes(_local.P("back.bin")).Should().Equal(Bytes(5000));
        }
        finally
        {
            ArchivePasswords.Forget(moved);
            PceKnownGenerations.Forget(moved);
        }
    }

    [Fact]
    public async Task A_folder_called_pce_that_is_no_container_stays_an_ordinary_folder()
    {
        var connections = await ConnectAsync();
        var token = TestContext.Current.CancellationToken;
        var plain = _folder + "/zwykły.pce";
        File.WriteAllText(_local.P("a.txt"), "jawne");
        await connections.UseAsync(RemotePath.Parse(plain)!.Root, async c =>
        {
            await c.CreateDirectoryAsync(RemotePath.Parse(plain)!.Inner, token);
            return 0;
        }, token);
        (await Run.Async(new RemoteUploadOperation(connections, [_local.P("a.txt")], plain), token: token)).Outcome.Should().Be(OperationOutcome.Completed);
        (await NamesAsync(plain)).Should().Equal("a.txt");
        var copy = new MemoryStream();
        await connections.UseAsync(RemotePath.Parse(plain)!.Root, async c =>
        {
            await c.DownloadAsync(RemotePath.Parse(plain)!.Inner + "/a.txt", copy, 0, token);
            return 0;
        }, token);
        System.Text.Encoding.UTF8.GetString(copy.ToArray()).Should().Be("jawne");
    }
}
