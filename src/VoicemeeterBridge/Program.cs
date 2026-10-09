using Serilog;

namespace VoicemeeterBridge;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var launch = LaunchOptions.Parse(args);

        WebApplication app;
        try
        {
            app = BridgeApp.Build(launch);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "VoicemeeterBridge failed to start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        try
        {
            app.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "VoicemeeterBridge crashed");
            MessageBox.Show(ex.Message, "VoicemeeterBridge stopped", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 3;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
