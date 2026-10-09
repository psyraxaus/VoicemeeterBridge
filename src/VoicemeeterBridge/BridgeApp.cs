using System.Net;
using Serilog;
using VoicemeeterBridge.Configuration;
using VoicemeeterBridge.Mixer;
using VoicemeeterBridge.Native;
using VoicemeeterBridge.WebSockets;

namespace VoicemeeterBridge;

/// <summary>Command line switches understood by the bridge.</summary>
/// <param name="UseFake">Run against <see cref="FakeVoicemeeterClient"/> instead of the real DLL.</param>
/// <param name="HostArgs">Remaining arguments passed to the generic host.</param>
public sealed record LaunchOptions(bool UseFake, string[] HostArgs)
{
    /// <summary>Parses the process arguments.</summary>
    public static LaunchOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var useFake = args.Any(a => string.Equals(a, "--fake", StringComparison.OrdinalIgnoreCase));
        var rest = args.Where(a => !string.Equals(a, "--fake", StringComparison.OrdinalIgnoreCase)).ToArray();
        return new LaunchOptions(useFake, rest);
    }
}

/// <summary>
/// Builds the web host: configuration layering, Serilog, the mixer service and the
/// WebSocket endpoint. Kept separate from <c>Program</c> so tests can build the same host
/// on an in-memory server.
/// </summary>
public static class BridgeApp
{
    /// <summary>Per-user data folder: <c>%LOCALAPPDATA%\VoicemeeterBridge</c>.</summary>
    public static string DataFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoicemeeterBridge");

    /// <summary>Folder for rolling log files.</summary>
    public static string LogFolder => Path.Combine(DataFolder, "logs");

    /// <summary>Per-user settings override file.</summary>
    public static string OverrideSettingsPath => Path.Combine(DataFolder, "appsettings.json");

    /// <summary>
    /// Builds, but does not start, the application.
    /// </summary>
    /// <param name="launch">Parsed command line switches.</param>
    /// <param name="configure">
    /// Optional hook run after the standard services are registered; tests use it to swap in
    /// a fake client and an in-memory server.
    /// </param>
    /// <param name="useUserFiles">
    /// When true (the default) the user's override file and log folder are used. Tests pass
    /// false to stay inside the test process.
    /// </param>
    public static WebApplication Build(LaunchOptions launch, Action<WebApplicationBuilder>? configure = null, bool useUserFiles = true)
    {
        ArgumentNullException.ThrowIfNull(launch);

        string? generatedToken = null;
        if (useUserFiles)
        {
            var (_, generated) = TokenStore.EnsureToken(OverrideSettingsPath);
            if (generated)
            {
                generatedToken = OverrideSettingsPath;
            }
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = launch.HostArgs,
            ContentRootPath = AppContext.BaseDirectory,
        });

        if (useUserFiles)
        {
            builder.Configuration.AddJsonFile(OverrideSettingsPath, optional: true, reloadOnChange: false);
        }

        builder.Host.UseSerilog((context, services, loggerConfiguration) =>
        {
            loggerConfiguration
                .ReadFrom.Configuration(context.Configuration)
                .Enrich.FromLogContext()
                .WriteTo.Debug();
            if (useUserFiles)
            {
                loggerConfiguration.WriteTo.File(
                    Path.Combine(LogFolder, "bridge-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    shared: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
            }
        });

        builder.Services.Configure<BridgeOptions>(builder.Configuration.GetSection(BridgeOptions.SectionName));
        builder.Services.AddSingleton<SessionRegistry>();
        builder.Services.AddSingleton<MixerStateService>();
        builder.Services.AddSingleton<IMixerStateService>(sp => sp.GetRequiredService<MixerStateService>());
        builder.Services.AddHostedService(sp => sp.GetRequiredService<MixerStateService>());

        if (launch.UseFake)
        {
            builder.Services.AddSingleton<IVoicemeeterClient>(_ =>
            {
                var fake = new FakeVoicemeeterClient();
                fake.SeedDefaults();
                return fake;
            });
        }
        else
        {
            builder.Services.AddSingleton<IVoicemeeterClient>(sp =>
            {
                var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BridgeOptions>>().Value;
                var dllPath = VoicemeeterLocator.ResolveDllPath(options.DllPath);
                sp.GetRequiredService<ILogger>().Information("Using Voicemeeter Remote DLL at {DllPath}", dllPath);
                return new NativeVoicemeeterClient(dllPath);
            });
        }

        var bridge = builder.Configuration.GetSection(BridgeOptions.SectionName).Get<BridgeOptions>() ?? new BridgeOptions();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            var address = IPAddress.TryParse(bridge.ListenAddress, out var parsed) ? parsed : IPAddress.Any;
            kestrel.Listen(address, bridge.Port);
        });

        configure?.Invoke(builder);

        var app = builder.Build();
        var log = app.Services.GetRequiredService<ILogger>();
        log.Information("VoicemeeterBridge {Version} starting (fake={Fake}, listen={Address}:{Port})",
            AppVersion.Current, launch.UseFake, bridge.ListenAddress, bridge.Port);
        if (generatedToken is not null)
        {
            log.Information("Generated a new auth token and stored it in {Path}", generatedToken);
        }

        WebSocketEndpoint.Map(app);
        return app;
    }
}
