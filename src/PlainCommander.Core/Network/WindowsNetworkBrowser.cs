using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PlainCommander.Core.Network;

/// <summary>
/// The local network as Windows sees it. Computers come from the shell's Network folder (network discovery: WS-Discovery,
/// NetBIOS, the same list Explorer shows) and from the old browser service where it still answers; shares from
/// NetShareEnum; a logon goes through WNetAddConnection3 with Windows' own credential dialog (with "remember me").
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNetworkBrowser(Func<IntPtr> owner) : INetworkBrowser
{
    /// <summary>Discovery answers over several seconds (Explorer's Network folder too); after this we show what came.</summary>
    private static readonly TimeSpan DiscoveryLimit = TimeSpan.FromSeconds(15);

    public IReadOnlyList<string> Computers(CancellationToken cancellationToken)
    {
        // Both sources at once, each adding names as they arrive; whatever is in after the limit is the answer.
        var found = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var shell = new Thread(() => ShellComputers(found)) { IsBackground = true, Name = "Network discovery" };
        shell.SetApartmentState(ApartmentState.STA); // the shell namespace wants an STA
        shell.Start();
        var browser = new Thread(() => BrowserComputers(found)) { IsBackground = true, Name = "Network browser" };
        browser.Start();

        var deadline = DateTime.UtcNow + DiscoveryLimit;
        while (DateTime.UtcNow < deadline && (shell.IsAlive || browser.IsAlive))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.Sleep(100);
        }

        return found.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ShellComputers(System.Collections.Concurrent.ConcurrentDictionary<string, bool> result)
    {
        var folder = NetworkFolder;
        var itemIid = typeof(IShellItem).GUID;
        if (SHGetKnownFolderItem(ref folder, 0, IntPtr.Zero, ref itemIid, out var network) < 0 || network is null)
        {
            return;
        }

        try
        {
            var bhid = EnumItemsHandler;
            var enumIid = typeof(IEnumShellItems).GUID;
            if (network.BindToHandler(IntPtr.Zero, ref bhid, ref enumIid, out var enumerator) < 0 || enumerator is null)
            {
                return;
            }

            try
            {
                while (enumerator.Next(1, out var child, out var fetched) == 0 && fetched == 1 && child is not null)
                {
                    try
                    {
                        if (child.GetDisplayName(DesktopAbsoluteParsing, out var name) >= 0 && name != IntPtr.Zero)
                        {
                            var parsing = Marshal.PtrToStringUni(name);
                            Marshal.FreeCoTaskMem(name);
                            if (NetworkPath.UncServer(parsing) is { } server && parsing!.TrimEnd('\\').Length == server.Length + 2)
                            {
                                result[server] = true;
                            }
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(child);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch (COMException)
        {
            // discovery switched off or blocked: the other sources still count
        }
        finally
        {
            Marshal.ReleaseComObject(network);
        }
    }

    /// <summary>The computer browser of old Windows networks; usually silent today (and slow to say so), but free to ask.</summary>
    private static void BrowserComputers(System.Collections.Concurrent.ConcurrentDictionary<string, bool> result)
    {
        uint resume = 0;
        if (NetServerEnum(null, 100, out var buffer, -1, out var read, out _, 0xFFFFFFFF, null, ref resume) is not (0 or 234) || buffer == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var size = Marshal.SizeOf<ServerInfo100>();
            for (var i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<ServerInfo100>(buffer + (i * size));
                if (Marshal.PtrToStringUni(info.Name) is { Length: > 0 } name)
                {
                    result[name] = true;
                }
            }
        }
        finally
        {
            _ = NetApiBufferFree(buffer);
        }
    }

    public IReadOnlyList<NetworkShare> Shares(string server, bool includeHidden)
    {
        uint resume = 0;
        var status = NetShareEnum(@"\\" + server, 1, out var buffer, -1, out var read, out _, ref resume);
        if (status is 5 or 1326 or 1244 or 2221)
        {
            throw new UnauthorizedAccessException($@"\\{server}: access denied.");
        }

        if (status is not (0 or 234))
        {
            throw new IOException($@"\\{server}: {new System.ComponentModel.Win32Exception(status).Message}", status);
        }

        var shares = new List<NetworkShare>();
        try
        {
            var size = Marshal.SizeOf<ShareInfo1>();
            for (var i = 0; i < read; i++)
            {
                var info = Marshal.PtrToStructure<ShareInfo1>(buffer + (i * size));
                var name = Marshal.PtrToStringUni(info.Name) ?? string.Empty;
                var disk = (info.Type & 0xFF) == 0; // STYPE_DISKTREE; printers and IPC$ are not folders
                var special = (info.Type & 0x80000000) != 0 || name.EndsWith('$');
                if (name.Length > 0 && disk && (includeHidden || !special))
                {
                    shares.Add(new NetworkShare(name, Marshal.PtrToStringUni(info.Remark) ?? string.Empty));
                }
            }
        }
        finally
        {
            _ = NetApiBufferFree(buffer);
        }

        return shares.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool Logon(string remoteName)
    {
        var resource = new NetResource { RemoteName = remoteName };
        // CONNECT_INTERACTIVE | CONNECT_PROMPT: Windows asks for the user and password itself and can remember them
        return WNetAddConnection3(owner(), ref resource, null, null, 0x8 | 0x10) == 0;
    }

    private static readonly Guid NetworkFolder = new("D20BEEC4-5CA8-4905-AE3B-BF251EA09B53");
    private static readonly Guid EnumItemsHandler = new("94F60519-2850-4924-AA5A-D15E84868039");
    private const uint DesktopAbsoluteParsing = 0x80028000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ShareInfo1
    {
        public IntPtr Name;
        public uint Type;
        public IntPtr Remark;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServerInfo100
    {
        public uint PlatformId;
        public IntPtr Name;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public uint Scope;
        public uint Type;
        public uint DisplayType;
        public uint Usage;
        public string? LocalName;
        public string? RemoteName;
        public string? Comment;
        public string? Provider;
    }

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig]
        int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IEnumShellItems? result);

        [PreserveSig]
        int GetParent(out IntPtr parent);

        [PreserveSig]
        int GetDisplayName(uint form, out IntPtr name);

        [PreserveSig]
        int GetAttributes(uint mask, out uint attributes);

        [PreserveSig]
        int Compare(IntPtr other, uint hint, out int order);
    }

    [ComImport]
    [Guid("70629033-e363-4a28-a567-0db78006e6d7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumShellItems
    {
        [PreserveSig]
        int Next(uint count, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item, out uint fetched);
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderItem(ref Guid folder, uint flags, IntPtr token, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NetShareEnum(string server, int level, out IntPtr buffer, int preferredMaximum, out int read, out int total, ref uint resume);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NetServerEnum(string? server, int level, out IntPtr buffer, int preferredMaximum, out int read, out int total, uint type, string? domain, ref uint resume);

    [DllImport("netapi32.dll", ExactSpelling = true)]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode, EntryPoint = "WNetAddConnection3W", ExactSpelling = true)]
    private static extern int WNetAddConnection3(IntPtr owner, ref NetResource resource, string? password, string? user, uint flags);
}
