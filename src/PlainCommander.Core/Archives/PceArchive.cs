using PlainCommander.Core.Crypto;

namespace PlainCommander.Core.Archives;

/// <summary>
/// A .pce container seen as an archive: the panels list it from its index, read single entries and change it in
/// place through the same paths as zip or 7z. Files of the first .pce layout are not containers; they are browsed
/// through a temporary TAR (<see cref="PceBrowse"/>).
/// </summary>
public static class PceArchive
{
    public const string FormatName = "pce";

    /// <summary>True for an existing .pce file in the container layout.</summary>
    public static bool IsContainer(string file)
    {
        if (!PceFormat.IsPceName(file))
        {
            return false;
        }

        try
        {
            return PceContainer.LayoutVersion(file) == 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens the container with the password remembered for it, asking (again, after a wrong one) through
    /// <see cref="ArchivePasswords"/>. <see cref="ArchivePasswordException"/> when there is no password to try.
    /// </summary>
    public static PceContainer Open(string file, bool writable = false)
    {
        var wrong = false;
        while (true)
        {
            var password = ArchivePasswords.Get(file, wrong) ?? throw new ArchivePasswordException(file, wrong);
            try
            {
                return PceContainer.Open(file, password, writable);
            }
            catch (PceWrongPasswordException)
            {
                ArchivePasswords.Forget(file);
                wrong = true;
            }
        }
    }

    internal static IArchiveEntryReader Read(string file, IReadOnlySet<string>? wanted) => new EntryReader(Open(file), wanted);

    /// <summary>Entries in index order; each opens on its own, so skipping one reads nothing of it.</summary>
    private sealed class EntryReader(PceContainer container, IReadOnlySet<string>? wanted) : IArchiveEntryReader
    {
        private int _at = -1;

        private PceEntry Current => container.Entries[_at];

        public string Key => Current.Path;

        public bool IsDirectory => Current.IsDirectory;

        public long Size => Current.IsDirectory ? 0 : Current.Size;

        public DateTime? Modified => Current.ModifiedUtc;

        public bool MoveNext()
        {
            while (++_at < container.Entries.Count)
            {
                if (wanted is null || (!Current.IsDirectory && wanted.Contains(Current.Path)))
                {
                    return true;
                }
            }

            return false;
        }

        public Stream OpenEntryStream() => container.OpenRead(Current);

        public void Dispose() => container.Dispose();
    }
}
