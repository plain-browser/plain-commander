using AwesomeAssertions;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Fuzz;

/// <summary>Entry names from archives and servers joined to a folder: whatever the name, the result stays inside.</summary>
public sealed class PathRulesFuzzTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "pc-fuzz-root", "inner"));

    private static readonly string[] KeyPieces =
    [
        "/", "\\", "..", ".", "...", ". .", " ..", ".. ", "a", "b.txt", "a.", "a ", "C:", "c:", "C:x", "C:\\", "Z:/", "a:b", "file.txt:stream", "::$DATA", "\\\\?\\", "\\\\.\\",
        "\\\\srv\\share", "//srv/share", "\\??\\", "CON", "nul.txt", "COM1", "~", "$", "%TEMP%", "\0", "\u2215", "\uFF0F", "\u2024\u2024", "\uFF0E\uFF0E", "\u200B..", "..\u200B", " ",
    ];

    private static string Key(Gen g) => g.Bool(85) ? g.FromAlphabet(KeyPieces, 8) : g.NastyString();

    private static bool Inside(string path, string root, bool allowRoot)
    {
        // ExactPath.Full, not Path.GetFullPath: a last name "CON" or "a." is written through \\?\ as it is (ExactPath.Native).
        var full = ExactPath.Full(path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(root + Path.DirectorySeparatorChar, comparison) || (allowRoot && string.Equals(Path.TrimEndingDirectorySeparator(full), root, comparison));
    }

    [Fact]
    public void A_joined_entry_never_leaves_its_root()
    {
        FuzzKit.Check(
            seed: 801,
            iterations: 5000,
            g => (Key: Key(g), AllowRoot: g.Bool()),
            input =>
            {
                string? joined;
                try
                {
                    joined = ContainedPath.Join(Root, input.Key, input.AllowRoot);
                }
                catch (ArgumentException)
                {
                    return; // a NUL in the name: refused by the framework, nothing written
                }

                if (joined is null)
                {
                    return;
                }

                if (joined.Contains('\0', StringComparison.Ordinal))
                {
                    return; // GetFullPath refuses it, and so does every file API
                }

                Inside(joined, Root, input.AllowRoot).Should().BeTrue($"{FuzzKit.Literal(joined)} is inside {Root}");
                PathRules.IsInside(joined, Root).Should().BeTrue();
            },
            input => FuzzKit.ShrinkString(input.Key).Select(k => (k, input.AllowRoot)),
            input => $"{FuzzKit.Literal(input.Key)} allowRoot={input.AllowRoot}");
    }

    [Fact]
    public void Plain_names_are_always_joined()
    {
        FuzzKit.Check(
            seed: 802,
            iterations: 2000,
            g => g.Word(12) + g.Pick(["", ".txt", "_x", "-1"]),
            name =>
            {
                if (name.Length == 0)
                {
                    return;
                }

                ContainedPath.Join(Root, name).Should().Be(Path.Combine(Root, name));
                ContainedPath.Join(Root, "sub/" + name).Should().Be(Path.Combine(Root, "sub", name));
            });
    }

    /// <summary>
    /// Path.Combine starts over at the last rooted part, so on Windows "dir/C:x" joined to the target came out as
    /// "C:x" (relative to the current folder of drive C:), outside the target; only the first segment was checked.
    /// </summary>
    [Theory]
    [InlineData("dir/C:x")]
    [InlineData("dir\\C:")]
    [InlineData("a/b/Z:evil.exe")]
    [InlineData("ok/c:\\Windows\\x")]
    public void Regression_a_drive_in_a_later_segment_does_not_escape(string key)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "drive-relative names exist on Windows only");
        ContainedPath.Join(Root, key).Should().BeNull();
    }

    [Fact]
    public void Inside_is_reflexive_and_rejects_siblings_with_a_common_prefix()
    {
        FuzzKit.Check(
            seed: 803,
            iterations: 2000,
            g => g.Word(6) is { Length: > 0 } w ? w : "x",
            name =>
            {
                var folder = Path.Combine(Root, name);
                PathRules.IsInside(folder, folder).Should().BeTrue();
                PathRules.IsInside(folder + Path.DirectorySeparatorChar + "child", folder).Should().BeTrue();
                PathRules.IsInside(folder + "x", folder).Should().BeFalse();
                PathRules.IsInside(Root, folder).Should().BeFalse();
            });
    }

    [Fact]
    public void Exact_path_helpers_never_throw_and_prefixing_round_trips()
    {
        FuzzKit.Check(
            seed: 804,
            iterations: 3000,
            g => g.Bool(60) ? Path.Combine(Root, g.FromAlphabet(KeyPieces, 4)) : g.NastyString(),
            path =>
            {
                _ = ExactPath.NeedsPrefix(path);
                _ = ExactPath.NeedsPrefix(Path.GetFileName(path));
                _ = ExactPath.DisplayText(path);
                _ = ExactPath.UpperDrive(path);
                if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\?\", StringComparison.Ordinal))
                {
                    return; // the round trip is for paths the app shows (an already prefixed path is shown without it)
                }

                ExactPath.Display(ExactPath.Prefixed(path)).Should().Be(path);
                ExactPath.Display(ExactPath.Native(path)).Should().Be(path);
                try
                {
                    var full = ExactPath.Full(path);
                    if (!(full.Length >= 2 && full[1] == ':' && !char.IsAsciiLetter(full[0])))
                    {
                        // .NET reads "::" as a drive with an impossible letter and returns "::\"; navigating there just reports "not found".
                        Path.IsPathFullyQualified(full).Should().BeTrue();
                    }
                }
                catch (ArgumentException) when (path.Contains('\0', StringComparison.Ordinal))
                {
                    // the framework refuses a NUL in a path
                }
            },
            FuzzKit.ShrinkString);
    }
}
