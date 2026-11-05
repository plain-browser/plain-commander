namespace PlainCommander.Core.Model;

/// <summary>One row in a file panel: a file, a directory, or the ".." parent link.</summary>
public sealed record FileEntry
{
    public required string Name { get; init; }
    public required string FullPath { get; init; }
    public required bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTimeOffset Modified { get; init; }

    /// <summary>When the file was created; known on disk only (servers and archives leave it null).</summary>
    public DateTimeOffset? Created { get; init; }
    public FileAttributes Attributes { get; init; }
    public bool IsParentLink { get; init; }

    /// <summary>Unix permission bits of a file on a server (SFTP, FTP); null on disk.</summary>
    public int? Mode { get; init; }

    /// <summary>
    /// Extension without the dot. Empty for directories, dot-less names and names whose only dot
    /// is the first character (".gitignore" is a name, not an extension).
    /// </summary>
    public string Extension
    {
        get
        {
            var dot = ExtensionDot;
            return dot < 0 ? string.Empty : Name[(dot + 1)..];
        }
    }

    /// <summary>Name without <see cref="Extension"/>, as shown in the name column of the full view.</summary>
    public string NameWithoutExtension
    {
        get
        {
            var dot = ExtensionDot;
            return dot < 0 ? Name : Name[..dot];
        }
    }

    public bool IsHidden => Attributes.HasFlag(FileAttributes.Hidden);
    public bool IsSystem => Attributes.HasFlag(FileAttributes.System);
    public bool IsReadOnly => Attributes.HasFlag(FileAttributes.ReadOnly);

    private int ExtensionDot
    {
        get
        {
            if (IsDirectory)
            {
                return -1;
            }

            var dot = Name.LastIndexOf('.');
            return dot > 0 && dot < Name.Length - 1 ? dot : -1;
        }
    }

    public static FileEntry ParentLink(string parentPath) => new()
    {
        Name = "..",
        FullPath = parentPath,
        IsDirectory = true,
        IsParentLink = true,
    };
}
