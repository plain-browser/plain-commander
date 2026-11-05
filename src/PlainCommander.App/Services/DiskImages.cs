using System.ComponentModel;
using System.Runtime.InteropServices;
using PlainCommander.Presentation;

namespace PlainCommander.App.Services;

/// <summary>
/// Attaches and detaches disk images with Explorer's own verbs ("mount" on the file, "eject" on the drive), so an ISO
/// needs no administrator rights and a VHD gets the usual UAC question. The verbs come from shell context menus,
/// which want an STA thread. Explorer's "mount" also opens a window on the new drive; the panel shows it already,
/// so that window is closed.
/// </summary>
internal sealed class DiskImages(Func<IntPtr> owner) : IDiskImages
{
    private static readonly TimeSpan ExplorerWindowWait = TimeSpan.FromSeconds(8);

    public void Attach(string imageFile)
    {
        var before = ExplorerWindows();
        Invoke(imageFile, "mount");
        var deadline = DateTime.UtcNow + ExplorerWindowWait;
        while (DateTime.UtcNow < deadline)
        {
            var opened = ExplorerWindows().Except(before).ToList();
            if (opened.Count > 0)
            {
                foreach (var window in opened)
                {
                    PostMessage(window, CloseMessage, IntPtr.Zero, IntPtr.Zero);
                }

                return;
            }

            Thread.Sleep(100);
        }
    }

    /// <summary>Top-level Explorer folder windows.</summary>
    private static HashSet<IntPtr> ExplorerWindows()
    {
        var windows = new HashSet<IntPtr>();
        for (var w = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "CabinetWClass", null); w != IntPtr.Zero; w = FindWindowEx(IntPtr.Zero, w, "CabinetWClass", null))
        {
            windows.Add(w);
        }

        return windows;
    }

    private const uint CloseMessage = 0x0010; // WM_CLOSE

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowExW")]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? title);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    public void Detach(string driveRoot) => Invoke(driveRoot, "eject");

    private void Invoke(string target, string verb)
    {
        Exception? failure = null;
        var window = owner();
        var thread = new Thread(() =>
        {
            var info = new ShellExecuteInfo
            {
                Size = Marshal.SizeOf<ShellExecuteInfo>(),
                Mask = InvokeIdList | NoAsync,
                Window = window,
                Verb = verb,
                File = target,
                Show = 1,
            };
            if (!ShellExecuteEx(ref info))
            {
                failure = new Win32Exception(Marshal.GetLastWin32Error());
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw failure;
        }
    }

    private const uint InvokeIdList = 0x0000000C; // SEE_MASK_INVOKEIDLIST: verbs of the item's context menu
    private const uint NoAsync = 0x00000100; // SEE_MASK_NOASYNC: finish before returning

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Window;
        public string? Verb;
        public string? File;
        public string? Parameters;
        public string? Directory;
        public int Show;
        public IntPtr InstApp;
        public IntPtr IdList;
        public string? Class;
        public IntPtr ClassKey;
        public uint HotKey;
        public IntPtr IconOrMonitor;
        public IntPtr Process;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ShellExecuteExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);
}
