using System.Runtime.Versioning;
using Microsoft.Win32;

namespace PlainCommander.Core.Network;

/// <summary>
/// The Linux distributions of WSL (Ubuntu, Debian, docker-desktop, …). Their files are reachable as the UNC shares
/// "\\wsl.localhost\Ubuntu" (older Windows: "\\wsl$\Ubuntu"), but NetServerEnum and NetShareEnum know nothing of them:
/// the names come from the registry, where WSL keeps its distribution list.
/// </summary>
public static class WslDistros
{
    public const string Server = "wsl.localhost";

    /// <summary>True for "wsl.localhost" and the older "wsl$".</summary>
    public static bool IsServer(string? server) =>
        string.Equals(server, Server, StringComparison.OrdinalIgnoreCase) || string.Equals(server, "wsl$", StringComparison.OrdinalIgnoreCase);

    /// <summary>"\\wsl.localhost\Ubuntu": the root of a distribution, which is also its drive-menu root.</summary>
    public static string Root(string name) => $@"\\{Server}\{name}";

    /// <summary>The distribution of "\\wsl.localhost\Ubuntu\home" ("Ubuntu"), or null.</summary>
    public static string? DistroOf(string? path)
    {
        if (!IsServer(NetworkPath.UncServer(path)))
        {
            return null;
        }

        var rest = path![(2 + NetworkPath.UncServer(path)!.Length)..].TrimStart('\\');
        var name = rest.Split('\\', 2)[0];
        return name.Length == 0 ? null : name;
    }

    /// <summary>Installed distributions, the default one first; empty off Windows or without WSL.</summary>
    public static IReadOnlyList<string> Find()
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
    private static List<string> FindOnWindows()
    {
        using var lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
        if (lxss is null)
        {
            return [];
        }

        var defaultId = lxss.GetValue("DefaultDistribution") as string;
        var names = new List<(bool Default, string Name)>();
        foreach (var id in lxss.GetSubKeyNames())
        {
            using var distro = lxss.OpenSubKey(id);
            // State 1 = installed; 3 = being installed, 4 = being removed
            if (distro?.GetValue("DistributionName") is string name && name.Length > 0 && (distro.GetValue("State") as int? ?? 1) == 1)
            {
                names.Add((string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase), name));
            }
        }

        return names.OrderByDescending(static n => n.Default).ThenBy(static n => n.Name, StringComparer.OrdinalIgnoreCase).Select(static n => n.Name).ToList();
    }
}
