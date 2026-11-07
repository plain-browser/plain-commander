using System.Text.RegularExpressions;
using System.Xml.Linq;
using AwesomeAssertions;
using PlainCommander.Core.Navigation;
using Xunit;

namespace PlainCommander.Presentation.Tests.Resources;

/// <summary>
/// Reads the App's .resw files and sources as text (the App itself is WinUI and cannot be loaded here) and
/// checks that the English and Polish strings agree with each other and with the code that uses them.
/// </summary>
public sealed partial class ResourceConsistencyTests
{
    private static readonly string Root = FindRoot();
    private static readonly string AppDir = Path.Combine(Root, "src", "PlainCommander.App");
    private static readonly IReadOnlyDictionary<string, string> English = Load("en-US");
    private static readonly IReadOnlyDictionary<string, string> Polish = Load("pl-PL");

    // Base names: "Menu_Files.Text" and "Menu_Files/Text" both belong to "Menu_Files".
    private static readonly HashSet<string> BaseNames = English.Keys.Select(BaseName).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Both_languages_have_the_same_keys()
    {
        English.Keys.Except(Polish.Keys).Lines().Should().BeEmpty("every English string needs a Polish one");
        Polish.Keys.Except(English.Keys).Lines().Should().BeEmpty("every Polish string needs an English one");
    }

    [Fact]
    public void No_value_is_empty()
    {
        English.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => "en " + p.Key)
            .Concat(Polish.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => "pl " + p.Key))
            .Lines().Should().BeEmpty();
    }

    [Fact]
    public void Placeholders_match_between_languages()
    {
        var mismatched = English.Keys.Where(Polish.ContainsKey)
            .Where(k => !Placeholders(English[k]).SetEquals(Placeholders(Polish[k])))
            .Select(k => $"{k}: en {{{string.Join(",", Placeholders(English[k]))}}} pl {{{string.Join(",", Placeholders(Polish[k]))}}}");
        mismatched.Lines().Should().BeEmpty();
    }

    [Fact]
    public void Placeholders_are_well_formed()
    {
        // string.Format throws FormatException on a stray brace, so a typo would crash the dialog.
        var bad = English.Concat(Polish).Where(p => Placeholders(p.Value).Count > 0 && !Formats(p.Value)).Select(p => p.Key);
        bad.Lines().Should().BeEmpty();
    }

    [Fact]
    public void Every_xuid_has_strings()
    {
        var missing = Files("*.xaml").SelectMany(f => XUid().Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Uid: m.Groups[1].Value)))
            .Where(u => !BaseNames.Contains(u.Uid))
            .Select(u => $"{u.File}: {u.Uid}");
        missing.Lines().Should().BeEmpty();
    }

    [Fact]
    public void Every_key_literal_in_code_exists()
    {
        // Any "Prefix_Name" literal whose prefix is a resource prefix is a key, whether it goes straight to
        // Loc.Get or through an array first (conflict buttons, permission grid).
        var prefixes = BaseNames.Where(k => k.Contains('_')).Select(k => k[..k.IndexOf('_')]).ToHashSet(StringComparer.Ordinal);
        var missing = Files("*.cs").SelectMany(f => KeyLiteral().Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Key: m.Groups[1].Value)))
            .Where(l => prefixes.Contains(l.Key[..l.Key.IndexOf('_')]) && !BaseNames.Contains(l.Key))
            .Select(l => $"{l.File}: {l.Key}")
            .Distinct();
        missing.Lines().Should().BeEmpty();
    }

    [Fact]
    public void Composed_keys_exist()
    {
        // Keys the code builds at run time, which the literal scan cannot see.
        var composed = Enum.GetNames<FilterDatePreset>().Select(p => "Flt_Date" + p)
            .Concat(Enumerable.Range(2, 7).Select(n => $"FKey_F{n}"));
        composed.Where(k => !BaseNames.Contains(k)).Lines().Should().BeEmpty();
    }

    [Fact]
    public void Format_calls_pass_enough_arguments()
    {
        var problems = new List<string>();
        foreach (var file in Files("*.cs"))
        {
            var text = File.ReadAllText(file);
            foreach (Match call in FormatCall().Matches(text))
            {
                var args = Arguments(text, call.Index + call.Length);
                foreach (Match key in QuotedKey().Matches(args[0]))
                {
                    var name = key.Groups[1].Value;
                    foreach (var (lang, strings) in new[] { ("en", English), ("pl", Polish) })
                    {
                        if (!strings.TryGetValue(name, out var value))
                        {
                            problems.Add($"{Path.GetFileName(file)}: {name} missing in {lang}");
                            continue;
                        }

                        var used = Placeholders(value);
                        if (used.Count == 0)
                        {
                            problems.Add($"{Path.GetFileName(file)}: {name} ({lang}) has no placeholder but goes through Loc.Format");
                        }
                        else if (used.Max() >= args.Count - 1)
                        {
                            problems.Add($"{Path.GetFileName(file)}: {name} ({lang}) uses {{{used.Max()}}} but gets {args.Count - 1} argument(s)");
                        }
                    }
                }
            }
        }

        problems.Lines().Should().BeEmpty();
    }

    [Fact]
    public void Every_string_is_used()
    {
        var code = string.Concat(Files("*.cs").Concat(Files("*.xaml")).Select(File.ReadAllText));
        var uids = Files("*.xaml").SelectMany(f => XUid().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        var composed = Enum.GetNames<FilterDatePreset>().Select(p => "Flt_Date" + p).ToHashSet(StringComparer.Ordinal);
        var unused = BaseNames
            .Where(k => !uids.Contains(k) && !composed.Contains(k) && !k.StartsWith("FKey_", StringComparison.Ordinal))
            .Where(k => !code.Contains('"' + k + '"', StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
        unused.Lines().Should().BeEmpty("a string nobody shows is either dead or a label that got lost");
    }

    private static string BaseName(string key)
    {
        var cut = key.IndexOfAny(['.', '/']);
        return cut < 0 ? key : key[..cut];
    }

    private static HashSet<int> Placeholders(string value) =>
        Placeholder().Matches(value.Replace("{{", string.Empty, StringComparison.Ordinal)).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();

    private static bool Formats(string value)
    {
        try
        {
            _ = string.Format(System.Globalization.CultureInfo.InvariantCulture, value, Enumerable.Range(0, 10).Cast<object>().ToArray());
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Top-level arguments of a call whose opening parenthesis ends just before <paramref name="start"/>.</summary>
    private static List<string> Arguments(string text, int start)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var depth = 0;
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                current.Append(c);
                if (c == '\\' && i + 1 < text.Length)
                {
                    current.Append(text[++i]);
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    current.Append(c);
                    break;
                case '(' or '[' or '{':
                    depth++;
                    current.Append(c);
                    break;
                case ')' or ']' or '}' when depth == 0:
                    args.Add(current.ToString());
                    return args;
                case ')' or ']' or '}':
                    depth--;
                    current.Append(c);
                    break;
                case ',' when depth == 0:
                    args.Add(current.ToString());
                    current.Clear();
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        args.Add(current.ToString());
        return args;
    }

    private static IEnumerable<string> Files(string pattern) =>
        Directory.EnumerateFiles(AppDir, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static Dictionary<string, string> Load(string language)
    {
        var doc = XDocument.Load(Path.Combine(AppDir, "Strings", language, "Resources.resw"));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var data in doc.Root!.Elements("data"))
        {
            var name = (string)data.Attribute("name")!;
            result.Should().NotContainKey(name, $"{language} defines {name} twice");
            result[name] = (string?)data.Element("value") ?? string.Empty;
        }

        return result;
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PlainCommander.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"x:Uid=""([^""]+)""")]
    private static partial Regex XUid();

    [GeneratedRegex(@"(?<![$@])""([A-Za-z]+_[A-Za-z0-9_]+)""")]
    private static partial Regex KeyLiteral();

    [GeneratedRegex("""\bLoc\.Format\(""")]
    private static partial Regex FormatCall();

    [GeneratedRegex(@"(?<![$@])""([A-Za-z0-9_]+)""")]
    private static partial Regex QuotedKey();

    [GeneratedRegex("""\{(\d+)[^}]*\}""")]
    private static partial Regex Placeholder();
}

internal static class ReportExtensions
{
    /// <summary>All items on separate lines, so a failure lists every offender and not just the first.</summary>
    public static string Lines(this IEnumerable<string> items) => string.Join(Environment.NewLine, items);
}
