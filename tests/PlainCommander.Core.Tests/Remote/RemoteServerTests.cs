using AwesomeAssertions;
using PlainCommander.Core.Compare;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Remote;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Remote;

/// <summary>
/// Against real servers (SSH.NET and FluentFTP over the network). Opt-in: set PC_REMOTE_TESTS to a host running
/// the test containers, e.g. "localhost" after
///   docker run -d -p 2222:22 atmoz/sftp:alpine tester:secret:::upload
///   docker run -d -p 2121:21 -p 21000-21010:21000-21010 -e USERS="tester|secret" -e ADDRESS=127.0.0.1 -e MIN_PORT=21000 -e MAX_PORT=21010 delfer/alpine-ftp-server
///   (FTPS: the same image on 2990 with TLS_CERT/TLS_KEY and ports 21020-21030)
///   docker run -d -p 9000:9000 -e RUSTFS_ACCESS_KEY=tester -e RUSTFS_SECRET_KEY=secretsecret rustfs/rustfs /data   (S3, MinIO-compatible)
/// User "tester", password "secret" (S3: "secretsecret", 8 characters at least; plain HTTP).
/// </summary>
public sealed class RemoteServerTests : IDisposable
{
    private static readonly string? Host = Environment.GetEnvironmentVariable("PC_REMOTE_TESTS");
    private static readonly DateTime T0 = new(2024, 3, 4, 5, 6, 0, DateTimeKind.Utc);
    private readonly Sandbox _local = new();

    public void Dispose() => _local.Dispose();

    public static TheoryData<string> Servers => new()
    {
        "sftp://tester@{0}:2222/upload",
        "ftp://tester@{0}:2121/ftp/tester",
        "ftps://tester@{0}:2990/ftp/tester",
        "s3://tester@{0}:9000/pc-test",
    };

    private static bool IsS3(string template) => template.StartsWith("s3", StringComparison.Ordinal);

    private static string PasswordFor(string template) => IsS3(template) ? "secretsecret" : "secret";

    private static (RemoteConnections Connections, ScriptedRemotePrompts Prompts, string Folder) Connect(string template)
    {
        if (string.IsNullOrEmpty(Host))
        {
            Assert.Skip("Set PC_REMOTE_TESTS to the host of the test servers.");
        }

        var location = RemotePath.Parse(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Host))!;
        var prompts = new ScriptedRemotePrompts { Trust = TrustChoice.Once };
        var secrets = new MemorySecrets();
        secrets.Write(RemoteConnections.SecretKey(location.Root), PasswordFor(template));
        var connections = new RemoteConnections(prompts, secrets, new Dictionary<string, string>(), () => Sites(location));
        if (IsS3(template))
        {
            // the bucket the tests work in
            connections.UseAsync(location.Root, async c => await c.StatAsync(location.Inner, CancellationToken.None) is null ? await Create(c) : 0, CancellationToken.None).GetAwaiter().GetResult();
            async Task<int> Create(IRemoteClient c)
            {
                await c.CreateDirectoryAsync(location.Inner, CancellationToken.None);
                return 0;
            }
        }

        var folder = location.Child("pc-" + Guid.NewGuid().ToString("N")[..8]).FullPath;
        return (connections, prompts, folder);
    }

    /// <summary>The S3 server in the tests speaks plain HTTP, which only a saved site can say.</summary>
    private static List<RemoteSite> Sites(RemoteLocation location) => location.Protocol == RemoteProtocol.S3
        ? [new RemoteSite { Protocol = RemoteProtocol.S3, Host = location.Host, Port = location.Port, User = location.User, PlainHttp = true }]
        : [];

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task Client_round_trip(string template)
    {
        var (connections, prompts, folder) = Connect(template);
        await using var _ = connections;
        var location = RemotePath.Parse(folder)!;
        var token = TestContext.Current.CancellationToken;

        var client = await connections.OpenAsync(location.Root, token);
        await using (client)
        {
            await client.CreateDirectoryAsync(location.Inner, token);
            var file = RemotePath.JoinInner(location.Inner, "zażółć.txt");
            await client.UploadAsync(new MemoryStream("gęślą jaźń"u8.ToArray()), file, false, token);
            await client.SetModifiedAsync(file, T0, token);

            var listed = (await client.ListAsync(location.Inner, token)).Should().ContainSingle().Subject;
            listed.Name.Should().Be("zażółć.txt");
            listed.Size.Should().Be("gęślą jaźń"u8.Length);
            listed.Modified.UtcDateTime.Date.Should().Be(T0.Date, "FTP listings of old files carry the day only; S3 reads the time from metadata");

            (await client.GetModifiedAsync(file, token))!.Value.UtcDateTime.Should().BeCloseTo(T0, TimeSpan.FromMinutes(1));
            (await client.StatAsync(RemotePath.JoinInner(location.Inner, "none"), token)).Should().BeNull();

            var renamed = RemotePath.JoinInner(location.Inner, "b.txt");
            await client.RenameAsync(file, renamed, token);
            var copy = new MemoryStream();
            await client.DownloadAsync(renamed, copy, 0, token);
            System.Text.Encoding.UTF8.GetString(copy.ToArray()).Should().Be("gęślą jaźń");

            await client.DeleteFileAsync(renamed, token);
            await client.DeleteDirectoryAsync(location.Inner, token);
            (await client.StatAsync(location.Inner, token)).Should().BeNull();
        }

        if (template.StartsWith("ftps", StringComparison.Ordinal))
        {
            prompts.HostQuestions.Should().NotBeEmpty("the test certificate is self-signed");
            prompts.HostQuestions[0].Identity.Kind.Should().Contain("CN=pc-test-ftp");
        }
        else if (template.StartsWith("sftp", StringComparison.Ordinal))
        {
            prompts.HostQuestions.Should().NotBeEmpty().And.AllSatisfy(q => q.Identity.Fingerprint.Should().StartWith("SHA256:"));
        }
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task Operations_up_and_down(string template)
    {
        var (connections, _, folder) = Connect(template);
        await using var __ = connections;
        _local.File(Path.Combine("src", "a.txt"), "a", T0);
        _local.File(Path.Combine("src", "deep", "b.bin"), new string('x', 300_000), T0);

        var up = await Run.Async(new RemoteUploadOperation(connections, [_local.P("src")], RemotePath.Parse(folder)!.Parent!.FullPath, newName: RemotePath.NameOf(folder)));
        up.Outcome.Should().Be(OperationOutcome.Completed);

        var fs = new RemoteFileSystem(new LocalFileSystem(), connections);
        var names = await fs.ListAsync(folder, includeHidden: true, TestContext.Current.CancellationToken).Select(e => e.Name).ToListAsync(TestContext.Current.CancellationToken);
        names.Should().BeEquivalentTo(["..", "a.txt", "deep"]);

        var down = await Run.Async(new RemoteDownloadOperation(connections, [folder], new TransferTarget(_local.Root, "back"), move: true));
        down.Outcome.Should().Be(OperationOutcome.Completed);
        _local.Read(Path.Combine("back", "deep", "b.bin")).Length.Should().Be(300_000);
        File.GetLastWriteTimeUtc(_local.P("back", "a.txt")).Should().BeCloseTo(T0, TimeSpan.FromMinutes(1));

        var left = await connections.UseAsync(RemotePath.Parse(folder)!.Root, c => c.StatAsync(RemotePath.Parse(folder)!.Inner, CancellationToken.None), CancellationToken.None);
        left.Should().BeNull("F6 removes the server copy");
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task Broken_transfers_continue_where_they_stopped(string template)
    {
        var (connections, _, folder) = Connect(template);
        await using var __ = connections;
        var location = RemotePath.Parse(folder)!;
        var token = TestContext.Current.CancellationToken;
        var data = new byte[700_000];
        new Random(7).NextBytes(data);
        var whole = _local.P("whole.bin");
        File.WriteAllBytes(whole, data);
        File.SetLastWriteTimeUtc(whole, T0);
        var file = RemotePath.JoinInner(location.Inner, "whole.bin");

        // the client calls: append after a first piece, and a download from an offset
        await connections.UseAsync(location.Root, async c =>
        {
            await c.CreateDirectoryAsync(location.Inner, token);
            if (c.AtomicUploads)
            {
                await c.UploadAsync(new MemoryStream(data), file + ".part", false, token); // S3 has no append
            }
            else
            {
                await c.UploadAsync(new MemoryStream(data, 0, 300_000), file + ".part", false, token);
                await using var rest = new MemoryStream(data);
                rest.Position = 300_000;
                await c.UploadAsync(rest, file + ".part", true, token);
            }

            (await c.StatAsync(file + ".part", token))!.Size.Should().Be(data.Length);

            var tail = new MemoryStream();
            await c.DownloadAsync(file + ".part", tail, 650_000, token);
            tail.ToArray().Should().Equal(data[650_000..]);
            await c.DeleteFileAsync(file + ".part", token);

            // a part left on the server by an upload that broke off
            await c.UploadAsync(new MemoryStream(data, 0, 123_456), file + ".part", false, token);
            return 0;
        }, token);

        var up = await Run.Async(new RemoteUploadOperation(connections, [whole], folder));
        up.Outcome.Should().Be(OperationOutcome.Completed);

        // and a part on disk left by a download that broke off
        _local.Dir("back");
        File.WriteAllBytes(_local.P("back", "whole.bin.part"), data[..234_567]);
        var down = await Run.Async(new RemoteDownloadOperation(connections, [RemotePath.Parse(folder)!.Child("whole.bin").FullPath], new TransferTarget(_local.P("back"), null)));
        down.Outcome.Should().Be(OperationOutcome.Completed);
        File.ReadAllBytes(_local.P("back", "whole.bin")).Should().Equal(data);

        await connections.UseAsync(location.Root, async c =>
        {
            await c.DeleteFileAsync(file, token);
            if (await c.StatAsync(file + ".part", token) is not null)
            {
                await c.DeleteFileAsync(file + ".part", token); // S3 uploads skip parts, so the planted one stays
            }

            await c.DeleteDirectoryAsync(location.Inner, token);
            return 0;
        }, token);
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task Synchronize_with_a_disk_folder_and_chmod(string template)
    {
        var (connections, _, folder) = Connect(template);
        await using var __ = connections;
        var location = RemotePath.Parse(folder)!;
        var token = TestContext.Current.CancellationToken;
        _local.File(Path.Combine("L", "a.txt"), "alpha", T0);
        _local.File(Path.Combine("L", "sub", "b.txt"), "beta", T0.AddDays(-40)); // old enough for a day-only FTP listing
        await connections.UseAsync(location.Root, async c => { await c.CreateDirectoryAsync(location.Inner, token); return 0; }, token);

        async Task<CompareResult> CompareAsync()
        {
            await using var side = new RemoteCompareSide(connections, folder);
            return await DirectoryCompare.RunAsync(new DiskCompareSide(_local.P("L")), side, new CompareOptions(Subfolders: true), token: token);
        }

        var before = await CompareAsync();
        before.Items.Should().HaveCount(2).And.AllSatisfy(i => i.Value.Should().Be(Difference.OnlyLeft));
        var sync = await Run.Async(new SyncOperation(_local.P("L"), folder, SyncPlan.From(before), remote: connections), new ScriptedPrompts());
        sync.Outcome.Should().Be(OperationOutcome.Completed);
        (await CompareAsync()).Items.Should().BeEmpty("the server keeps the times, so nothing looks changed after a synchronize");

        if (IsS3(template))
        {
            await connections.UseAsync(location.Root, async c =>
            {
                await c.DeleteFileAsync(RemotePath.JoinInner(location.Inner, "sub/b.txt"), token);
                await c.DeleteDirectoryAsync(RemotePath.JoinInner(location.Inner, "sub"), token);
                await c.DeleteFileAsync(RemotePath.JoinInner(location.Inner, "a.txt"), token);
                await c.DeleteDirectoryAsync(location.Inner, token);
                return 0;
            }, token);
            return; // S3 has no chmod
        }

        var chmod = await Run.Async(new RemoteModeOperation(connections, [location.Child("a.txt").FullPath, location.Child("sub").FullPath], 0x1E0, recursive: true)); // rwxr-----
        chmod.Outcome.Should().Be(OperationOutcome.Completed);
        var listed = await connections.UseAsync(location.Root, c => c.ListAsync(location.Inner, token), token);
        listed.Single(i => i.Name == "a.txt").Mode.Should().Be(0x1E0);
        listed.Single(i => i.Name == "sub").Mode.Should().Be(0x1E0);
        var inside = await connections.UseAsync(location.Root, c => c.ListAsync(RemotePath.JoinInner(location.Inner, "sub"), token), token);
        inside.Single().Mode.Should().Be(0x1E0);

        await connections.UseAsync(location.Root, async c =>
        {
            await c.SetModeAsync(RemotePath.JoinInner(location.Inner, "sub"), 0x1ED, token);
            await c.DeleteFileAsync(RemotePath.JoinInner(location.Inner, "sub/b.txt"), token);
            await c.DeleteDirectoryAsync(RemotePath.JoinInner(location.Inner, "sub"), token);
            await c.DeleteFileAsync(RemotePath.JoinInner(location.Inner, "a.txt"), token);
            await c.DeleteDirectoryAsync(location.Inner, token);
            return 0;
        }, token);
    }

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task A_wrong_password_is_asked_again(string template)
    {
        var (_, _, folder) = Connect(template);
        var location = RemotePath.Parse(folder)!;
        var prompts = new ScriptedRemotePrompts { Trust = TrustChoice.Once };
        prompts.Answers.Enqueue(new CredentialAnswer("wrongwrong", false));
        prompts.Answers.Enqueue(new CredentialAnswer(PasswordFor(template), false));
        await using var connections = new RemoteConnections(prompts, new MemorySecrets(), new Dictionary<string, string>(), () => Sites(location));

        var home = await connections.HomeAsync(location.Root, TestContext.Current.CancellationToken);

        home.Should().StartWith("/");
        prompts.CredentialProblems.Should().HaveCount(2);
        prompts.CredentialProblems[1].Should().NotBeNullOrEmpty();
    }
}
