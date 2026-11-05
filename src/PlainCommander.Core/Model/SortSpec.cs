namespace PlainCommander.Core.Model;

public enum SortColumn
{
    Name,
    Extension,
    Size,
    Modified,
    Attributes,
    Created,

    /// <summary>The panel's detail column (dimensions, photo date, duration, version).</summary>
    Detail,
}

public enum SortDirection
{
    Ascending,
    Descending,
}

public sealed record SortSpec(SortColumn Column = SortColumn.Name, SortDirection Direction = SortDirection.Ascending)
{
    public SortSpec Toggle(SortColumn column) =>
        column == Column
            ? this with { Direction = Direction == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending }
            : new SortSpec(column);
}
