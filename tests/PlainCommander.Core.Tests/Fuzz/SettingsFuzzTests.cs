using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using PlainCommander.Core.Settings;
using PlainCommander.Core.Tests.Operations;
using Xunit;

namespace PlainCommander.Core.Tests.Fuzz;

/// <summary>settings.json is a file the user (or a crash) can break: loading never throws and never hands out a null.</summary>
public sealed class SettingsFuzzTests : IDisposable
{
    private readonly Sandbox _s = new();
    private readonly SettingsStore _store;

    public SettingsFuzzTests()
    {
        _store = new SettingsStore(_s.P("settings.json"));
    }

    public void Dispose() => _s.Dispose();

    /// <summary>Every property the settings classes promise as non-null is non-null, all the way down (Window may be null).</summary>
    private static void NoNulls(object value, string path, List<string> problems)
    {
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || !property.CanWrite)
            {
                continue;
            }

            var current = property.GetValue(value);
            var here = path + "." + property.Name;
            if (current is null)
            {
                if (property.Name != nameof(AppSettings.Window))
                {
                    problems.Add(here);
                }

                continue;
            }

            if (current is string || current.GetType().IsValueType)
            {
                continue;
            }

            if (current is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Value is null)
                    {
                        problems.Add($"{here}[{entry.Key}]");
                    }
                }

                continue;
            }

            if (current is IList list)
            {
                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i] is null)
                    {
                        problems.Add($"{here}[{i}]");
                    }
                    else if (list[i] is not string)
                    {
                        NoNulls(list[i]!, $"{here}[{i}]", problems);
                    }
                }

                continue;
            }

            if (current.GetType().Namespace?.StartsWith("PlainCommander", StringComparison.Ordinal) == true)
            {
                NoNulls(current, here, problems);
            }
        }
    }

    private void LoadsCleanly(byte[] bytes)
    {
        File.WriteAllBytes(_store.FilePath, bytes);
        var settings = _store.Load();
        var problems = new List<string>();
        NoNulls(settings, "settings", problems);
        string.Join(", ", problems).Should().BeEmpty();

        // Whatever loaded can be saved and loads back the same.
        _store.Save(settings);
        var saved = File.ReadAllText(_store.FilePath);
        _store.Save(_store.Load());
        File.ReadAllText(_store.FilePath).Should().Be(saved);
    }

    private static string ValidJson()
    {
        var settings = new AppSettings
        {
            Left = new PanelSettings { Path = "C:\\x", Tabs = [new TabSettings { Path = "C:\\a" }, new TabSettings { Path = "D:\\b" }], ActiveTab = 1 },
            Window = new WindowSettings { X = 10, Y = 20, Width = 800, Height = 600 },
            Favorites = ["C:\\fav", "D:\\"],
            CommandHistory = ["dir", "cd .."],
            RemoteSites = [new Core.Remote.RemoteSite { Name = "s", Host = "h", Port = 22, User = "u" }],
            TrustedHosts = new Dictionary<string, string> { ["h:22"] = "SHA256:abc" },
        };
        return JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);
    }

    [Fact]
    public void Random_bytes_load_as_defaults()
    {
        FuzzKit.Check(seed: 301, iterations: 200, g => g.Bytes(300), LoadsCleanly, FuzzKit.ShrinkBytes, FuzzKit.Hex);
    }

    [Fact]
    public void A_mutated_settings_file_loads_cleanly()
    {
        var valid = Encoding.UTF8.GetBytes(ValidJson());
        FuzzKit.Check(seed: 302, iterations: 300, g => g.Mutate(valid), LoadsCleanly, show: b => FuzzKit.Literal(Encoding.UTF8.GetString(b)));
    }

    private static readonly Func<Gen, JsonNode?>[] OddValues =
    [
        _ => null,
        _ => JsonValue.Create(true),
        _ => JsonValue.Create(-1),
        _ => JsonValue.Create(0),
        _ => JsonValue.Create(int.MaxValue),
        _ => JsonValue.Create(long.MaxValue),
        _ => JsonValue.Create(-1e300),
        _ => JsonValue.Create(0.5),
        g => JsonValue.Create(g.NastyString(3)),
        _ => JsonValue.Create("Dark"),
        _ => JsonValue.Create("99"),
        _ => new JsonArray(),
        _ => new JsonArray(null, JsonValue.Create(1), JsonValue.Create("x")),
        _ => new JsonObject(),
        _ => new JsonObject { ["path"] = null, ["sort"] = null, ["tabs"] = new JsonArray(null, null) },
        _ => Deep(200),
        _ => Deep(40),
    ];

    private static JsonNode Deep(int depth)
    {
        JsonNode node = new JsonArray();
        for (var i = 0; i < depth; i++)
        {
            node = new JsonObject { ["x"] = node };
        }

        return node;
    }

    /// <summary>Replaces random values anywhere in the tree with values of the wrong type, huge, negative, null or deeply nested.</summary>
    private static string MutateValues(Gen g, string json)
    {
        var root = JsonNode.Parse(json)!;
        var slots = new List<(JsonNode Parent, object Key)>();
        void Collect(JsonNode node)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (key, child) in o.ToList())
                    {
                        slots.Add((o, key));
                        if (child is not null)
                        {
                            Collect(child);
                        }
                    }

                    break;
                case JsonArray a:
                    for (var i = 0; i < a.Count; i++)
                    {
                        slots.Add((a, i));
                        if (a[i] is { } child)
                        {
                            Collect(child);
                        }
                    }

                    break;
            }
        }

        Collect(root);
        var count = g.Int(1, 4);
        for (var n = 0; n < count && slots.Count > 0; n++)
        {
            var (parent, key) = g.Pick(slots);
            var value = g.Pick(OddValues)(g);
            try
            {
                if (key is string name)
                {
                    parent[name] = value;
                }
                else
                {
                    parent[(int)key] = value;
                }
            }
            catch (InvalidOperationException)
            {
                // the slot was detached by an earlier replacement
            }
        }

        if (g.Bool(20))
        {
            root.AsObject()[g.Pick(["unknownKey", "left", "theme", "trustedHosts", "remoteSites", "panelSplit"])] = g.Pick(OddValues)(g);
        }

        return root.ToJsonString();
    }

    [Fact]
    public void Values_of_the_wrong_kind_load_cleanly()
    {
        var valid = ValidJson();
        FuzzKit.Check(seed: 303, iterations: 400, g => MutateValues(g, valid), json => LoadsCleanly(Encoding.UTF8.GetBytes(json)), FuzzKit.ShrinkString);
    }

    [Fact]
    public void Save_then_load_gives_the_same_settings()
    {
        FuzzKit.Check(
            seed: 304,
            iterations: 100,
            g => new AppSettings
            {
                Left = new PanelSettings { Path = Gen.WellFormed(g.NastyString(3)), ActiveTab = g.Int(-5, 5), Tabs = [.. Enumerable.Range(0, g.Int(3)).Select(_ => new TabSettings { Path = Gen.WellFormed(g.NastyString(2)), BriefView = g.Bool() })] },
                Right = new PanelSettings { Path = Gen.WellFormed(g.NastyString(3)), Sort = new Core.Model.SortSpec((Core.Model.SortColumn)g.Int(7), (Core.Model.SortDirection)g.Int(2)) },
                ActivePanel = (PanelSide)g.Int(2),
                PanelSplit = g.Double(),
                CopyParallelism = g.Int(-100, 100),
                PanelFont = Gen.WellFormed(g.NastyString(2)),
                PanelFontSize = g.Double() * 100,
                Editor = Gen.WellFormed(g.NastyString(2)),
                Language = Gen.WellFormed(g.NastyString(1)),
                Theme = (AppTheme)g.Int(3),
                Window = g.Bool() ? null : new WindowSettings { X = g.Int(-5000, 5000), Width = g.Int(0, 5000), IsMaximized = g.Bool() },
                Favorites = [.. Enumerable.Range(0, g.Int(4)).Select(_ => Gen.WellFormed(g.NastyString(2)))],
                CommandHistory = [.. Enumerable.Range(0, g.Int(4)).Select(_ => Gen.WellFormed(g.NastyString(2)))],
                TrustedHosts = Enumerable.Range(0, g.Int(4)).Select(i => (Key: Gen.WellFormed(g.NastyString(2)) + i, Value: Gen.WellFormed(g.NastyString(2)))).ToDictionary(p => p.Key, p => p.Value),
            },
            settings =>
            {
                _store.Save(settings);
                var first = File.ReadAllText(_store.FilePath);
                var loaded = _store.Load();
                _store.Save(loaded);
                File.ReadAllText(_store.FilePath).Should().Be(first);
                loaded.Left.Path.Should().Be(settings.Left.Path);
                loaded.Favorites.Should().Equal(settings.Favorites);
                static string Pairs(Dictionary<string, string> d) => string.Join("\n", d.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value));
                Pairs(loaded.TrustedHosts).Should().Be(Pairs(settings.TrustedHosts));
                File.Exists(_store.FilePath + ".bad").Should().BeFalse("a file the app wrote itself is never corrupt");
            });
    }
}
