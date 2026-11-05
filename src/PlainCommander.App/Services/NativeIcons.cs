using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace PlainCommander.App.Services;

/// <summary>Shell icon → premultiplied BGRA pixels. Call from a background STA thread.</summary>
internal static unsafe class NativeIcons
{
    public static byte[]? Load(string path, bool usePath, bool isDirectory, out int width, out int height)
    {
        width = height = 0;
        var flags = SHGFI_FLAGS.SHGFI_ICON; // large (32 px): crisp at 200 %, scaled down to 16 DIP otherwise
        var attributes = FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL;
        var query = path;
        if (!usePath)
        {
            // Icon by type only: no disk access, so it is fast and works for files that are gone.
            flags |= SHGFI_FLAGS.SHGFI_USEFILEATTRIBUTES;
            if (isDirectory)
            {
                attributes = FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY;
                query = "folder";
            }
            else
            {
                query = "file" + Path.GetExtension(path);
            }
        }

        var info = default(SHFILEINFOW);
        nuint result;
        fixed (char* q = query)
        {
            result = PInvoke.SHGetFileInfo(q, attributes, &info, (uint)sizeof(SHFILEINFOW), flags);
        }

        if (result == 0 || info.hIcon.IsNull)
        {
            return null;
        }

        try
        {
            return ToPixels(info.hIcon, out width, out height);
        }
        finally
        {
            PInvoke.DestroyIcon(info.hIcon);
        }
    }

    private static byte[]? ToPixels(HICON icon, out int width, out int height)
    {
        width = height = 0;
        ICONINFO iconInfo;
        if (!PInvoke.GetIconInfo(icon, &iconInfo))
        {
            return null;
        }

        try
        {
            if (iconInfo.hbmColor.IsNull)
            {
                return null; // monochrome icon; not worth supporting
            }

            BITMAP bitmap;
            if (PInvoke.GetObject(iconInfo.hbmColor, sizeof(BITMAP), &bitmap) == 0)
            {
                return null;
            }

            width = bitmap.bmWidth;
            height = bitmap.bmHeight;
            var color = ReadBits(iconInfo.hbmColor, width, height);
            if (color is null)
            {
                return null;
            }

            var hasAlpha = false;
            for (var i = 3; i < color.Length; i += 4)
            {
                if (color[i] != 0)
                {
                    hasAlpha = true;
                    break;
                }
            }

            if (hasAlpha)
            {
                Premultiply(color);
            }
            else
            {
                // Old-style icon: transparency comes from the AND mask (black = opaque).
                var mask = iconInfo.hbmMask.IsNull ? null : ReadBits(iconInfo.hbmMask, width, height);
                for (var i = 0; i < color.Length; i += 4)
                {
                    var opaque = mask is null || mask[i] == 0;
                    if (opaque)
                    {
                        color[i + 3] = 255;
                    }
                    else
                    {
                        color[i] = color[i + 1] = color[i + 2] = color[i + 3] = 0;
                    }
                }
            }

            return color;
        }
        finally
        {
            if (!iconInfo.hbmColor.IsNull)
            {
                PInvoke.DeleteObject(iconInfo.hbmColor);
            }

            if (!iconInfo.hbmMask.IsNull)
            {
                PInvoke.DeleteObject(iconInfo.hbmMask);
            }
        }
    }

    private static byte[]? ReadBits(HBITMAP bitmap, int width, int height)
    {
        var info = default(BITMAPINFO);
        info.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = width;
        info.bmiHeader.biHeight = -height; // top-down rows
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = 0; // BI_RGB

        var pixels = new byte[width * height * 4];
        var dc = PInvoke.GetDC(HWND.Null);
        try
        {
            fixed (byte* p = pixels)
            {
                return PInvoke.GetDIBits(dc, bitmap, 0, (uint)height, p, &info, DIB_USAGE.DIB_RGB_COLORS) == 0 ? null : pixels;
            }
        }
        finally
        {
            _ = PInvoke.ReleaseDC(HWND.Null, dc);
        }
    }

    private static void Premultiply(byte[] bgra)
    {
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            if (a == 255)
            {
                continue;
            }

            bgra[i] = (byte)(bgra[i] * a / 255);
            bgra[i + 1] = (byte)(bgra[i + 1] * a / 255);
            bgra[i + 2] = (byte)(bgra[i + 2] * a / 255);
        }
    }
}
