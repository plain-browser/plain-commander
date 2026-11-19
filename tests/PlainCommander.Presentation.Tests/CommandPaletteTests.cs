using AwesomeAssertions;
using Xunit;

namespace PlainCommander.Presentation.Tests;

public sealed class CommandPaletteTests
{
    private static readonly PaletteCommand[] Commands =
    [
        new("Kopiuj", "Pliki", "F5"),
        new("Zmień nazwę", "Pliki", "Shift+F6"),
        new("Zmień nazwy wielu plików…", "Pliki", "Ctrl+M"),
        new("Nowa karta", "Polecenia", "Ctrl+T"),
        new("Zamknij kartę", "Polecenia", "Ctrl+W"),
        new("Sortuj po nazwie", "Widok", "Ctrl+F3"),
        new("Ukryte pliki", "Widok", "Ctrl+H"),
    ];

    private static string[] Titles(string query) => CommandPalette.Filter(Commands, query).Select(i => Commands[i].Title).ToArray();

    [Fact]
    public void An_empty_query_lists_everything_in_menu_order()
    {
        CommandPalette.Filter(Commands, "  ").Should().Equal(0, 1, 2, 3, 4, 5, 6);
        CommandPalette.Filter(Commands, null).Should().HaveCount(Commands.Length);
    }

    [Fact]
    public void Letters_match_without_accents_and_case()
    {
        Titles("zmien").Should().Equal("Zmień nazwę", "Zmień nazwy wielu plików…");
        Titles("KARTE").Should().Equal("Zamknij kartę");
    }

    [Fact]
    public void Every_word_has_to_match_in_any_order()
    {
        Titles("nazw sort").Should().Equal("Sortuj po nazwie");
        Titles("karta xyz").Should().BeEmpty();
    }

    [Fact]
    public void A_title_that_starts_with_the_query_comes_before_a_match_inside_and_before_the_menu_name()
    {
        // "pli": no title starts with it; "Ukryte pliki" and the multi-rename have a word starting with it, the rest only sit in the Pliki menu
        Titles("pli").Should().Equal("Zmień nazwy wielu plików…", "Ukryte pliki", "Kopiuj", "Zmień nazwę");
        Titles("na").Should().StartWith("Zmień nazwę").And.Contain("Sortuj po nazwie");
        Titles("ka").Should().Equal("Nowa karta", "Zamknij kartę");
        Titles("kart zam").Should().Equal("Zamknij kartę");
    }
}
