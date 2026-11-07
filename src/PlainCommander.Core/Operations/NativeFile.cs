using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PlainCommander.Core.Operations;

/// <summary>
/// The two things a path cannot tell: whether two paths name one file (links, junctions, 8.3 names and hard links
/// all give a second spelling), and which alternate data streams a Windows file carries.
/// </summary>
internal static class NativeFile
{
    /// <summary>
    /// The volume and file number, or null when the file system does not say (missing file, unsupported platform).
    /// Two paths with the same identity are one file, whatever their text.
    /// </summary>
    public static (ulong Volume, ulong File)? Identity(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return WindowsIdentity(path);
            }

            if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64)
            {
                return LinuxIdentity(path);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // no identity: callers fall back to comparing paths
        }

        return null;
    }

    /// <summary>
    /// The alternate data streams of a file (":Zone.Identifier", ":note"), each with its leading colon. Empty when there
    /// are none, on a file system without streams (FAT, network shares of other systems) and off Windows.
    /// </summary>
    public static IReadOnlyList<string> NamedStreams(string source)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var names = new List<string>();
        var find = FindFirstStreamW(source, 0, out var data, 0);
        if (find == InvalidHandle)
        {
            return names; // no streams at all, or a file system without them
        }

        try
        {
            do
            {
                // ":Zone.Identifier:$DATA"; the main stream is "::$DATA".
                var name = data.StreamName;
                if (name.EndsWith(":$DATA", StringComparison.OrdinalIgnoreCase) && name.Length > ":$DATA".Length + 1)
                {
                    names.Add(name[..^":$DATA".Length]);
                }
            }
            while (FindNextStreamW(find, out data));
        }
        finally
        {
            FindClose(find);
        }

        return names;
    }

    /// <summary>
    /// Whether the volume holding <paramref name="path"/> can keep alternate data streams. FAT, exFAT and many removable
    /// or network volumes cannot: opening "file:stream" there fails. True when unknown, so the old behaviour stays.
    /// </summary>
    public static bool SupportsStreams(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        try
        {
            var root = new char[1024];
            if (!GetVolumePathNameW(path, root, root.Length) || !GetVolumeInformationW(new string(root, 0, Array.IndexOf(root, '\0')), null, 0, out _, out _, out var flags, null, 0))
            {
                return true;
            }

            return (flags & FileNamedStreams) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return true;
        }
    }

    /// <summary>Copies every named data stream (":Zone.Identifier" and the like) from one file to another; Windows only.</summary>
    /// <remarks>Does nothing when the target volume cannot keep streams: the caller has already asked, or the streams are not worth a question.</remarks>
    public static void CopyStreams(string source, string destination)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var names = NamedStreams(source);
        if (names.Count == 0 || !SupportsStreams(destination))
        {
            return;
        }

        foreach (var name in names)
        {
            using var input = new FileStream(source + name, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var output = new FileStream(destination + name, FileMode.Create, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    private static (ulong, ulong)? WindowsIdentity(string path)
    {
        // No access rights asked for: works on files others hold open or locked; backup semantics opens folders too.
        using var handle = CreateFileW(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
        {
            return null;
        }

        return (info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }

    private static unsafe (ulong, ulong)? LinuxIdentity(string path)
    {
        // struct stat on x86-64 and arm64 Linux starts with st_dev and st_ino, both 64-bit; the rest is not needed.
        var buffer = stackalloc ulong[32];
        var name = System.Text.Encoding.UTF8.GetBytes(path + "\0");
        int result;
        try
        {
            result = stat(name, buffer);
        }
        catch (EntryPointNotFoundException)
        {
            // glibc before 2.33 exports only the versioned __xstat
            result = __xstat(RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 1 : 0, name, buffer);
        }

        return result == 0 ? (buffer[0], buffer[1]) : null;
    }

    private const uint BackupSemantics = 0x02000000;
    private const uint FileNamedStreams = 0x00040000;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindStreamData
    {
        public long StreamSize;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string StreamName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string path, [Out] char[] volumePath, int length);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string root, char[]? label, int labelLength, out uint serial, out uint maxComponent, out uint flags, char[]? fileSystem, int fileSystemLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr FindFirstStreamW(string name, int level, out FindStreamData data, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(IntPtr find, out FindStreamData data);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr find);

    [DllImport("libc", EntryPoint = "stat", ExactSpelling = true, SetLastError = true)]
    private static extern unsafe int stat(byte[] path, ulong* buffer);

    [DllImport("libc", EntryPoint = "__xstat", ExactSpelling = true, SetLastError = true)]
    private static extern unsafe int __xstat(int version, byte[] path, ulong* buffer);
}
