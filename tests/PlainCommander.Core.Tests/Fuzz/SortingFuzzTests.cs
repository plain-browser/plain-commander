using System.Globalization;
using AwesomeAssertions;
using PlainCommander.Core.Model;
using PlainCommander.Core.Sorting;
using Xunit;

namespace PlainCommander.Core.Tests.Fuzz;

/// <summary>The panel comparers must be total orders, or List.Sort throws "IComparer.Compare() method returns inconsistent results".</summary>
public sealed class SortingFuzzTests
{
    /// <summary>Pieces that make digit runs, case and culture interact: leading zeros, other scripts' digits, ignorable characters.</summary>
    private static readonly string[] Pieces =
    [
        "0", "00", "1", "01", "001", "9", "10", "2", "99999999999999999999", "18446744073709551616", "١", "٠١", "１", "０", "०", "²", "①",
        "a", "A", "b", "B", "z", "é", "é", "E", "ł", "Ł", "ß", "ss", "SS", "İ", "i", "I", "ı",
        ".", " ", "-", "_", "~", "​", "­", "́", "﻿", "\u0000", "😀", "\uD83D", "\uDE00", "中", "　",
    ];

    private static string Name(Gen g) => g.Bool(85) ? g.FromAlphabet(Pieces, 5) : g.NastyString(3);

    private static readonly CultureInfo[] Cultures = [CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("pl-PL"), CultureInfo.GetCultureInfo("tr-TR")];

    private static void TotalOrderLaws<T>(IComparer<T> comparer, T x, T y, T z)
    {
        comparer.Compare(x, x).Should().Be(0, "compare(x, x) is 0");
        Math.Sign(comparer.Compare(x, y)).Should().Be(-Math.Sign(comparer.Compare(y, x)), "antisymmetric");
        var xy = Math.Sign(comparer.Compare(x, y));
        var yz = Math.Sign(comparer.Compare(y, z));
        var xz = Math.Sign(comparer.Compare(x, z));
        if (xy <= 0 && yz <= 0)
        {
            xz.Should().BeLessThanOrEqualTo(0, "x <= y and y <= z, so x <= z");
            if (xy < 0 || yz < 0)
            {
                xz.Should().BeLessThan(0, "x < z when one step is strict");
            }
        }

        if (xy >= 0 && yz >= 0)
        {
            xz.Should().BeGreaterThanOrEqualTo(0, "x >= y and y >= z, so x >= z");
            if (xy > 0 || yz > 0)
            {
                xz.Should().BeGreaterThan(0, "x > z when one step is strict");
            }
        }
    }

    private static IEnumerable<(string, string, string)> ShrinkTriple((string X, string Y, string Z) t)
    {
        foreach (var x in FuzzKit.ShrinkString(t.X))
        {
            yield return (x, t.Y, t.Z);
        }

        foreach (var y in FuzzKit.ShrinkString(t.Y))
        {
            yield return (t.X, y, t.Z);
        }

        foreach (var z in FuzzKit.ShrinkString(t.Z))
        {
            yield return (t.X, t.Y, z);
        }
    }

    [Fact]
    public void Natural_order_is_a_total_order()
    {
        foreach (var culture in Cultures)
        {
            var comparer = new NaturalStringComparer(culture);
            FuzzKit.Check(
                seed: 401 + culture.LCID,
                iterations: 3000,
                g =>
                {
                    var x = Name(g);

                    // Near relatives of x make the interesting comparisons (ties, prefixes) common.
                    string Near() => g.Int(3) switch
                    {
                        0 => Name(g),
                        1 => x + g.Pick(Pieces),
                        _ => g.Pick(Pieces) + x,
                    };

                    return (X: x, Y: Near(), Z: Near());
                },
                t => TotalOrderLaws(comparer, t.X, t.Y, t.Z),
                ShrinkTriple,
                t => $"{culture.Name}: ({FuzzKit.Literal(t.X)}, {FuzzKit.Literal(t.Y)}, {FuzzKit.Literal(t.Z)})");
        }
    }

    [Fact]
    public void Natural_order_sorts_any_list_without_complaint()
    {
        foreach (var culture in Cultures)
        {
            var comparer = new NaturalStringComparer(culture);
            FuzzKit.Check(
                seed: 402 + culture.LCID,
                iterations: 200,
                g => Enumerable.Range(0, g.Int(2, 60)).Select(_ => Name(g)).ToList(),
                names =>
                {
                    var sorted = new List<string>(names);
                    sorted.Sort(comparer);
                    var wrong = Enumerable.Range(1, sorted.Count - 1).Where(i => comparer.Compare(sorted[i - 1], sorted[i]) > 0)
                        .Select(i => $"{FuzzKit.Literal(sorted[i - 1])} > {FuzzKit.Literal(sorted[i])}").ToList();
                    string.Join("; ", wrong).Should().BeEmpty();
                },
                FuzzKit.ShrinkList);
        }
    }

    private static readonly long[] Sizes = [0, 1, 1, 100, long.MaxValue, -1];

    private static readonly DateTimeOffset[] Times =
        [DateTimeOffset.MinValue, DateTimeOffset.MaxValue, new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), new(2024, 1, 1, 1, 0, 0, TimeSpan.FromHours(1))];

    private static readonly FileAttributes[] Attributes = [0, FileAttributes.ReadOnly, FileAttributes.Hidden, FileAttributes.Archive, FileAttributes.Normal];

    private static string Show(FileEntry e) =>
        $"{FuzzKit.Literal(e.Name)}{(e.IsDirectory ? " dir" : string.Empty)}{(e.IsParentLink ? " parent" : string.Empty)} {e.Size}";

    private static FileEntry Entry(Gen g)
    {
        var name = Name(g);

        // The ".." row is always a folder (a file marked as parent would break antisymmetry, but none exists).
        var parent = g.Bool(5);
        return new FileEntry
        {
            Name = name,
            FullPath = "/x/" + name,
            IsDirectory = parent || g.Bool(30),
            IsParentLink = parent,
            Size = g.Pick(Sizes),
            Modified = g.Pick(Times),
            Created = g.Bool() ? null : new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Attributes = g.Pick(Attributes),
        };
    }

    [Fact]
    public void File_order_is_a_total_order_for_every_column_and_direction()
    {
        foreach (var column in Enum.GetValues<SortColumn>())
        {
            foreach (var direction in Enum.GetValues<SortDirection>())
            {
                var comparer = new FileEntryComparer(new SortSpec(column, direction), new NaturalStringComparer(CultureInfo.InvariantCulture));
                FuzzKit.Check(
                    seed: 403 + ((int)column * 10) + (int)direction,
                    iterations: 300,
                    g => (X: Entry(g), Y: Entry(g), Z: Entry(g)),
                    t => TotalOrderLaws(comparer, t.X, t.Y, t.Z),
                    show: t => $"{column}/{direction}: {Show(t.X)} | {Show(t.Y)} | {Show(t.Z)}");

                FuzzKit.Check(
                    seed: 404 + ((int)column * 10) + (int)direction,
                    iterations: 30,
                    g => Enumerable.Range(0, g.Int(2, 80)).Select(_ => Entry(g)).ToList(),
                    entries =>
                    {
                        var sorted = new List<FileEntry>(entries);
                        sorted.Sort(comparer);
                        var wrong = Enumerable.Range(1, sorted.Count - 1).Where(i => comparer.Compare(sorted[i - 1], sorted[i]) > 0)
                            .Select(i => $"{Show(sorted[i - 1])} > {Show(sorted[i])}").ToList();
                        string.Join("; ", wrong).Should().BeEmpty();
                    },
                    FuzzKit.ShrinkList,
                    e => $"{column}/{direction}: [{string.Join(", ", e.Select(Show))}]");
            }
        }
    }
}
