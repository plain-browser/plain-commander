using System.IO.Enumeration;

namespace PlainCommander.Core.Navigation;

/// <summary>Masks for Num+/Num− selection: "*.txt;*.md", "*.*" means everything.</summary>
public static class FileMask
{
    public static Func<string, bool> Parse(string masks)
    {
        ArgumentNullException.ThrowIfNull(masks);
        var parts = masks.Split([';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(m => m is "*.*" ? "*" : m)
            .ToArray();
        if (parts.Length == 0)
        {
            return static _ => false;
        }

        return name => parts.Any(p => FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true));
    }
}
