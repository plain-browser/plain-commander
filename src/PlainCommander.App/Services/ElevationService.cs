using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using PlainCommander.Core.Elevation;
using Serilog;

namespace PlainCommander.App.Services;

/// <summary>
/// "As administrator": starts this same exe elevated (UAC) in helper mode, without a window, and talks to it
/// over a named pipe. One helper serves every elevated operation until it has been idle for a while or the
/// app closes. Both ends check who is on the other side of the pipe: the app accepts only the process it
/// started, the helper only a Plain Commander process with the id it was given.
/// </summary>
internal sealed partial class ElevationService : IElevation, IAsyncDisposable
{
    /// <summary>Command-line switch of helper mode: <c>--elevated &lt;pipe&gt; &lt;app process id&gt;</c>.</summary>
    public const string Switch = "--elevated";

    private const string PipePrefix = "PlainCommander.Elevated.";
    private const int ErrorCancelled = 1223;
    private static readonly TimeSpan IdleExit = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ElevationClient? _client;

    /// <summary>The app itself already runs as administrator: nothing to offer.</summary>
    public static bool IsAdministrator => Environment.IsPrivilegedProcess;

    /// <summary>
    /// The Microsoft Store refused <c>allowElevation</c>, so the Store package is built without it and carries this
    /// marker file (build\msix-store.ps1): there "As administrator" is not offered, since it would fail.
    /// </summary>
    public static bool IsDisabled { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "no-elevation.flag"));

    public async Task<ElevationClient> ConnectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_client is { IsConnected: true })
            {
                return _client;
            }

            if (_client is not null)
            {
                await _client.DisposeAsync();
                _client = null;
            }

            _client = await StartAsync(cancellationToken);
            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync(); // the helper sees the pipe close and exits
        }

        _gate.Dispose();
    }

    private static async Task<ElevationClient> StartAsync(CancellationToken cancellationToken)
    {
        var name = PipePrefix + Guid.NewGuid().ToString("N");
        var server = CreatePipe(name);
        try
        {
            Process? helper;
            try
            {
                var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("Unknown program path."))
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = $"{Switch} {name} {Environment.ProcessId}",
                };
                helper = await Task.Run(() => Process.Start(start), cancellationToken); // blocks while UAC asks
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                Log.Information("UAC declined");
                throw new OperationCanceledException("The administrator prompt was declined.", ex);
            }
            catch (Win32Exception ex)
            {
                throw new IOException(ex.Message, ex);
            }

            using (helper)
            {
                if (helper is null)
                {
                    throw new IOException("The administrator process did not start.");
                }

                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                wait.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    await server.WaitForConnectionAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new IOException("The administrator process did not connect.");
                }

                if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out var client) || client != (uint)helper.Id)
                {
                    throw new IOException("An unexpected process connected to the administrator pipe.");
                }

                Log.Information("Elevated helper {Pid} connected", helper.Id);
            }

            return new ElevationClient(server);
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Only this user and administrators may open the pipe: with "over the shoulder" UAC the helper runs as
    /// another (administrator) account. One instance, created before the helper starts, so nobody can take the name.
    /// </summary>
    private static NamedPipeServerStream CreatePipe(string name)
    {
        var security = new PipeSecurity();
        using (var me = WindowsIdentity.GetCurrent())
        {
            security.AddAccessRule(new PipeAccessRule(me.User!, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        }

        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
    }

    /// <summary>Helper mode (the elevated process): serve the app on the pipe, then exit. No window, no XAML.</summary>
    public static int RunHelper(string pipeName, string appProcessId)
    {
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlainCommander", "logs");
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(logDir, "elevated-.log"), formatProvider: System.Globalization.CultureInfo.InvariantCulture, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();
        try
        {
            if (!pipeName.StartsWith(PipePrefix, StringComparison.Ordinal) || !uint.TryParse(appProcessId, out var app))
            {
                Log.Error("Elevated helper: bad arguments");
                return 2;
            }

            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(10_000);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var server) || server != app || !IsThisProgram(server))
            {
                Log.Error("Elevated helper: the pipe is not served by Plain Commander ({Server})", server);
                return 3;
            }

            Log.Information("Elevated helper serving process {App}", app);
            ElevationHost.RunAsync(pipe, IdleExit).GetAwaiter().GetResult();
            Log.Information("Elevated helper done");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error(ex, "Elevated helper failed");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>The process on the other end runs the same exe as this one.</summary>
    private static bool IsThisProgram(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return string.Equals(process.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            Log.Warning(ex, "Cannot check process {Pid}", processId);
            return false;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafeHandle pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafeHandle pipe, out uint serverProcessId);
}
