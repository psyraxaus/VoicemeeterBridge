using Microsoft.Extensions.Options;
using Serilog;
using VoicemeeterBridge.Configuration;
using VoicemeeterBridge.Mixer;
using VoicemeeterBridge.Tray;
using VoicemeeterBridge.WebSockets;

namespace VoicemeeterBridge;

/// <summary>
/// Entry point. The UI thread runs the WinForms message loop for the tray icon; the web
/// host runs on the thread pool and is stopped when the loop ends.
/// </summary>
internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\VoicemeeterBridge.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        var launch = LaunchOptions.Parse(args);

        using var singleInstance = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("VoicemeeterBridge is already running. Look for its icon in the notification area.",
                "VoicemeeterBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        WebApplication app;
        try
        {
            app = BridgeApp.Build(launch);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "VoicemeeterBridge failed to build the host");
            Log.CloseAndFlush();
            MessageBox.Show(ex.Message, "VoicemeeterBridge failed to start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        var log = app.Services.GetRequiredService<ILogger>();
        var ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        var context = new ApplicationContext();
        Exception? hostFailure = null;

        var hostTask = Task.Run(async () =>
        {
            try
            {
                await app.RunAsync();
            }
            catch (Exception ex)
            {
                hostFailure = ex;
                log.Fatal(ex, "Web host stopped unexpectedly");
            }
            finally
            {
                ui.Post(_ => context.ExitThread(), null);
            }
        });

        try
        {
            using var tray = new TrayIcon(
                app.Services.GetRequiredService<IMixerStateService>(),
                app.Services.GetRequiredService<SessionRegistry>(),
                app.Services.GetRequiredService<IOptions<BridgeOptions>>().Value,
                log,
                ui,
                exit: () => context.ExitThread());

            Application.Run(context);
        }
        finally
        {
            // The message loop has ended, so there is no UI thread left to deadlock:
            // blocking here is the normal way to finish a WinForms Main.
            app.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            hostTask.GetAwaiter().GetResult();
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Log.CloseAndFlush();
        }

        if (hostFailure is not null)
        {
            MessageBox.Show(hostFailure.Message, "VoicemeeterBridge stopped", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 3;
        }

        return 0;
    }
}
