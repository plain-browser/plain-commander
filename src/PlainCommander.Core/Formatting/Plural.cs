namespace PlainCommander.Core.Formatting;

public static class Plural
{
    /// <summary>Polish plural: 1 plik, 2–4 pliki (but 12–14 plików), everything else plików.</summary>
    public static string Polish(long count, string one, string few, string many)
    {
        if (count == 1)
        {
            return one;
        }

        var lastDigit = count % 10;
        var lastTwo = count % 100;
        return lastDigit is >= 2 and <= 4 && lastTwo is < 12 or > 14 ? few : many;
    }
}
