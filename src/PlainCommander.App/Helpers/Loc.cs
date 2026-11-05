using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace PlainCommander.App.Helpers;

/// <summary>Strings for code (dialogs); XAML uses x:Uid. Both come from Strings/*/Resources.resw.</summary>
internal static class Loc
{
    // ResourceLoader.GetString throws for a missing key; the resource map just returns null.
    private static readonly ResourceMap Map = new ResourceManager().MainResourceMap.GetSubtree("Resources");

    public static string Get(string key) => TryGet(key) ?? key;

    /// <summary>The string, or null when the key is not in the resources (used for optional variants).</summary>
    public static string? TryGet(string key)
    {
        var value = Map.TryGetValue(key)?.ValueAsString;
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
