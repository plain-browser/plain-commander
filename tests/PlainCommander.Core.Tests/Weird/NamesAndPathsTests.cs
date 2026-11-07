using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using PlainCommander.Core.Archives;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Network;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Remote;
using PlainCommander.Core.Tests.Operations;
using PlainCommander.Core.Tests.Remote;
using Xunit;

namespace PlainCommander.Core.Tests.Weird;

/// <summary>Stress tests for unusual file names and path shapes. Tests named Regression_* document confirmed bugs and fail.</summary>
public sealed class NamesAndPathsTests : IDisposable
{
    private const string Nfc = "caf\u00e9";
    private const string Nfd = "cafe\u0301";

    private readonly Sandbox _s = new();

    public void Dispose() => _s.Dispose();

    /// <summary>Names every file system the tests run on accepts.</summary>
    private static IEnumerable<string> PortableWeirdNames() =>
    [
        "\ud83d\ude00 emoji \ud83d\udc68\u200d\ud83d\udc69\u200d\ud83d\udc67.txt",
        "z\u0302\u0303\u0304 combining.txt",
        Nfd + ".txt",
        " leading space.txt",
        "-leading-dash",
        "--",
        "100% sure %PATH%.txt",
        "[brackets] {braces}.txt",
        "...hidden dots",
        "a.b.c.d",
        ".bashrc",
        "\u202eright-to-left.txt",
        "\u00a0nbsp.txt",
        "semi; amp & caret ^ bang!.txt",
    ];

    /// <summary>Legal on Linux, forbidden on Windows.</summary>
    private static IEnumerable<string> PosixOnlyWeirdNames() =>
    [
        "back\\slash",
        "star*",
        "question?",
        "trailing dot.",
        "trailing space ",
        "...",
        "a:b",
        "pipe|lt<gt>\"quote\"",
    ];

    private static List<string> WeirdNames() =>
        OperatingSystem.IsWindows() ? PortableWeirdNames().ToList() : PortableWeirdNames().Concat(PosixOnlyWeirdNames()).ToList();

    // ---------------------------------------------------------------- local listing and operations

    [Fact]
    public async Task Listing_keeps_every_weird_name_and_its_full_path_points_at_the_file()
    {
        var dir = _s.Dir("weird");
        foreach (var name in WeirdNames())
        {
            File.WriteAllText(ExactPath.Native(Path.Combine(dir, name)), name);
        }

        var entries = (await new LocalFileSystem().ListAsync(dir, includeHidden: true).ToListAsync()).Where(e => !e.IsParentLink).ToList();

        entries.Select(e => e.Name).Should().BeEquivalentTo(WeirdNames());
        foreach (var entry in entries)
        {
            Path.GetFileName(entry.FullPath).Should().Be(entry.Name);
            File.ReadAllText(ExactPath.Native(entry.FullPath)).Should().Be(entry.Name);
        }
    }

    [Fact]
    public void Nfc_and_nfd_spellings_stay_distinct_where_the_file_system_keeps_them_apart()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "ext4 keeps both normalization forms as separate names");
        var dir = _s.Dir("norm");
        File.WriteAllText(Path.Combine(dir, Nfc), "nfc");
        File.WriteAllText(Path.Combine(dir, Nfd), "nfd");

        ExactPath.Full(Path.Combine(dir, Nfd)).Should().EndWith(Nfd, "no silent normalization of the typed name");
        File.ReadAllText(Path.Combine(dir, Nfc)).Should().Be("nfc");
        File.ReadAllText(Path.Combine(dir, Nfd)).Should().Be("nfd");
        PathRules.Same(Path.Combine(dir, Nfc), Path.Combine(dir, Nfd)).Should().BeFalse();
    }

    [Fact]
    public async Task Copy_of_a_folder_full_of_weird_names_recreates_every_one()
    {
        var src = _s.Dir("src", "w");
        foreach (var name in WeirdNames())
        {
            File.WriteAllText(ExactPath.Native(Path.Combine(src, name)), name);
        }

        _s.Dir("src", "w", Nfc);
        _s.Dir("src", "w", "\ud83d\udcc1 sub");
        File.WriteAllText(Path.Combine(src, "\ud83d\udcc1 sub", "\ud83d\ude00.txt"), "deep");
        var target = _s.Dir("dst");

        var result = await Run.Async(new TransferOperation(TransferKind.Copy, [src], new TransferTarget(target, null)));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        foreach (var name in WeirdNames())
        {
            File.ReadAllText(ExactPath.Native(Path.Combine(target, "w", name))).Should().Be(name);
        }

        File.ReadAllText(Path.Combine(target, "w", "\ud83d\udcc1 sub", "\ud83d\ude00.txt")).Should().Be("deep");
        Directory.Exists(Path.Combine(target, "w", Nfc)).Should().BeTrue();
    }

    [Fact]
    public async Task Move_of_weird_names_keeps_content_and_empties_the_source()
    {
        var src = _s.Dir("msrc");
        var sources = new List<string>();
        foreach (var name in WeirdNames())
        {
            var path = Path.Combine(src, name);
            File.WriteAllText(ExactPath.Native(path), name);
            sources.Add(path);
        }

        var target = _s.Dir("mdst");
        var result = await Run.Async(new TransferOperation(TransferKind.Move, sources, new TransferTarget(target, null)));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        Directory.EnumerateFileSystemEntries(src).Should().BeEmpty();
        Directory.EnumerateFiles(target).Select(Path.GetFileName).Should().BeEquivalentTo(WeirdNames());
    }

    [Fact]
    public async Task Deleting_files_named_star_question_mark_and_brackets_deletes_only_those()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "'*' and '?' are not allowed in Windows names");
        var dir = _s.Dir("wild");
        var star = _s.File(Path.Combine("wild", "*"));
        var question = _s.File(Path.Combine("wild", "?"));
        _s.File(Path.Combine("wild", "keep.txt"));
        _s.File(Path.Combine("wild", "k"));
        _s.Dir("wild", "[a]");
        _s.File(Path.Combine("wild", "a", "inner.txt"));

        await Run.Async(new DeleteOperation([star, question, Path.Combine(dir, "[a]")]));

        Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).Should().BeEquivalentTo(["keep.txt", "k", "a"]);
        File.Exists(Path.Combine(dir, "a", "inner.txt")).Should().BeTrue();
    }

    [Fact]
    public void Names_of_255_utf8_bytes_work_and_255_chars_of_510_bytes_fail_cleanly()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "ext4 limits a name to 255 bytes");
        var dir = _s.Dir("long");
        var bytes255 = new string('\u00e9', 127) + "x";
        Encoding.UTF8.GetByteCount(bytes255).Should().Be(255);
        File.Exists(SimpleCommands.CreateFile(dir, bytes255)).Should().BeTrue();

        var chars255Bytes510 = new string('\u00e9', 255);
        var act = () => SimpleCommands.CreateFile(dir, chars255Bytes510);
        act.Should().Throw<IOException>("the name is 510 bytes: a plain I/O error the UI can show");
    }

    [Fact]
    public async Task A_deep_path_well_past_MAX_PATH_copies()
    {
        var parts = Enumerable.Range(0, 30).Select(i => $"level-{i:00}-\u00e9\u00e9\u00e9\u00e9\u00e9").ToArray();
        var deep = _s.Dir(["deep", .. parts]);
        File.WriteAllText(ExactPath.Native(Path.Combine(deep, "leaf.txt")), "leaf");
        deep.Length.Should().BeGreaterThan(260);
        var target = _s.Dir("deep-out");

        var result = await Run.Async(new TransferOperation(TransferKind.Copy, [_s.P("deep")], new TransferTarget(target, null)));

        result.Outcome.Should().Be(OperationOutcome.Completed);
        File.ReadAllText(Path.Combine([target, "deep", .. parts, "leaf.txt"])).Should().Be("leaf");
    }

    // ---------------------------------------------------------------- simple commands

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("\"\"")]
    [InlineData(" .. ")]
    [InlineData("...")] // only dots: Windows drops trailing dots, nothing is left
    public void Clean_name_rejects_empty_and_dot_names(string name)
    {
        var act = () => SimpleCommands.CleanName(name, allowSeparators: false);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Clean_name_keeps_unicode_untouched_and_trims_outer_spaces_and_quotes()
    {
        SimpleCommands.CleanName("  \"" + Nfd + "\"  ", allowSeparators: false).Should().Be(Nfd);
        SimpleCommands.CleanName("\ud83d\ude00", allowSeparators: false).Should().Be("\ud83d\ude00");
        SimpleCommands.CleanName("..a", allowSeparators: false).Should().Be("..a");
        SimpleCommands.CleanName("-rf", allowSeparators: false).Should().Be("-rf");
    }

    [Fact]
    public void Rename_between_nfc_and_nfd_is_a_real_rename_on_linux()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "ext4 tells the two forms apart");
        var file = _s.File(Nfc, "x");

        var renamed = SimpleCommands.Rename(file, Nfd);

        Path.GetFileName(renamed).Should().Be(Nfd);
        File.Exists(file).Should().BeFalse();
        File.ReadAllText(renamed).Should().Be("x");
    }

    [Fact]
    public void Case_only_rename_on_linux_does_not_overwrite_the_other_spelling()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "case-sensitive file system");
        var lower = _s.File("readme", "lower");
        _s.File("README", "upper");

        var act = () => SimpleCommands.Rename(lower, "README");

        act.Should().Throw<OperationRuleException>();
        _s.Read("README").Should().Be("upper");
        _s.Read("readme").Should().Be("lower");
    }

    // ---------------------------------------------------------------- PathRules

    [Theory]
    [InlineData("report.pdf", "report (2).pdf")]
    [InlineData("noext", "noext (2)")]
    [InlineData("\ud83d\ude00.txt", "\ud83d\ude00 (2).txt")]
    [InlineData("a.b.c", "a.b (2).c")]
    public void Free_name_inserts_the_number_before_the_extension(string name, string expected)
    {
        var path = _s.File(name);

        Path.GetFileName(PathRules.FreeName(path)).Should().Be(expected);
    }

    [Fact]
    public void Is_inside_handles_trailing_separators_and_prefix_lookalikes()
    {
        var sep = Path.DirectorySeparatorChar;
        var a = _s.P("a");
        PathRules.IsInside(_s.P("a", "b"), a + sep).Should().BeTrue();
        PathRules.IsInside(_s.P("ab"), a).Should().BeFalse("'ab' only starts with the same letters");
        PathRules.IsInside(a + sep, a).Should().BeTrue();
        PathRules.IsInside(_s.P("a", "..", "ab"), a).Should().BeFalse("'..' is resolved first");
        PathRules.IsInside(a + sep + sep + "b", a).Should().BeTrue("double separators");
    }

    [Fact]
    public void Regression_PathRules_IsInside_says_nothing_lies_inside_a_root()
    {
        var root = Path.GetPathRoot(_s.Root)!;

        PathRules.IsInside(_s.Root, root).Should().BeTrue("every path on the volume lies below its root");
    }

    // ---------------------------------------------------------------- ExactPath

    [Fact]
    public void Exact_path_full_is_stable_for_odd_linux_shapes()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Linux shapes");
        ExactPath.Full("/tmp//x///y").Should().Be("/tmp/x/y");
        ExactPath.Full("/tmp/./x/../y").Should().Be("/tmp/y");
        ExactPath.Full("/").Should().Be("/");
        ExactPath.Full("/tmp/" + Nfd).Should().Be("/tmp/" + Nfd);
        ExactPath.Full("/tmp/a\\b").Should().Be("/tmp/a\\b", "a backslash is an ordinary character on Linux");
        var act = () => ExactPath.Full("  ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Exact_path_display_strips_prefixes()
    {
        ExactPath.Display(@"\\?\UNC\srv\share\x").Should().Be(@"\\srv\share\x");
        ExactPath.Display(@"\\?\C:\a.").Should().Be(@"C:\a.");
        ExactPath.DisplayText(@"cannot open \\?\C:\a. and \\?\UNC\s\x").Should().Be(@"cannot open C:\a. and \\s\x");
        ExactPath.UpperDrive("c:").Should().Be("C:");
        ExactPath.UpperDrive("/").Should().Be("/");
        ExactPath.UpperDrive(string.Empty).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- archives

    private string MakeZip(string relative, params string[] keys)
    {
        var zip = _s.P(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var key in keys)
        {
            var entry = archive.CreateEntry(key);
            if (!key.EndsWith('/'))
            {
                using var writer = new StreamWriter(entry.Open());
                writer.Write(key);
            }
        }

        return zip;
    }

    [Fact]
    public void Folder_named_like_an_archive_is_not_an_archive_boundary()
    {
        var folder = _s.Dir("data.zip", "inner");
        var real = MakeZip(Path.Combine("data.zip", "inner", "real.zip"), "x.txt");

        ArchivePath.Split(Path.Combine(folder, "real.zip", "x.txt"), File.Exists).Should().Be(new ArchiveLocation(real, "x.txt"));
        ArchivePath.Split(folder, File.Exists).Should().BeNull();
    }

    [Fact]
    public void Split_tolerates_double_and_trailing_separators_and_keeps_nested_archive_names_in_inner()
    {
        var zip = MakeZip("outer.zip", "in.zip", "dir/\ud83d\ude00.txt");
        var sep = Path.DirectorySeparatorChar;

        ArchivePath.Split(zip + sep + sep + "dir" + sep + sep + "\ud83d\ude00.txt" + sep, File.Exists)
            .Should().Be(new ArchiveLocation(zip, "dir/\ud83d\ude00.txt"));
        ArchivePath.Split(zip + sep, File.Exists)!.IsRoot.Should().BeTrue();
        ArchivePath.Split(zip + sep + "in.zip" + sep + "x", File.Exists).Should().Be(new ArchiveLocation(zip, "in.zip/x"));
        ArchivePath.Split(string.Empty, File.Exists).Should().BeNull();
    }

    [Fact]
    public void Archive_entry_names_with_unicode_percent_and_brackets_list_and_extract()
    {
        var zip = MakeZip("u.zip", Nfd + "/100%.txt", "\ud83d\ude00 [x].txt", " lead.txt");
        var catalog = ArchiveCatalog.Open(zip);

        catalog.List(string.Empty).Select(e => e.Name).Should().BeEquivalentTo([Nfd, "\ud83d\ude00 [x].txt", " lead.txt"]);
        catalog.List(Nfd).Single().FullPath.Should().Be(ArchivePath.Join(zip, Nfd + "/100%.txt"));
        var temp = ArchiveExtractOperation.ExtractToTemp(ArchivePath.Join(zip, "\ud83d\ude00 [x].txt"));
        try
        {
            File.ReadAllText(temp).Should().Be("\ud83d\ude00 [x].txt");
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(temp)!, recursive: true);
        }
    }

    [Theory]
    [InlineData("./a/b/", "a/b")]
    [InlineData("/abs/x", "abs/x")]
    [InlineData("././x", "x")]
    [InlineData("a\\b", "a/b")]
    [InlineData("", "")]
    [InlineData("/", "")]
    public void Normalize_key_cases(string key, string expected) => ArchivePath.NormalizeKey(key).Should().Be(expected);

    [Fact]
    public async Task Regression_zip_slip_extracting_an_entry_with_dotdot_writes_outside_the_target()
    {
        var zip = MakeZip(Path.Combine("in", "evil.zip"), "ok.txt", "../../escaped.txt");
        var target = _s.Dir("out", "here");

        await Run.Async(new ArchiveExtractOperation([zip], new TransferTarget(target, null), ownFolders: false), new ScriptedPrompts(errors: [ErrorChoice.Skip, ErrorChoice.Skip]));

        File.Exists(Path.Combine(target, "ok.txt")).Should().BeTrue();
        File.Exists(_s.P("escaped.txt")).Should().BeFalse("an archive entry must never be written outside the chosen target folder");
    }

    [Theory]
    [InlineData("a.tar.gz", "a")]
    [InlineData(".zip", ".zip")]
    [InlineData("\ud83d\ude00 [1].zip", "\ud83d\ude00 [1]")]
    [InlineData("x.y.zip", "x.y")]
    public void Own_folder_names_for_archives(string archive, string folder) => ArchiveExtractOperation.FolderNameFor(archive).Should().Be(folder);

    [Fact]
    public async Task Regression_extracting_an_archive_named_dotdotdot_zip_into_its_own_folder_writes_into_the_parent()
    {
        // "...zip" is a legal name; its stem is "..", so the "own folder" becomes the parent of the target.
        var zip = MakeZip(Path.Combine("in", "...zip"), "inside.txt");
        var target = _s.Dir("out3", "here");

        await Run.Async(new ArchiveExtractOperation([zip], new TransferTarget(target, null)));

        File.Exists(_s.P("out3", "inside.txt")).Should().BeFalse("extraction must stay inside the chosen target folder");
    }

    // ---------------------------------------------------------------- remote paths

    [Theory]
    [InlineData("sftp://user@host", "/")]
    [InlineData("sftp://user@host/", "/")]
    [InlineData("sftp://host//a///b//", "/a/b")]
    [InlineData("sftp://host/../../etc", "/etc")]
    [InlineData("sftp://host/a b/%20/\u00e9", "/a b/%20/\u00e9")]
    [InlineData("sftp://host/home/x@y:z", "/home/x@y:z")]
    public void Remote_inner_paths_normalize(string text, string inner) => RemotePath.Parse(text)!.Inner.Should().Be(inner);

    [Theory]
    [InlineData("sftp://a@b@host/x", "a@b", "host", 22)]
    [InlineData("sftp://us%2Fer@host:2200", "us/er", "host", 2200)]
    [InlineData("ftp://user:pass@host", "user:pass", "host", 21)]
    [InlineData("s3://KEY@s3.eu-west-1.amazonaws.com", "KEY", "s3.eu-west-1.amazonaws.com", 443)]
    [InlineData("sftp://[FE80::1]:22/", "", "fe80::1", 22)]
    [InlineData("sftp://HOST:022/", "", "host", 22)]
    public void Remote_authority_variants_round_trip(string text, string user, string host, int port)
    {
        var location = RemotePath.Parse(text)!;

        (location.User, location.Host, location.Port).Should().Be((user, host, port));
        RemotePath.Parse(location.FullPath).Should().Be(location, "the full path round-trips");
    }

    [Theory]
    [InlineData("sftp://host:0/")]
    [InlineData("sftp://host:/")]
    [InlineData("sftp://[::1/")]
    [InlineData("sftp://user@/x")]
    [InlineData("sftp://host:+22/")]
    public void Remote_rejects_broken_authorities(string text) => RemotePath.Parse(text).Should().BeNull();

    [Fact]
    public void Regression_RemotePath_unbracketed_ipv6_is_split_at_its_last_colon()
    {
        var location = RemotePath.Parse("sftp://2001:db8::1/x");

        location?.Host.Should().Be("2001:db8::1", "an unbracketed IPv6 literal must be rejected or read whole, never split into host '2001:db8:' and port 1");
    }

    [Fact]
    public void Remote_child_and_parent_with_unicode_names()
    {
        var root = RemotePath.Parse("sftp://u@h/")!;
        var child = root.Child("\ud83d\ude00").Child(Nfd);

        child.Inner.Should().Be("/\ud83d\ude00/" + Nfd);
        child.Name.Should().Be(Nfd);
        child.Parent!.Parent!.IsRoot.Should().BeTrue();
        root.Parent.Should().BeNull();
        root.Name.Should().Be("h");
        RemotePath.NameOf("sftp://u@h/a/b/").Should().Be("b");
        RemotePath.ParentInner("/").Should().Be("/");
        RemotePath.ParentInner("/a").Should().Be("/");
    }

    [Fact]
    public async Task Regression_remote_delete_of_a_folder_deletes_a_file_outside_it_when_a_listed_name_has_backslashes()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "the folder posing as a Unix server needs a backslash in a name");
        _s.Dir("srv", "home", "me", "data");
        _s.File(Path.Combine("srv", "home", "me", "data", @"..\keep.txt"), "a Unix file name with backslashes");
        _s.File(Path.Combine("srv", "home", "me", "keep.txt"), "must survive");
        await using var server = new FolderServer(_s.P("srv"));

        await Run.Async(new RemoteDeleteOperation(server.Connections, [FolderServer.At("/home/me/data")]), new ScriptedPrompts(errors: [ErrorChoice.SkipAll]));

        File.Exists(_s.P("srv", "home", "me", "keep.txt")).Should().BeTrue("deleting /home/me/data must not touch /home/me/keep.txt");
    }

    [Theory]
    [InlineData("/", null, "")]
    [InlineData("/bucket", "bucket", "")]
    [InlineData("/bucket/", "bucket", "")]
    [InlineData("/bucket/a/b.txt", "bucket", "a/b.txt")]
    [InlineData("//bucket//a//", "bucket", "a")]
    [InlineData("/bucket/\ud83d\ude00 x/%2F", "bucket", "\ud83d\ude00 x/%2F")]
    public void S3_split_cases(string path, string? bucket, string key) => S3RemoteClient.Split(path).Should().Be((bucket, key));

    // ---------------------------------------------------------------- network paths

    [Theory]
    [InlineData(@"\\pc", "pc")]
    [InlineData(@"\\pc\", "pc")]
    [InlineData(@"\\pc\share\x", "pc")]
    [InlineData(@"\\?\C:\x", null)]
    [InlineData(@"\\.\pipe\x", null)]
    [InlineData(@"\\", null)]
    [InlineData(@"\\\x", null)]
    [InlineData(@"C:\x", null)]
    [InlineData(@"\\192.0.2.10\c$", "192.0.2.10")]
    [InlineData(@"\\fe80--1.ipv6-literal.net\s", "fe80--1.ipv6-literal.net")]
    public void Unc_server_cases(string path, string? server) => NetworkPath.UncServer(path).Should().Be(server);

    [Fact]
    public void Network_places_and_share_roots()
    {
        NetworkPath.AsServerPlace(@"\\pc").Should().Be("net://pc");
        NetworkPath.AsServerPlace(@"\\pc\\").Should().Be("net://pc");
        NetworkPath.AsServerPlace(@"\\pc\share").Should().BeNull();
        NetworkPath.IsShareRoot(@"\\pc\share").Should().BeTrue();
        NetworkPath.IsShareRoot(@"\\pc\share\").Should().BeTrue();
        NetworkPath.IsShareRoot(@"\\pc\share\x").Should().BeFalse();
        NetworkPath.IsShareRoot(@"\\pc\").Should().BeFalse();
        NetworkPath.Normalize("NET://pc/").Should().Be("net://pc");
        NetworkPath.Normalize("net://").Should().Be("net://");
        NetworkPath.Normalize("NET:///").Should().Be("net://");
        NetworkPath.ServerOf("x").Should().BeNull();
    }

    [Fact]
    public void Regression_NetworkPath_unc_with_a_forward_slash_is_taken_for_a_computer()
    {
        // Windows reads "\\pc/share" as the share \\pc\share; typed in Ctrl+L it must not open the share list of a computer "pc/share".
        NetworkPath.UncServer(@"\\pc/share").Should().Be("pc");
        NetworkPath.AsServerPlace(@"\\pc/share").Should().BeNull();
    }
}
