using AwesomeAssertions;
using PlainCommander.Core.Rename;
using Xunit;

namespace PlainCommander.Core.Tests;

public sealed class TransliterationTests
{
    [Theory]
    [InlineData("Zażółć gęślą jaźń.txt", "Zazolc gesla jazn.txt")]
    [InlineData("ŁÓDŹ, Kraków", "LODZ, Krakow")]
    [InlineData("Straße Øresund Æble", "Strasse Oresund Aeble")]
    [InlineData("Москва.jpg", "Moskva.jpg")]
    [InlineData("Жук и щука", "Zhuk i shchuka")]
    [InlineData("ЖУК", "ZHUK")]
    [InlineData("Київ", "Kiyiv")]
    [InlineData("Αθήνα", "Athina")]
    [InlineData("Ψυχή", "Psychi")]
    [InlineData("Ёлка и йогурт", "Yolka i yogurt")]
    [InlineData("plain name 123", "plain name 123")]
    public void Spells_names_in_plain_latin(string name, string expected) =>
        Transliteration.ToLatin(name).Should().Be(expected);

    [Fact]
    public void Ctrl_M_applies_it_before_the_letter_case()
    {
        var source = new RenameSource("/x/Żółw.TXT", "Żółw.TXT", false, DateTimeOffset.Now);

        MultiRename.Compute(source, new RenameRules(Transliterate: true), 1).Should().Be("Zolw.TXT");
        MultiRename.Compute(source, new RenameRules(Transliterate: true, Case: CaseChange.Lower), 1).Should().Be("zolw.txt");
        MultiRename.Compute(source, new RenameRules(), 1).Should().Be("Żółw.TXT", "off by default");
    }
}
