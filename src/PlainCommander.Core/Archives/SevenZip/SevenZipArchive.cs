using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace PlainCommander.Core.Archives.SevenZip;

/// <summary>One item of an archive opened by 7-Zip, by its index in the handler.</summary>
internal sealed record SevenZipItem(uint Index, string Key, bool IsDirectory, long Size, DateTime? Modified, bool Encrypted);

/// <summary>
/// An archive opened with 7z.dll: the handler, the file it reads and the item list. Formats are tried by
/// extension, then by signature; an encrypted header asks for the password (and asks again when it is wrong).
/// </summary>
internal sealed unsafe partial class SevenZipArchive : IDisposable
{
    /// <summary>Self-extracting archives keep the archive after the program; how far to look for it.</summary>
    private const ulong SfxSearchLimit = 1 << 23;

    private static readonly string[] SfxFormats = ["7z", "Rar5", "Rar", "zip", "Cab", "Nsis"];

    private readonly FileStream _stream;
    private readonly IReadOnlyList<FileStream> _volumes;
    private List<SevenZipItem>? _items;

    private SevenZipArchive(string file, SevenZipFormat format, IInArchive handler, FileStream stream, IReadOnlyList<FileStream> volumes)
    {
        File = file;
        Format = format;
        Handler = handler;
        _stream = stream;
        _volumes = volumes;
    }

    public string File { get; }

    public SevenZipFormat Format { get; }

    public IInArchive Handler { get; }

    public static SevenZipArchive Open(string file)
    {
        var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
        try
        {
            foreach (var (format, searchSfx) in Candidates(file, stream))
            {
                var wrong = false;
                while (true)
                {
                    var handler = SevenZipLibrary.Create<IInArchive>(format);
                    var callback = new OpenCallback(file, wrong);
                    stream.Position = 0;
                    var limit = SfxSearchLimit;
                    var hr = handler.Open(new InStreamAdapter(stream), searchSfx ? &limit : null, callback);
                    if (hr == HResult.Ok)
                    {
                        if (callback.UsedPassword is { } password)
                        {
                            ArchivePasswords.Remember(file, password);
                        }

                        return new SevenZipArchive(file, format, handler, stream, callback.Volumes);
                    }

                    SevenZipLibrary.Release(handler);
                    callback.CloseVolumes();
                    if (callback.PasswordCancelled)
                    {
                        throw new ArchivePasswordException(file, wrong);
                    }

                    if (callback.UsedPassword is not null)
                    {
                        // The header did not decrypt: the password was wrong. Ask again.
                        ArchivePasswords.Forget(file);
                        wrong = true;
                        continue;
                    }

                    if (hr == HResult.Abort)
                    {
                        throw new OperationCanceledException();
                    }

                    break; // not this format
                }
            }

            throw new NotAnArchiveException(file);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Formats to try: the extension's first, then those whose signature is at the start of the file.</summary>
    private static IEnumerable<(SevenZipFormat Format, bool SearchSfx)> Candidates(string file, FileStream stream)
    {
        var tried = new HashSet<int>();
        var name = Path.GetFileName(file);
        var extension = Path.GetExtension(file).TrimStart('.');
        var formats = SevenZipLibrary.Formats;

        // "a.part1.rar", "a.r00": the extension alone says rar.
        foreach (var format in formats.Where(f => f.Extensions.Any(e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase))).OrderBy(f => Preference(f.Name)))
        {
            tried.Add(format.Index);
            yield return (format, false);
        }

        var header = new byte[1 << 16];
        stream.Position = 0;
        var length = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        foreach (var format in formats.Where(f => !tried.Contains(f.Index) && f.Signatures.Any(s => Matches(header, length, f.SignatureOffset, s))).OrderBy(f => Preference(f.Name)))
        {
            tried.Add(format.Index);
            yield return (format, false);
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".sfx", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var sfx in SfxFormats.Select(SevenZipLibrary.Find).OfType<SevenZipFormat>())
            {
                yield return (sfx, true);
            }

            if (SevenZipLibrary.Find("PE") is { } pe && !tried.Contains(pe.Index))
            {
                yield return (pe, false);
            }
        }
    }

    /// <summary>Real archive formats before the generic containers that also match (a 7z SFX is a PE file too).</summary>
    private static int Preference(string name) => name switch
    {
        "7z" or "Rar5" or "Rar" or "zip" => 0,
        "PE" or "TE" or "ELF" or "MachO" or "MBR" or "GPT" or "Split" => 2,
        _ => 1,
    };

    private static bool Matches(byte[] header, int length, uint offset, byte[] signature) =>
        offset + signature.Length <= length && header.AsSpan((int)offset, signature.Length).SequenceEqual(signature);

    public IReadOnlyList<SevenZipItem> Items => _items ??= ReadItems();

    private List<SevenZipItem> ReadItems()
    {
        uint count;
        SevenZipLibrary.Check(Handler.GetNumberOfItems(&count));
        var items = new List<SevenZipItem>((int)count);
        for (uint i = 0; i < count; i++)
        {
            if (GetBool(i, Kpid.IsAnti) || GetBool(i, Kpid.IsAltStream))
            {
                continue;
            }

            var path = GetString(i, Kpid.Path);
            if (string.IsNullOrEmpty(path))
            {
                // Single-stream formats (.zst, .lz, .z) have no names: 7-Zip calls the content after the archive.
                path = count == 1 ? Path.GetFileNameWithoutExtension(File) : $"[{i}]";
            }

            var key = ArchivePath.NormalizeKey(path);
            if (key.Length == 0)
            {
                continue;
            }

            items.Add(new SevenZipItem(
                i,
                key,
                GetBool(i, Kpid.IsDir),
                (long)(GetULong(i, Kpid.Size) ?? 0),
                GetTime(i, Kpid.MTime),
                GetBool(i, Kpid.Encrypted)));
        }

        return items;
    }

    private PropVariant Get(uint index, uint id)
    {
        PropVariant value = default;
        if (Handler.GetProperty(index, id, &value) < 0)
        {
            value.Clear();
        }

        return value;
    }

    private bool GetBool(uint index, uint id)
    {
        var value = Get(index, id);
        var result = value.AsBool();
        value.Clear();
        return result;
    }

    private ulong? GetULong(uint index, uint id)
    {
        var value = Get(index, id);
        var result = value.AsULong();
        value.Clear();
        return result;
    }

    private string? GetString(uint index, uint id)
    {
        var value = Get(index, id);
        var result = value.AsString();
        value.Clear();
        return result;
    }

    private DateTime? GetTime(uint index, uint id)
    {
        var value = Get(index, id);
        var result = value.AsFileTime();
        var dos = value.Precision == PropVariant.DosPrecision;
        value.Clear();
        return dos && result is { } utc ? FromDosUtc(utc) : result;
    }

    /// <summary>
    /// Zip and rar4 keep local "wall clock" times. 7-Zip converts them with today's UTC offset, so a summer date read
    /// in winter came out an hour off; undo that and convert with the offset the date really had.
    /// </summary>
    internal static DateTime FromDosUtc(DateTime utcBySevenZip)
    {
        var wall = DateTime.SpecifyKind(utcBySevenZip + TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow), DateTimeKind.Unspecified);
        return TimeZoneInfo.Local.IsInvalidTime(wall) ? utcBySevenZip : TimeZoneInfo.ConvertTimeToUtc(wall, TimeZoneInfo.Local);
    }

    /// <summary>
    /// The password of an archive whose content is encrypted, checked by test-decompressing its smallest encrypted
    /// file; asks the user (again while wrong). Null when nothing is encrypted. Cancelling throws.
    /// </summary>
    public string? RequirePassword()
    {
        var probe = Items.Where(static i => i.Encrypted && !i.IsDirectory).OrderBy(static i => i.Size).FirstOrDefault();
        if (probe is null)
        {
            return ArchivePasswords.Remembered(File);
        }

        var wrong = false;
        while (true)
        {
            var password = ArchivePasswords.Get(File, wrong) ?? throw new ArchivePasswordException(File, wrong);
            if (Test(probe.Index, password))
            {
                ArchivePasswords.Remember(File, password);
                return password;
            }

            ArchivePasswords.Forget(File);
            wrong = true;
        }
    }

    private bool Test(uint index, string password)
    {
        var callback = new TestCallback(password);
        var indices = stackalloc uint[1];
        indices[0] = index;
        var hr = Handler.Extract(indices, 1, 1, callback);
        return hr == HResult.Ok && callback.Result == ExtractResult.Ok;
    }

    /// <summary>Test mode: 7-Zip decompresses and checks, nothing is written.</summary>
    [GeneratedComClass]
    private sealed partial class TestCallback(string password) : IArchiveExtractCallback, ICryptoGetTextPassword
    {
        public ExtractResult Result { get; private set; } = ExtractResult.DataError;

        public int SetTotal(ulong total) => HResult.Ok;

        public int SetCompleted(ulong* completeValue) => HResult.Ok;

        public int GetStream(uint index, out ISequentialOutStream? outStream, int askExtractMode)
        {
            outStream = null;
            return HResult.Ok;
        }

        public int PrepareOperation(int askExtractMode) => HResult.Ok;

        public int SetOperationResult(int opRes)
        {
            Result = (ExtractResult)opRes;
            return HResult.Ok;
        }

        public int CryptoGetTextPassword(IntPtr* value)
        {
            *value = Marshal.StringToBSTR(password);
            return HResult.Ok;
        }
    }

    public void Dispose()
    {
        Handler.Close();
        SevenZipLibrary.Release(Handler);
        _stream.Dispose();
        foreach (var volume in _volumes)
        {
            volume.Dispose();
        }
    }

    /// <summary>Opening: progress is not needed, passwords and the next volumes of a multi-part archive are.</summary>
    [GeneratedComClass]
    private sealed partial class OpenCallback(string file, bool wrong) : IArchiveOpenCallback, IArchiveOpenVolumeCallback, ICryptoGetTextPassword
    {
        private readonly List<FileStream> _volumes = [];

        /// <summary>The other parts of a multi-volume archive; 7-Zip reads them while the archive is open.</summary>
        public IReadOnlyList<FileStream> Volumes => _volumes;

        public void CloseVolumes()
        {
            foreach (var volume in _volumes)
            {
                volume.Dispose();
            }

            _volumes.Clear();
        }

        public string? UsedPassword { get; private set; }

        public bool PasswordCancelled { get; private set; }

        public int SetTotal(ulong* files, ulong* bytes) => HResult.Ok;

        public int SetCompleted(ulong* files, ulong* bytes) => HResult.Ok;

        public int GetProperty(uint propId, PropVariant* value)
        {
            *value = propId == Kpid.Name ? PropVariant.FromString(Path.GetFileName(file)) : default;
            return HResult.Ok;
        }

        public int GetStream(char* name, out IInStream? inStream)
        {
            inStream = null;
            var volume = Path.Combine(Path.GetDirectoryName(file) ?? string.Empty, new string(name));
            if (!System.IO.File.Exists(volume))
            {
                return HResult.False;
            }

            try
            {
                var stream = new FileStream(volume, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.RandomAccess);
                _volumes.Add(stream);
                inStream = new InStreamAdapter(stream);
                return HResult.Ok;
            }
            catch (IOException)
            {
                return HResult.False;
            }
        }

        public int CryptoGetTextPassword(IntPtr* password)
        {
            var answer = ArchivePasswords.Get(file, wrong);
            if (answer is null)
            {
                PasswordCancelled = true;
                *password = IntPtr.Zero;
                return HResult.Abort;
            }

            UsedPassword = answer;
            *password = Marshal.StringToBSTR(answer);
            return HResult.Ok;
        }
    }
}
