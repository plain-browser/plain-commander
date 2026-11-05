using System.Runtime.InteropServices;
using PlainCommander.Core.Remote;
using Serilog;

namespace PlainCommander.App.Services;

/// <summary>
/// Remembered FTP/SFTP passwords in the Windows Credential Manager (generic credentials, "PlainCommander:sftp://…"),
/// encrypted by Windows for this user. Visible and removable in Control Panel → Credential Manager.
/// </summary>
internal sealed partial class CredentialStore : ISecretStore
{
    private const uint GenericType = 1;
    private const uint PersistLocalMachine = 2;

    public string? Read(string key)
    {
        if (!CredReadW(key, GenericType, 0, out var pointer))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            return credential.BlobSize == 0 ? string.Empty : Marshal.PtrToStringUni(credential.Blob, (int)credential.BlobSize / 2);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public void Write(string key, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var target = Marshal.StringToCoTaskMemUni(key);
        var user = Marshal.StringToCoTaskMemUni("PlainCommander");
        var blob = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            var credential = new Credential
            {
                Type = GenericType,
                TargetName = target,
                BlobSize = (uint)(secret.Length * 2),
                Blob = blob,
                Persist = PersistLocalMachine,
                UserName = user,
            };
            if (!CredWriteW(ref credential, 0))
            {
                Log.Warning("Cannot store the password for {Key}: error {Error}", key, Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
            Marshal.FreeCoTaskMem(target);
            Marshal.FreeCoTaskMem(user);
        }
    }

    public void Delete(string key) => CredDeleteW(key, GenericType, 0);

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(string target, uint type, uint flags, out IntPtr credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWriteW(ref Credential credential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);

    /// <summary>CREDENTIALW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }
}
