using System.Runtime.Versioning;
using Microsoft.Win32;

namespace PlainCommander.Core.FileSystem;

/// <summary>A synced OneDrive folder: "OneDrive" (personal) or "OneDrive - Contoso" (work or school).</summary>
public sealed record CloudFolder(string Name, string Path)
{
    /// <summary>What fits on a button: "OneDrive", or the organisation of "OneDrive - Contoso".</summary>
    public string ShortName
    {
        get
        {
            var dash = Name.IndexOf(" - ", StringComparison.Ordinal);
            return dash >= 0 && Name.Length - dash - 3 >= 2 ? Name[(dash + 3)..] : Name;
        }
    }
}

/// <summary>Finds the OneDrive folders of the signed-in user (one personal account and any number of work accounts).</summary>
public static class CloudFolders
{
    public static IReadOnlyList<CloudFolder> Find()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            return FindOnWindows();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<CloudFolder> FindOnWindows()
    {
        var paths = new List<string>();
        using (var accounts = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts"))
        {
            // "Personal" first, then "Business1", "Business2", …: the order of the buttons
            foreach (var name in (accounts?.GetSubKeyNames() ?? []).OrderBy(static n => n == "Personal" ? 0 : 1).ThenBy(static n => n, StringComparer.OrdinalIgnoreCase))
            {
                using var account = accounts!.OpenSubKey(name);
                if (account?.GetValue("UserFolder") is string folder)
                {
                    paths.Add(folder);
                }
            }
        }

        foreach (var variable in new[] { "OneDriveConsumer", "OneDriveCommercial", "OneDrive" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
            {
                paths.Add(value);
            }
        }

        return Merge(paths, Directory.Exists);
    }

    /// <summary>The same folder named twice (registry and environment) counts once; folders that are gone are left out.</summary>
    internal static IReadOnlyList<CloudFolder> Merge(IEnumerable<string> paths, Func<string, bool> exists)
    {
        var result = new List<CloudFolder>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            var path = Path.TrimEndingDirectorySeparator(raw.Trim());
            if (path.Length == 0 || !seen.Add(path) || !exists(path))
            {
                continue;
            }

            var name = Path.GetFileName(path);
            result.Add(new CloudFolder(name.Length == 0 ? "OneDrive" : name, path));
        }

        return result;
    }
}
