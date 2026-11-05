using PlainCommander.Core.Model;

namespace PlainCommander.Core.Settings;

public enum PanelSide
{
    Left,
    Right,
}

public sealed class TabSettings
{
    public string Path { get; set; } = string.Empty;
    public SortSpec Sort { get; set; } = new();
    public bool BriefView { get; set; }
    public Details.DetailColumn Detail { get; set; }
}

public sealed class PanelSettings
{
    /// <summary>State of the active tab; kept so a settings file from before tabs still loads.</summary>
    public string Path { get; set; } = string.Empty;
    public SortSpec Sort { get; set; } = new();
    public bool BriefView { get; set; }
    public Details.DetailColumn Detail { get; set; }

    /// <summary>All tabs, written only when there is more than one.</summary>
    public List<TabSettings> Tabs { get; set; } = [];

    public int ActiveTab { get; set; }
}

/// <summary>Restored window bounds in physical pixels. Width 0 means "never saved".</summary>
public sealed class WindowSettings
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsMaximized { get; set; }
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public sealed class AppSettings
{
    public const string DefaultPanelFont = "Segoe UI Variable Text";
    public const double DefaultPanelFontSize = 13;

    public PanelSettings Left { get; set; } = new();
    public PanelSettings Right { get; set; } = new();
    public PanelSide ActivePanel { get; set; }
    public bool ShowHidden { get; set; }

    /// <summary>Shift+F2 options, remembered between runs.</summary>
    public bool CompareSubfolders { get; set; }

    public bool CompareByContent { get; set; }

    /// <summary>22 px rows instead of 26.</summary>
    public bool CompactRows { get; set; }

    /// <summary>The attributes column in the full view (off by default, the name needs the room).</summary>
    public bool ShowAttributes { get; set; }

    /// <summary>Folder sizes are measured in the background after a folder opens (local drives only).</summary>
    public bool FolderSizes { get; set; }

    /// <summary>Small files copied at once. 1 for spinning disks, where parallel copies make the heads seek.</summary>
    public int CopyParallelism { get; set; } = Operations.TransferOperation.DefaultParallelism;
    public WindowSettings? Window { get; set; }

    /// <summary>Left panel's share of the width between the panels, 0.2–0.8; 0.5 is the classic split.</summary>
    public double PanelSplit { get; set; } = 0.5;
    public string Editor { get; set; } = "notepad.exe";

    /// <summary>"" follows Windows; "pl-PL" or "en-US" force a language (takes effect after a restart).</summary>
    public string Language { get; set; } = string.Empty;
    public AppTheme Theme { get; set; }
    public string PanelFont { get; set; } = DefaultPanelFont;
    public double PanelFontSize { get; set; } = DefaultPanelFontSize;
    public List<string> Favorites { get; set; } = [];
    public List<string> CommandHistory { get; set; } = [];

    /// <summary>Saved FTP/SFTP connections (Ctrl+F). Passwords are not here but in the Windows Credential Manager.</summary>
    public List<Remote.RemoteSite> RemoteSites { get; set; } = [];

    /// <summary>SSH host keys and FTPS certificates the user trusted, by "host:port" → "SHA256:…".</summary>
    public Dictionary<string, string> TrustedHosts { get; set; } = [];
}
