using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PlainCommander.Core.Model;
using PlainCommander.Presentation;
using Serilog;

namespace PlainCommander.App.Services;

/// <summary>
/// Shell icons with a cache. Ordinary files share one icon per extension; files that carry their own
/// icon (.exe, .lnk, .ico…) are looked up by path. Shell calls run on one background STA thread,
/// bitmaps are created on the UI thread. Cache and pending lists are touched only on the UI thread.
/// </summary>
internal sealed class IconService : IIconProvider, IDisposable
{
    private static readonly HashSet<string> OwnIconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "lnk", "ico", "cur", "ani", "url", "msc", "scr", "cpl", "appref-ms",
    };

    private readonly DispatcherQueue _ui;
    private readonly Dictionary<string, ImageSource> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Action<object?>>> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<Request> _queue = [];
    private readonly Thread _worker;

    public IconService(DispatcherQueue ui)
    {
        _ui = ui;
        _worker = new Thread(Work) { IsBackground = true, Name = "Shell icons" };
        _worker.SetApartmentState(ApartmentState.STA);
        _worker.Start();
    }

    public object? GetIcon(FileEntry entry, Action<object?> onLoaded)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var (key, usePath) = KeyFor(entry);
        if (_cache.TryGetValue(key, out var image))
        {
            return image;
        }

        if (_failed.Contains(key))
        {
            return null;
        }

        if (_pending.TryGetValue(key, out var waiters))
        {
            waiters.Add(onLoaded);
            return null;
        }

        _pending[key] = [onLoaded];
        _queue.Add(new Request(key, entry.FullPath, usePath, entry.IsDirectory));
        return null;
    }

    public void Dispose() => _queue.CompleteAdding();

    private static (string Key, bool UsePath) KeyFor(FileEntry entry)
    {
        if (entry.IsDirectory)
        {
            return ("<dir>", false);
        }

        var ext = entry.Extension;
        return OwnIconExtensions.Contains(ext) ? ("path:" + entry.FullPath, true) : ("ext:" + ext, false);
    }

    private void Work()
    {
        foreach (var request in _queue.GetConsumingEnumerable())
        {
            byte[]? pixels = null;
            int width = 0, height = 0;
            try
            {
                pixels = NativeIcons.Load(request.Path, request.UsePath, request.IsDirectory, out width, out height);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Icon for {Path} failed", request.Path);
            }

            _ui.TryEnqueue(() => Complete(request.Key, pixels, width, height));
        }
    }

    private void Complete(string key, byte[]? pixels, int width, int height)
    {
        ImageSource? image = null;
        if (pixels is not null && width > 0 && height > 0)
        {
            var bitmap = new WriteableBitmap(width, height);
            using (var stream = bitmap.PixelBuffer.AsStream())
            {
                stream.Write(pixels);
            }

            bitmap.Invalidate();
            image = bitmap;
            _cache[key] = image;
        }
        else
        {
            _failed.Add(key);
        }

        if (_pending.Remove(key, out var waiters))
        {
            foreach (var callback in waiters)
            {
                callback(image);
            }
        }
    }

    private sealed record Request(string Key, string Path, bool UsePath, bool IsDirectory);
}
