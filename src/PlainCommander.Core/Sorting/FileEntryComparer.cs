using PlainCommander.Core.Model;

namespace PlainCommander.Core.Sorting;

/// <summary>".." first, then directories, then files; within each group by the chosen column.</summary>
public sealed class FileEntryComparer : IComparer<FileEntry>
{
    private readonly SortSpec _spec;
    private readonly IComparer<string> _names;

    public FileEntryComparer(SortSpec spec, IComparer<string>? names = null)
    {
        _spec = spec;
        _names = names ?? NaturalStringComparer.Instance;
    }

    public int Compare(FileEntry? x, FileEntry? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var group = Group(x).CompareTo(Group(y));
        if (group != 0)
        {
            return group;
        }

        var c = _spec.Column switch
        {
            SortColumn.Extension => _names.Compare(x.Extension, y.Extension),
            SortColumn.Size => x.Size.CompareTo(y.Size),
            SortColumn.Modified => x.Modified.CompareTo(y.Modified),
            SortColumn.Created => (x.Created ?? x.Modified).CompareTo(y.Created ?? y.Modified),
            SortColumn.Attributes => x.Attributes.CompareTo(y.Attributes),
            _ => _names.Compare(x.Name, y.Name),
        };

        // Directories never reverse: they stay in ascending order whatever the direction.
        if (_spec.Direction == SortDirection.Descending && !x.IsDirectory)
        {
            c = -c;
        }

        // Ties on another column fall back to the name, always ascending: "biggest first, then A–Z".
        return c != 0 || _spec.Column == SortColumn.Name ? c : _names.Compare(x.Name, y.Name);
    }

    private static int Group(FileEntry e) => e.IsParentLink ? 0 : e.IsDirectory ? 1 : 2;
}
