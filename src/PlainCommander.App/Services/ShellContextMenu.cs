using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PlainCommander.App.Services;

/// <summary>
/// Explorer's context menu for a set of files, the way Explorer builds it: a shell item array bound to
/// IContextMenu, a Win32 popup menu, then InvokeCommand with the chosen id. While the menu is up the owner
/// window is subclassed so "Send to" and "Open with" can fill their submenus (IContextMenu2/3).
/// Runs on the UI thread; TrackPopupMenuEx pumps messages itself.
/// </summary>
internal static unsafe class ShellContextMenu
{
    private const uint FirstId = 1;
    private const uint LastId = 0x7FFF;
    private const uint CmicMaskUnicode = 0x00004000;  // CMIC_MASK_UNICODE: not in the CsWin32 metadata
    private const uint CmicMaskPtInvoke = 0x20000000; // CMIC_MASK_PTINVOKE
    private const nuint SubclassId = 0x5043; // "PC"

    private static IContextMenu2? _menu2;
    private static IContextMenu3? _menu3;
    private static readonly SUBCLASSPROC Hook = OnMessage; // kept alive for the native callback

    /// <summary>Screen position of the mouse pointer.</summary>
    public static (int X, int Y) CursorPosition()
    {
        PInvoke.GetCursorPos(out var point);
        return (point.X, point.Y);
    }

    /// <summary>Client DIPs of <paramref name="hwnd"/> to screen pixels.</summary>
    public static (int X, int Y) ToScreen(nint hwnd, double x, double y, double scale)
    {
        var point = new System.Drawing.Point((int)Math.Round(x * scale), (int)Math.Round(y * scale));
        PInvoke.ClientToScreen(new HWND(hwnd), ref point);
        return (point.X, point.Y);
    }

    /// <summary>Shows the menu for <paramref name="paths"/> at a screen point and runs what the user picks.</summary>
    public static void Show(nint ownerHwnd, IReadOnlyList<string> paths, int x, int y)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var hwnd = new HWND(ownerHwnd);
        var pidls = new ITEMIDLIST*[paths.Count];
        IShellItemArray? items = null;
        IContextMenu? menu = null;
        var popup = HMENU.Null;
        try
        {
            for (var i = 0; i < paths.Count; i++)
            {
                PInvoke.SHParseDisplayName(paths[i], null, out pidls[i], 0, out _).ThrowOnFailure();
            }

            fixed (ITEMIDLIST** list = pidls)
            {
                PInvoke.SHCreateShellItemArrayFromIDLists((uint)paths.Count, list, out items).ThrowOnFailure();
            }

            var bhid = PInvoke.BHID_SFUIObject;
            var iid = typeof(IContextMenu).GUID;
            void* raw;
            items.BindToHandler(null, &bhid, &iid, out raw);
            var unknown = Marshal.GetObjectForIUnknown((nint)raw);
            Marshal.Release((nint)raw); // GetObjectForIUnknown took its own reference
            menu = (IContextMenu)unknown;
            _menu2 = menu as IContextMenu2;
            _menu3 = menu as IContextMenu3;

            popup = PInvoke.CreatePopupMenu();
            menu.QueryContextMenu(popup, 0, FirstId, LastId, PInvoke.CMF_NORMAL | PInvoke.CMF_EXPLORE).ThrowOnFailure();

            PInvoke.SetWindowSubclass(hwnd, Hook, SubclassId, 0);
            int chosen;
            try
            {
                chosen = PInvoke.TrackPopupMenuEx(popup, (uint)(TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON), x, y, hwnd, null).Value;
            }
            finally
            {
                PInvoke.RemoveWindowSubclass(hwnd, Hook, SubclassId);
            }

            if (chosen >= FirstId)
            {
                var info = new CMINVOKECOMMANDINFOEX
                {
                    cbSize = (uint)sizeof(CMINVOKECOMMANDINFOEX),
                    fMask = CmicMaskUnicode | CmicMaskPtInvoke,
                    hwnd = hwnd,
                    lpVerb = new PCSTR((byte*)(nint)(chosen - FirstId)),
                    lpVerbW = new PCWSTR((char*)(nint)(chosen - FirstId)),
                    nShow = (int)SHOW_WINDOW_CMD.SW_SHOWNORMAL,
                    ptInvoke = new System.Drawing.Point(x, y),
                };
                menu.InvokeCommand((CMINVOKECOMMANDINFO*)&info);
            }
        }
        finally
        {
            _menu2 = null;
            _menu3 = null;
            if (popup != HMENU.Null)
            {
                PInvoke.DestroyMenu(popup);
            }

            if (menu is not null)
            {
                Marshal.ReleaseComObject(menu);
            }

            if (items is not null)
            {
                Marshal.ReleaseComObject(items);
            }

            foreach (var pidl in pidls)
            {
                if (pidl is not null)
                {
                    PInvoke.CoTaskMemFree(pidl);
                }
            }
        }
    }

    /// <summary>Menu messages the shell extensions need to build their submenus and owner-drawn items.</summary>
    private static LRESULT OnMessage(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam, nuint id, nuint data)
    {
        if (msg is PInvoke.WM_INITMENUPOPUP or PInvoke.WM_DRAWITEM or PInvoke.WM_MEASUREITEM or PInvoke.WM_MENUCHAR)
        {
            try
            {
                if (_menu3 is not null)
                {
                    LRESULT result;
                    _menu3.HandleMenuMsg2(msg, wParam, lParam, &result);
                    return result;
                }

                if (_menu2 is not null)
                {
                    _menu2.HandleMenuMsg(msg, wParam, lParam);
                    return new LRESULT(0);
                }
            }
            catch (COMException)
            {
                // the extension did not want this message; let the window have it
            }
        }

        return PInvoke.DefSubclassProc(hwnd, msg, wParam, lParam);
    }
}
