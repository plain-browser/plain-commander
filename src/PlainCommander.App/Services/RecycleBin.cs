using System.Runtime.InteropServices;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Operations;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace PlainCommander.App.Services;

/// <summary>
/// Recycle Bin through the shell's IFileOperation, the same engine Explorer uses. FOF_WANTNUKEWARNING
/// makes Windows ask before anything is deleted permanently (network drives, items too big for the bin).
/// </summary>
internal sealed class RecycleBin : IRecycleBin
{
    private readonly Func<nint> _owner;

    public RecycleBin(Func<nint> owner)
    {
        _owner = owner;
    }

    public Task RecycleAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = _owner();
        var thread = new Thread(() =>
        {
            try
            {
                Recycle(paths, owner);
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Recycle Bin",
        };
        thread.SetApartmentState(ApartmentState.STA); // shell COM objects expect STA
        thread.Start();
        return done.Task;
    }

    /// <summary>
    /// The shell cannot address a name that ends in a dot or space (neither plainly nor behind \\?\), and
    /// Explorer cannot delete such items at all. We rename to the nearest plain name first and recycle that.
    /// </summary>
    private static string Recyclable(string path)
    {
        if (ExactPath.Native(path) == path)
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var plain = Path.GetFileName(path).TrimEnd('.', ' ');
        if (plain.Length == 0)
        {
            plain = "item";
        }

        var target = Path.Combine(directory, plain);
        for (var n = 2; File.Exists(target) || Directory.Exists(target); n++)
        {
            target = Path.Combine(directory, $"{plain} ({n})");
        }

        var native = ExactPath.Prefixed(path);
        var nativeTarget = ExactPath.Prefixed(target);
        if (Directory.Exists(native))
        {
            Directory.Move(native, nativeTarget);
        }
        else
        {
            File.Move(native, nativeTarget);
        }

        return target;
    }

    private static unsafe IShellItem ShellItem(string path)
    {
        var iid = typeof(IShellItem).GUID;
        fixed (char* p = path)
        {
            PInvoke.SHCreateItemFromParsingName(p, null, &iid, out var item).ThrowOnFailure();
            return (IShellItem)item;
        }
    }

    private static void Recycle(IReadOnlyList<string> paths, nint owner)
    {
        var operation = (IFileOperation)new FileOperation();
        try
        {
            operation.SetOperationFlags(
                FILEOPERATION_FLAGS.FOF_ALLOWUNDO
                | FILEOPERATION_FLAGS.FOF_NOCONFIRMATION
                | FILEOPERATION_FLAGS.FOF_WANTNUKEWARNING
                | FILEOPERATION_FLAGS.FOFX_RECYCLEONDELETE);
            if (owner != 0)
            {
                operation.SetOwnerWindow(new HWND(owner));
            }

            foreach (var path in paths)
            {
                operation.DeleteItem(ShellItem(Recyclable(path)), null);
            }

            operation.PerformOperations();
            operation.GetAnyOperationsAborted(out var aborted);
            if (aborted)
            {
                throw new IOException("The Recycle Bin operation was stopped.");
            }
        }
        catch (COMException ex)
        {
            throw new IOException(ex.Message, ex);
        }
        finally
        {
            Marshal.ReleaseComObject(operation);
        }
    }
}
