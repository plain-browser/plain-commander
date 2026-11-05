using Microsoft.UI.Xaml;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PlainCommander.App.Helpers;

/// <summary>
/// Puts a window in front of the others and on screen. Window.Activate() asks Windows for the foreground, and
/// Windows refuses when another process took it in the meantime (a cloud client busy with a download, a toast), so the
/// window would open behind the main one. The topmost-then-normal step raises it in the z-order without foreground rights.
/// </summary>
internal static class WindowFront
{
    public static void Bring(Window window)
    {
        window.Activate();
        var hwnd = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(window));
        if (PInvoke.IsIconic(hwnd))
        {
            PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_RESTORE);
        }

        const SET_WINDOW_POS_FLAGS Flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE;
        PInvoke.SetWindowPos(hwnd, HWND.HWND_TOPMOST, 0, 0, 0, 0, Flags | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        PInvoke.SetWindowPos(hwnd, HWND.HWND_NOTOPMOST, 0, 0, 0, 0, Flags);
        PInvoke.SetForegroundWindow(hwnd);
        Log.Debug("Brought window {Handle} to front; foreground is now {Foreground}", (nint)hwnd, (nint)PInvoke.GetForegroundWindow());
    }

    /// <summary>Raises the window again unless it already is the foreground one (after a slow load, say).</summary>
    public static void BringIfBehind(Window window)
    {
        var hwnd = new HWND(WinRT.Interop.WindowNative.GetWindowHandle(window));
        if (PInvoke.GetForegroundWindow() != hwnd)
        {
            Bring(window);
        }
    }
}
