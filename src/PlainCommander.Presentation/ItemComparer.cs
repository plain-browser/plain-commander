using PlainCommander.Core.Details;
using PlainCommander.Core.Model;
using PlainCommander.Core.Sorting;

namespace PlainCommander.Presentation;

/// <summary>
/// Orders panel rows. Like <see cref="FileEntryComparer"/>, plus what only rows know: measured folder sizes (sorting
/// by size then orders folders by size too) and the detail column's values (files without one go last).
/// </summary>
internal sealed class ItemComparer(SortSpec spec, DetailColumn detail) : IComparer<FileItem>
{
    private readonly FileEntryComparer _entries = new(spec);
    private readonly FileEntryComparer _names = new(new SortSpec());

    public int Compare(FileItem? x, FileItem? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null || y is null)
        {
            return x is null ? -1 : 1;
        }

        var group = Group(x).CompareTo(Group(y));
        if (group != 0 || x.Entry.IsParentLink)
        {
            return group;
        }

        if (spec.Column == SortColumn.Size && x.IsDirectory && x.DirectorySize is { } a && y.DirectorySize is { } b && a != b)
        {
            return spec.Direction == SortDirection.Descending ? b.CompareTo(a) : a.CompareTo(b);
        }

        if (spec.Column == SortColumn.Detail && !x.IsDirectory && detail != DetailColumn.None)
        {
            var hasX = DetailFormatter.HasValue(x.Details, detail);
            var hasY = DetailFormatter.HasValue(y.Details, detail);
            if (hasX != hasY)
            {
                return hasX ? -1 : 1; // files without a value stay last in both directions
            }

            var c = hasX ? DetailFormatter.Compare(x.Details, y.Details, detail) : 0;
            return c != 0 ? (spec.Direction == SortDirection.Descending ? -c : c) : _names.Compare(x.Entry, y.Entry);
        }

        return _entries.Compare(x.Entry, y.Entry);
    }

    private static int Group(FileItem i) => i.Entry.IsParentLink ? 0 : i.IsDirectory ? 1 : 2;
}
