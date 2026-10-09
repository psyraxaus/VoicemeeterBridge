using Microsoft.Extensions.Options;
using VoicemeeterBridge.Configuration;
using VoicemeeterBridge.Mixer;

namespace VoicemeeterBridge.WebSockets;

/// <summary>Maps the <c>/ws</c> endpoint and a small unauthenticated <c>/health</c> probe.</summary>
public static class WebSocketEndpoint
{
    /// <summary>Path clients connect to.</summary>
    public const string Path = "/ws";

    /// <summary>Registers the endpoints on <paramref name="app"/>.</summary>
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        app.Map(Path, async (
            HttpContext context,
            IMixerStateService mixer,
            SessionRegistry registry,
            IOptions<BridgeOptions> options,
            ILogger log,
            IHostApplicationLifetime lifetime) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("WebSocket upgrade required");
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted, lifetime.ApplicationStopping);
            var remote = $"{context.Connection.RemoteIpAddress}:{context.Connection.RemotePort}";
            log.Information("Client connected from {Remote}", remote);
            var session = new ClientSession(socket, mixer, registry, options.Value, log, remote);
            await session.RunAsync(cts.Token);
        });

        app.MapGet("/health", (IMixerStateService mixer, SessionRegistry registry) => Results.Json(new
        {
            voicemeeter = new { connected = mixer.Status.Connected },
            clients = registry.Count,
        }));
    }
}
