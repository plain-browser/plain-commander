using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using PlainCommander.App.Services;
using PlainCommander.Core.FileSystem;
using PlainCommander.Core.Operations;
using PlainCommander.Core.Settings;
using PlainCommander.Presentation;
using Serilog;

namespace PlainCommander.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlainCommander", "logs");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(Environment.GetEnvironmentVariable("PLAINCOMMANDER_TRACE") == "1" ? Serilog.Events.LogEventLevel.Verbose : Serilog.Events.LogEventLevel.Debug)
            .WriteTo.File(
                Path.Combine(logDir, "app-.log"),
                formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        var ui = DispatcherQueue.GetForCurrentThread();
        var dialogs = new DialogService(ui);
        IntPtr OwnerWindow() => dialogs.Owner is { } w ? WinRT.Interop.WindowNative.GetWindowHandle(w) : 0;
        PlainCommander.Core.Archives.ArchivePasswords.Prompt = dialogs;
        Services = new ServiceCollection()
            .AddSingleton<IFileSystem>(_ => new PlainCommander.Core.Archives.ArchiveFileSystem(
                new PlainCommander.Core.Network.NetworkFileSystem(new LocalFileSystem(), new PlainCommander.Core.Network.WindowsNetworkBrowser(OwnerWindow), PlainCommander.Core.Network.WslDistros.Find)))
            .AddSingleton<ILauncher>(_ => new ShellLauncher())
            .AddSingleton<IIconProvider>(_ => new IconService(ui))
            .AddSingleton<IDialogService>(dialogs)
            .AddSingleton<IOperationPrompts>(dialogs)
            .AddSingleton<IRecycleBin>(_ => new RecycleBin(() => dialogs.Owner is { } w ? WinRT.Interop.WindowNative.GetWindowHandle(w) : 0))
            .AddSingleton(_ => new SettingsStore(SettingsStore.DefaultPath))
            .AddSingleton(sp => new MainViewModel(
                sp.GetRequiredService<IFileSystem>(),
                sp.GetRequiredService<ILauncher>(),
                sp.GetRequiredService<SettingsStore>(),
                sp.GetRequiredService<IDialogService>(),
                sp.GetRequiredService<IOperationPrompts>(),
                sp.GetRequiredService<IRecycleBin>(),
                sp.GetRequiredService<IIconProvider>(),
                new RemoteSetup(dialogs, Secrets),
                ElevationService.IsAdministrator || ElevationService.IsDisabled ? null : Elevation,
                new ClipboardService(),
                new PlainCommander.Core.Details.WindowsFileDetails(),
                new DiskImages(OwnerWindow),
                new PlainCommander.Core.Undo.WindowsRecycleBinRestore(),
                PlainCommander.Core.FileSystem.CloudFolders.Find,
                PlainCommander.Core.Network.WslDistros.Find))
            .BuildServiceProvider();

        UnhandledException += (_, e) => Log.Fatal(e.Exception, "Unhandled exception");
    }

    public static new App Current => (App)Application.Current;

    public IServiceProvider Services { get; }

    /// <summary>The "as administrator" helper process, started on first use.</summary>
    internal ElevationService Elevation { get; } = new();

    /// <summary>Remembered FTP/SFTP passwords (Windows Credential Manager).</summary>
    public PlainCommander.Core.Remote.ISecretStore Secrets { get; } = new CredentialStore();

    /// <summary>
    /// Resources are picked when the first window is built, so this runs before it. The Windows App SDK
    /// variant of ApplicationLanguages works without package identity; the Windows.Globalization one throws.
    /// Code-side strings (Texts) follow CurrentUICulture, so that is set as well.
    /// </summary>
    private static void ApplyLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return; // "" means Windows' language; the override setter rejects an empty tag (E_INVALIDARG)
        }

        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = language;
            var culture = new System.Globalization.CultureInfo(language);
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
            System.Globalization.CultureInfo.CurrentUICulture = culture;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Globalization.CultureNotFoundException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Log.Warning(ex, "Language override {Language} not applied", language);
        }
    }

    /// <summary>Files viewed or launched from inside archives land in %TEMP%\PlainCommander; the last session's leftovers go at startup.</summary>
    private static void CleanTempFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "PlainCommander");
        Task.Run(() =>
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Debug(ex, "Temp folder not fully cleaned");
            }
        });
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Information("Plain Commander starting");
        ApplyLanguage(Services.GetRequiredService<MainViewModel>().Settings.Language);
        CleanTempFiles();
        _window = new MainWindow();
        _window.Activate();
    }
}
