using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PlainCommander.App.Services;

namespace PlainCommander.App;

/// <summary>
/// Our own entry point (DISABLE_XAML_GENERATED_MAIN) so the same exe can start as the elevated helper,
/// before any XAML exists. Otherwise it does what the generated Main does.
/// </summary>
public static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == ElevationService.Switch)
        {
            return ElevationService.RunHelper(args[1], args[2]);
        }

        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    [LibraryImport("Microsoft.ui.xaml.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial void XamlCheckProcessRequirements();
}
