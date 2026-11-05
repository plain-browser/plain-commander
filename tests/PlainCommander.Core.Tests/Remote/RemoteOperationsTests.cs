using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Remote;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Remote;

/// <summary>The operations against a folder posing as a server: same code paths as with SSH.NET/FluentFTP below.</summary>
public sealed class RemoteOperationsTests : IDisposable
{
    private static readonly DateTime T0 = new(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly Sandbox _server = new();
    private readonly Sandbox _local = new();

    public void Dispose()
    {
        _server.Dispose();
        _local.Dispose();
    }

    [Fact]
    public async Task Downloads_a_folder_tree_with_times_and_no_part_files()
    {
        _server.File(Path.Combine("site", "index.html"), "<h1>", T0);
        _server.File(Path.Combine("site", "css", "a.css"), "body{}", T0.AddDays(1));
        _server.Dir("site", "empty");
        await using var server = new FolderServer(_server.Root);

        var result = await Run.Async(new RemoteDownloadOperation(server.Connections, [FolderServer.At("/site")], new TransferTarget(_local.Root, null)));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        _local.Read(Path.Combine("site", "css", "a.css")).Should().Be("body{}");
        File.GetLastWriteTimeUtc(_local.P("site", "index.html")).Should().Be(T0);
        Directory.Exists(_local.P("site", "empty")).Should().BeTrue();
        Directory.EnumerateFiles(_local.Root, "*.part", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Download_conflicts_use_the_usual_questions()
    {
        _server.File("a.txt", "server", T0.AddDays(1));
        _server.File("b.txt", "server", T0);
        _local.File("a.txt", "local", T0);
        _local.File("b.txt", "local", T0);
        await using var server = new FolderServer(_server.Root);
        var prompts = new ScriptedPrompts([ConflictChoice.Skip, ConflictChoice.KeepBoth]);

        var result = await Run.Async(new RemoteDownloadOperation(server.Connections, [FolderServer.At("/a.txt"), FolderServer.At("/b.txt")], new TransferTarget(_local.Root, null)), prompts);

        result.Skipped.Should().Be(1);
        _local.Read("a.txt").Should().Be("local");
        _local.Read("b (2).txt").Should().Be("server");
        prompts.Conflicts[0].SourcePath.Should().Be(FolderServer.At("/a.txt"));
    }

    [Fact]
    public async Task F6_from_the_server_removes_what_was_downloaded()
    {
        _server.File(Path.Combine("logs", "1.log"), "1");
        _server.File(Path.Combine("logs", "deep", "2.log"), "2");
        await using var server = new FolderServer(_server.Root);

        await Run.Async(new RemoteDownloadOperation(server.Connections, [FolderServer.At("/logs")], new TransferTarget(_local.Root, null), move: true));

        _local.Read(Path.Combine("logs", "deep", "2.log")).Should().Be("2");
        Directory.Exists(_server.P("logs")).Should().BeFalse();
    }

    [Fact]
    public async Task Uploads_a_tree_under_a_new_name_with_local_times()
    {
        _local.File(Path.Combine("dist", "app.js"), "js", T0);
        _local.File(Path.Combine("dist", "img", "logo.svg"), "<svg/>", T0);
        _server.Dir("www");
        await using var server = new FolderServer(_server.Root);

        var result = await Run.Async(new RemoteUploadOperation(server.Connections, [_local.P("dist")], FolderServer.At("/www"), newName: "v2"));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        _server.Read(Path.Combine("www", "v2", "img", "logo.svg")).Should().Be("<svg/>");
        File.GetLastWriteTimeUtc(_server.P("www", "v2", "app.js")).Should().Be(T0);
        Directory.EnumerateFiles(_server.Root, "*.part", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Upload_conflicts_overwrite_only_older_files_when_asked()
    {
        _local.File("new.txt", "local", T0.AddDays(1));
        _local.File("old.txt", "local", T0);
        _server.File("new.txt", "server", T0);
        _server.File("old.txt", "server", T0.AddDays(2));
        await using var server = new FolderServer(_server.Root);
        var prompts = new ScriptedPrompts([ConflictChoice.OverwriteAllOlder]);

        var result = await Run.Async(new RemoteUploadOperation(server.Connections, [_local.P("new.txt"), _local.P("old.txt")], FolderServer.At("/")), prompts);

        prompts.Conflicts.Should().ContainSingle("the answer applies to the rest");
        _server.Read("new.txt").Should().Be("local");
        _server.Read("old.txt").Should().Be("server");
        result.Skipped.Should().Be(1);
    }

    [Fact]
    public async Task Upload_keep_both_picks_a_free_server_name()
    {
        _local.File("r.pdf", "new");
        _server.File("r.pdf", "1");
        _server.File("r (2).pdf", "2");
        await using var server = new FolderServer(_server.Root);

        await Run.Async(new RemoteUploadOperation(server.Connections, [_local.P("r.pdf")], FolderServer.At("/")), new ScriptedPrompts([ConflictChoice.KeepBoth]));

        _server.Read("r (3).pdf").Should().Be("new");
        _server.Read("r.pdf").Should().Be("1");
    }

    [Fact]
    public async Task F6_to_the_server_removes_the_local_tree()
    {
        _local.File(Path.Combine("out", "a.txt"), "a");
        _local.File(Path.Combine("out", "sub", "b.txt"), "b");
        await using var server = new FolderServer(_server.Root);

        await Run.Async(new RemoteUploadOperation(server.Connections, [_local.P("out")], FolderServer.At("/"), move: true));

        _server.Read(Path.Combine("out", "sub", "b.txt")).Should().Be("b");
        Directory.Exists(_local.P("out")).Should().BeFalse();
    }

    [Fact]
    public async Task A_file_where_a_folder_should_go_is_an_error_to_skip()
    {
        _local.File(Path.Combine("x", "a.txt"), "a");
        _server.File("x", "a file named like the folder");
        await using var server = new FolderServer(_server.Root);
        var prompts = new ScriptedPrompts(errors: [ErrorChoice.SkipAll]);

        var result = await Run.Async(new RemoteUploadOperation(server.Connections, [_local.P("x")], FolderServer.At("/")), prompts);

        result.Outcome.Should().Be(OperationOutcome.CompletedWithSkips);
        prompts.Errors[0].Should().BeOfType<OperationRuleException>().Which.Violation.Should().Be(RuleViolation.TypeMismatch);
    }

    [Fact]
    public async Task Deletes_folders_with_content()
    {
        _server.File(Path.Combine("old", "a", "b", "c.txt"));
        _server.File("keep.txt");
        await using var server = new FolderServer(_server.Root);

        var result = await Run.Async(new RemoteDeleteOperation(server.Connections, [FolderServer.At("/old")]));

        result.Done.Should().Be(4);
        Directory.Exists(_server.P("old")).Should().BeFalse();
        File.Exists(_server.P("keep.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task A_dropped_connection_during_a_copy_is_retried_on_a_new_one()
    {
        _server.File("a.txt", "a");
        await using var server = new FolderServer(_server.Root);
        await server.Connections.UseAsync(FolderServer.Root, _ => Task.FromResult(0), CancellationToken.None); // log in first
        var prompts = new ScriptedPrompts(errors: [ErrorChoice.Retry]);
        server.DropNextCalls = 1;

        var result = await Run.Async(new RemoteDownloadOperation(server.Connections, [FolderServer.At("/a.txt")], new TransferTarget(_local.Root, null)), prompts);

        prompts.Errors.Should().ContainSingle().Which.Should().BeOfType<RemoteException>();
        result.Outcome.Should().Be(OperationOutcome.Completed);
        _local.Read("a.txt").Should().Be("a");
    }

    [Fact]
    public async Task Temp_copies_for_F3_and_the_way_back_for_F4()
    {
        _server.File(Path.Combine("etc", "app.conf"), "port=1", T0);
        await using var server = new FolderServer(_server.Root);

        var temp = await RemoteDownloadOperation.ToTempAsync(server.Connections, FolderServer.At("/etc/app.conf"), CancellationToken.None);
        File.ReadAllText(temp).Should().Be("port=1");
        File.GetLastWriteTimeUtc(temp).Should().Be(T0);

        await File.WriteAllTextAsync(temp, "port=2", TestContext.Current.CancellationToken);
        await RemoteUploadOperation.UploadFileAsync(server.Connections, temp, FolderServer.At("/etc/app.conf"), CancellationToken.None);

        _server.Read(Path.Combine("etc", "app.conf")).Should().Be("port=2");
        Directory.Delete(Path.GetDirectoryName(temp)!, recursive: true);
    }

    [Fact]
    public async Task The_file_system_lists_server_folders_with_a_parent_link_and_hidden_dot_files()
    {
        _server.File(Path.Combine("home", ".bashrc"));
        _server.File(Path.Combine("home", "notes.txt"), "12345");
        await using var server = new FolderServer(_server.Root);
        var fs = new RemoteFileSystem(new LocalFileSystem(), server.Connections);

        var visible = await fs.ListAsync(FolderServer.At("/home"), includeHidden: false, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);
        var all = await fs.ListAsync(FolderServer.At("/home"), includeHidden: true, TestContext.Current.CancellationToken).ToListAsync(TestContext.Current.CancellationToken);

        visible.Select(e => e.Name).Should().Equal("..", "notes.txt");
        visible[0].FullPath.Should().Be(FolderServer.At("/"));
        visible[1].Size.Should().Be(5);
        visible[1].FullPath.Should().Be(FolderServer.At("/home/notes.txt"));
        all.Single(e => e.Name == ".bashrc").IsHidden.Should().BeTrue();
        fs.GetParent(FolderServer.At("/")).Should().BeNull();
        fs.Watch(FolderServer.At("/home"), () => { }).Should().BeNull();
    }
}
