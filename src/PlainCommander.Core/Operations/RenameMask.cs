namespace PlainCommander.Core.Operations;

/// <summary>
/// The name mask of F5/F6 ("*.bak", "new_*.*", "??-*.txt"), applied to each source name the way the command
/// prompt's "ren" splits it: the mask and the name are cut at the last dot, and each half is built on its own.
/// "*" takes the rest of that half of the old name, "?" takes its next character, anything else is added as typed
/// (so "new_*.*" puts a prefix before the name). A mask without a dot gives a name without an extension.
/// </summary>
public static class RenameMask
{
    public static bool IsMask(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Contains('*', StringComparison.Ordinal) || name.Contains('?', StringComparison.Ordinal);
    }

    public static string Apply(string mask, string name)
    {
        ArgumentNullException.ThrowIfNull(mask);
        ArgumentNullException.ThrowIfNull(name);
        var (maskStem, maskExtension) = Split(mask);
        var (stem, extension) = Split(name);
        var result = Part(maskStem, stem);
        if (maskExtension is not null)
        {
            var newExtension = Part(maskExtension, extension ?? string.Empty);
            if (newExtension.Length > 0)
            {
                result += "." + newExtension;
            }
        }

        return result;
    }

    /// <summary>A leading dot belongs to the name (".gitignore" has no extension).</summary>
    private static (string Stem, string? Extension) Split(string text)
    {
        var dot = text.LastIndexOf('.');
        return dot <= 0 ? (text, null) : (text[..dot], text[(dot + 1)..]);
    }

    private static string Part(string mask, string source)
    {
        var builder = new System.Text.StringBuilder(mask.Length + source.Length);
        var position = 0;
        foreach (var c in mask)
        {
            if (c == '*')
            {
                if (position < source.Length)
                {
                    builder.Append(source, position, source.Length - position);
                    position = source.Length;
                }
            }
            else if (c == '?')
            {
                if (position < source.Length)
                {
                    builder.Append(source[position]);
                }

                position++;
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
