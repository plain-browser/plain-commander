using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Serilog;

namespace PlainCommander.Core.Archives.SevenZip;

/// <summary>One archive format 7z.dll knows: its handler class and what it is recognized by.</summary>
internal sealed record SevenZipFormat(int Index, string Name, Guid ClassId, IReadOnlyList<string> Extensions, bool CanUpdate, IReadOnlyList<byte[]> Signatures, uint SignatureOffset);

/// <summary>
/// 7-Zip's 7z.dll, loaded once from the program folder. Absent (or not on Windows) means the archive code
/// falls back to the managed reader, which knows fewer formats and cannot write 7z.
/// </summary>
internal static unsafe class SevenZipLibrary
{
    public const string FileName = "7z.dll";

    private static readonly Lazy<State?> Loaded = new(Load);

    /// <summary>Creates managed views of 7-Zip objects; unique instances so each archive is released when we are done.</summary>
    internal static readonly StrategyBasedComWrappers Wrappers = new();

    private sealed class State
    {
        public required delegate* unmanaged[Stdcall]<Guid*, Guid*, void**, int> CreateObject;
        public required IReadOnlyList<SevenZipFormat> Formats;
    }

    public static bool IsAvailable => Loaded.Value is not null;

    public static IReadOnlyList<SevenZipFormat> Formats => Loaded.Value?.Formats ?? [];

    public static SevenZipFormat? Find(string name) =>
        Formats.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    private static State? Load()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var path = Path.Combine(AppContext.BaseDirectory, FileName);
        if (!File.Exists(path) || !NativeLibrary.TryLoad(path, out var handle))
        {
            Log.Warning("7-Zip library not found at {Path}; archives use the managed reader", path);
            return null;
        }

        try
        {
            var create = (delegate* unmanaged[Stdcall]<Guid*, Guid*, void**, int>)NativeLibrary.GetExport(handle, "CreateObject");
            var count = (delegate* unmanaged[Stdcall]<uint*, int>)NativeLibrary.GetExport(handle, "GetNumberOfFormats");
            var property = (delegate* unmanaged[Stdcall]<uint, uint, PropVariant*, int>)NativeLibrary.GetExport(handle, "GetHandlerProperty2");

            uint n;
            Check(count(&n));
            var formats = new List<SevenZipFormat>((int)n);
            for (uint i = 0; i < n; i++)
            {
                PropVariant Get(uint id)
                {
                    PropVariant value = default;
                    property(i, id, &value);
                    return value;
                }

                var name = Get(HandlerProp.Name);
                var classId = Get(HandlerProp.ClassId);
                var extensions = Get(HandlerProp.Extension);
                var update = Get(HandlerProp.Update);
                var signature = Get(HandlerProp.Signature);
                var multi = Get(HandlerProp.MultiSignature);
                var offset = Get(HandlerProp.SignatureOffset);
                try
                {
                    var id = classId.AsBytes();
                    if (id is not { Length: 16 })
                    {
                        continue;
                    }

                    var signatures = new List<byte[]>();
                    if (signature.AsBytes() is { Length: > 0 } single)
                    {
                        signatures.Add(single);
                    }

                    // Several signatures: each one prefixed by its length byte.
                    if (multi.AsBytes() is { Length: > 0 } packed)
                    {
                        for (var p = 0; p < packed.Length;)
                        {
                            var length = packed[p++];
                            if (length == 0 || p + length > packed.Length)
                            {
                                break;
                            }

                            signatures.Add(packed[p..(p + length)]);
                            p += length;
                        }
                    }

                    formats.Add(new SevenZipFormat(
                        (int)i,
                        name.AsString() ?? string.Empty,
                        new Guid(id),
                        (extensions.AsString() ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries),
                        update.AsBool(),
                        signatures,
                        (uint)(offset.AsULong() ?? 0)));
                }
                finally
                {
                    name.Clear();
                    classId.Clear();
                    extensions.Clear();
                    update.Clear();
                    signature.Clear();
                    multi.Clear();
                    offset.Clear();
                }
            }

            Log.Information("7-Zip library loaded with {Count} formats", formats.Count);
            return new State { CreateObject = create, Formats = formats };
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or COMException)
        {
            Log.Warning(ex, "7-Zip library at {Path} is not usable", path);
            return null;
        }
    }

    /// <summary>A new handler object of the format, viewed as <typeparamref name="T"/>; release it with <see cref="Release"/>.</summary>
    public static T Create<T>(SevenZipFormat format)
        where T : class
    {
        var state = Loaded.Value ?? throw new InvalidOperationException("7-Zip is not available.");
        var classId = format.ClassId;
        var iid = typeof(T).GUID;
        void* pointer;
        Check(state.CreateObject(&classId, &iid, &pointer));
        try
        {
            return (T)Wrappers.GetOrCreateObjectForComInstance((nint)pointer, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release((nint)pointer);
        }
    }

    /// <summary>Drops our reference to a handler right away instead of at the next garbage collection.</summary>
    public static void Release(object? handler)
    {
        if (handler is ComObject com)
        {
            com.FinalRelease();
        }
    }

    public static void Check(int hr)
    {
        if (hr < 0)
        {
            throw hr == HResult.Abort ? new OperationCanceledException() : new IOException($"7-Zip: {Marshal.GetExceptionForHR(hr)?.Message} (0x{hr:X8})", hr);
        }
    }
}
