using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace PlainCommander.Core.Archives.SevenZip;

/// <summary>A file or folder from disk that goes into a 7z archive under <paramref name="Key"/> ("docs/a.txt").</summary>
internal sealed record SevenZipNewItem(string Key, bool IsDirectory, string? Source, long Size, DateTime ModifiedUtc, FileAttributes Attributes);

/// <summary>An item of the existing archive that stays, possibly under a new key.</summary>
internal sealed record SevenZipKeptItem(uint Index, string? NewKey);

/// <summary>Hooks a writing operation gives the writer: progress, pause/cancel and which file is being packed.</summary>
internal interface ISevenZipWriteObserver
{
    void StartItem(string source, long size);

    void AddBytes(int count);

    void FinishItem();

    /// <summary>Throws OperationCanceledException to stop; may block while paused.</summary>
    void Checkpoint();
}

/// <summary>
/// Writes 7z and encrypted zip archives with 7z.dll: a new one from disk files, or a changed copy of an existing one
/// where kept entries are copied without recompressing (unless their solid block changes) and new files are added.
/// The archive is written to the path given; the caller moves it into place.
/// </summary>
internal static unsafe partial class SevenZipWriter
{
    public static bool CanWrite => SevenZipLibrary.IsAvailable && SevenZipLibrary.Find("7z") is not null;

    /// <summary>Zip with a password: AES-256 only (the old ZipCrypto is not offered).</summary>
    public static bool CanWriteZip => SevenZipLibrary.IsAvailable && SevenZipLibrary.Find("zip") is not null;

    /// <param name="formatName">"7z" or "zip".</param>
    public static void Create(string formatName, string target, IReadOnlyList<SevenZipNewItem> items, string? password, ISevenZipWriteObserver observer)
    {
        var format = SevenZipLibrary.Find(formatName) ?? throw new NotSupportedException("7-Zip is not available.");
        var handler = SevenZipLibrary.Create<IOutArchive>(format);
        try
        {
            Write(handler, format.Name, target, null, [], items, password, observer);
        }
        finally
        {
            SevenZipLibrary.Release(handler);
        }
    }

    /// <summary>The archive opened for reading also writes its changed copy (same handler, IOutArchive).</summary>
    public static void Update(SevenZipArchive archive, string target, IReadOnlyList<SevenZipKeptItem> kept, IReadOnlyList<SevenZipNewItem> added, ISevenZipWriteObserver observer)
    {
        var handler = (IOutArchive)archive.Handler;
        // New files go in with the archive's own password, checked first: a guess would leave entries no one can open.
        Write(handler, archive.Format.Name, target, archive, kept, added, archive.RequirePassword(), observer);
    }

    private static void Write(IOutArchive handler, string formatName, string target, SevenZipArchive? source, IReadOnlyList<SevenZipKeptItem> kept, IReadOnlyList<SevenZipNewItem> added, string? password, ISevenZipWriteObserver observer)
    {
        if (password is { Length: > 0 } && handler is ISetProperties properties)
        {
            SetEncryption(properties, formatName);
        }

        using var output = new FileStream(target, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        var callback = new UpdateCallback(source, kept, added, password, observer);
        int hr;
        try
        {
            hr = handler.UpdateItems(new OutStreamAdapter(output), (uint)(kept.Count + added.Count), callback);
        }
        finally
        {
            callback.CloseOpenStream();
        }

        if (callback.Failure is { } failure)
        {
            throw failure;
        }

        SevenZipLibrary.Check(hr);
    }

    /// <summary>
    /// 7z: the file names are encrypted too ("he=on"); a changed encrypted archive keeps them hidden.
    /// Zip: AES-256 ("em=AES256") instead of 7-Zip's default for zip, the weak ZipCrypto, and one file at a time
    /// ("mt=off"): the zip writer re-reads a file it could not compress, so a file stays open until it is done.
    /// </summary>
    private static void SetEncryption(ISetProperties properties, string formatName)
    {
        var zip = string.Equals(formatName, "zip", StringComparison.OrdinalIgnoreCase);
        (string Name, PropVariant Value)[] settings = zip
            ? [("em", PropVariant.FromString("AES256")), ("mt", PropVariant.FromBool(false))]
            : [("he", PropVariant.FromBool(true))];
        var names = new IntPtr[settings.Length];
        try
        {
            var namePointers = stackalloc char*[settings.Length];
            var values = stackalloc PropVariant[settings.Length];
            for (var i = 0; i < settings.Length; i++)
            {
                names[i] = Marshal.StringToHGlobalUni(settings[i].Name);
                namePointers[i] = (char*)names[i];
                values[i] = settings[i].Value;
            }

            SevenZipLibrary.Check(properties.SetProperties(namePointers, values, (uint)settings.Length));
        }
        finally
        {
            foreach (var name in names)
            {
                Marshal.FreeHGlobal(name);
            }

            foreach (var (_, value) in settings)
            {
                value.Clear(); // SetProperties copies the values
            }
        }
    }

    [GeneratedComClass]
    private sealed partial class UpdateCallback(
        SevenZipArchive? source,
        IReadOnlyList<SevenZipKeptItem> kept,
        IReadOnlyList<SevenZipNewItem> added,
        string? password,
        ISevenZipWriteObserver observer) : IArchiveUpdateCallback, ICryptoGetTextPassword2, ICryptoGetTextPassword, IDisposable
    {
        private FileStream? _open;

        /// <summary>The real reason when 7-Zip only reports a failed callback (a file we could not read, a cancel).</summary>
        public Exception? Failure { get; private set; }

        public void Dispose() => CloseOpenStream();

        /// <summary>
        /// The file being packed is closed when 7-Zip reports it done (it may seek back in it until then), when the
        /// next one is asked for, or at the end. 7z reads one file after another; zip is written with "mt=off".
        /// </summary>
        public void CloseOpenStream()
        {
            _open?.Dispose();
            _open = null;
        }

        public int SetTotal(ulong total) => HResult.Ok;

        public int SetCompleted(ulong* completeValue) => Guard(observer.Checkpoint);

        public int GetUpdateItemInfo(uint index, int* newData, int* newProps, uint* indexInArchive)
        {
            var isKept = index < kept.Count;
            if (newData is not null)
            {
                *newData = isKept ? 0 : 1;
            }

            if (newProps is not null)
            {
                *newProps = isKept && kept[(int)index].NewKey is null ? 0 : 1;
            }

            if (indexInArchive is not null)
            {
                *indexInArchive = isKept ? kept[(int)index].Index : uint.MaxValue;
            }

            return HResult.Ok;
        }

        public int GetProperty(uint index, uint propId, PropVariant* value)
        {
            *value = default;
            if (index < kept.Count)
            {
                // A renamed entry: the new name, everything else as it was.
                var item = kept[(int)index];
                if (propId == Kpid.Path)
                {
                    *value = PropVariant.FromString(item.NewKey!.Replace('/', '\\'));
                    return HResult.Ok;
                }

                if (propId == Kpid.IsAnti)
                {
                    *value = PropVariant.FromBool(false);
                    return HResult.Ok;
                }

                return source!.Handler.GetProperty(item.Index, propId, value);
            }

            var add = added[(int)index - kept.Count];
            *value = propId switch
            {
                Kpid.Path => PropVariant.FromString(add.Key.Replace('/', '\\')),
                Kpid.IsDir => PropVariant.FromBool(add.IsDirectory),
                Kpid.Size => PropVariant.FromUInt64(add.IsDirectory ? 0 : (ulong)add.Size),
                Kpid.MTime => PropVariant.FromFileTime(add.ModifiedUtc),
                Kpid.Attrib => PropVariant.FromUInt32((uint)add.Attributes),
                Kpid.IsAnti => PropVariant.FromBool(false),
                _ => default,
            };
            return HResult.Ok;
        }

        public int GetStream(uint index, out ISequentialInStream? inStream)
        {
            inStream = null;
            if (index < kept.Count)
            {
                return HResult.Ok;
            }

            var add = added[(int)index - kept.Count];
            if (add.IsDirectory || add.Source is null)
            {
                return HResult.Ok;
            }

            CloseOpenStream();
            try
            {
                observer.Checkpoint();
                _open = new FileStream(add.Source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
                observer.StartItem(add.Source, add.Size);
                inStream = new InStreamAdapter(_open, observer.AddBytes, cancelled: () =>
                {
                    try
                    {
                        observer.Checkpoint();
                        return false;
                    }
                    catch (OperationCanceledException)
                    {
                        return true;
                    }
                });
                return HResult.Ok;
            }
            catch (OperationCanceledException ex)
            {
                Failure = ex;
                return HResult.Abort;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Failure = new IOException($"{add.Source}: {ex.Message}", ex);
                return HResult.Fail;
            }
        }

        /// <summary>Also called for kept entries and folders; only files we handed over count in the progress.</summary>
        public int SetOperationResult(int operationResult)
        {
            if (_open is not null)
            {
                CloseOpenStream();
                observer.FinishItem();
            }

            return HResult.Ok;
        }

        public int CryptoGetTextPassword2(int* passwordIsDefined, IntPtr* passwordOut)
        {
            var defined = password is { Length: > 0 };
            *passwordIsDefined = defined ? 1 : 0;
            *passwordOut = defined ? Marshal.StringToBSTR(password) : IntPtr.Zero;
            return HResult.Ok;
        }

        /// <summary>Repacking a solid block of an encrypted archive needs its password.</summary>
        public int CryptoGetTextPassword(IntPtr* passwordOut)
        {
            var answer = source is null ? password : ArchivePasswords.Get(source.File, wrong: false);
            if (answer is null)
            {
                *passwordOut = IntPtr.Zero;
                return HResult.Abort;
            }

            *passwordOut = Marshal.StringToBSTR(answer);
            return HResult.Ok;
        }

        private int Guard(Action action)
        {
            try
            {
                action();
                return HResult.Ok;
            }
            catch (OperationCanceledException ex)
            {
                Failure = ex;
                return HResult.Abort;
            }
        }
    }
}
