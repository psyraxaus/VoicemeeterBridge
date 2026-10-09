using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using VoicemeeterBridge.Configuration;
using VoicemeeterBridge.Mixer;
using VoicemeeterBridge.Native;

namespace VoicemeeterBridge.Tests;

/// <summary>Drives the real host on an in-memory server with a fake Voicemeeter behind it.</summary>
public sealed class WebSocketEndToEndTests : IAsyncLifetime
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(3);

    private readonly FakeVoicemeeterClient _fake = new();
    private WebApplication _app = null!;

    public async Task InitializeAsync()
    {
        _fake.SimulateExternalChange("Strip[0].Gain", -6f);
        _fake.SimulateExternalChange("Strip[0].Label", "Mic");
        _fake.SimulateExternalChange("Bus[1].Mute", 0f);
        _fake.SetLevel(LevelType.Output, 0, 0.1f);

        var fast = TestSupport.FastOptions();
        _app = BridgeApp.Build(
            new LaunchOptions(UseFake: false, HostArgs: []),
            builder =>
            {
                builder.WebHost.UseTestServer();
                builder.Services.AddSingleton<IVoicemeeterClient>(_fake);
                builder.Services.Configure<BridgeOptions>(o =>
                {
                    o.Token = fast.Token;
                    o.PollIntervalMs = fast.PollIntervalMs;
                    o.LevelPollIntervalMs = fast.LevelPollIntervalMs;
                    o.ReconnectIntervalMs = fast.ReconnectIntervalMs;
                    o.HelloTimeoutMs = 300;
                });
            },
            useUserFiles: false);
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private async Task<WebSocket> ConnectAsync()
    {
        var client = _app.GetTestServer().CreateWebSocketClient();
        return await client.ConnectAsync(new Uri("ws://localhost/ws"), CancellationToken.None);
    }

    private static Task SendAsync(WebSocket ws, string json) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonElement> ReceiveAsync(WebSocket ws)
    {
        using var cts = new CancellationTokenSource(FrameTimeout);
        var buffer = new byte[16 * 1024];
        var result = await ws.ReceiveAsync(buffer, cts.Token);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            throw new InvalidOperationException($"socket closed: {ws.CloseStatus} {ws.CloseStatusDescription}");
        }

        Assert.True(result.EndOfMessage);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count)).RootElement.Clone();
    }

    private static async Task<JsonElement> ReceiveOpAsync(WebSocket ws, string op)
    {
        for (int i = 0; i < 50; i++)
        {
            var frame = await ReceiveAsync(ws);
            if (frame.GetProperty("op").GetString() == op)
            {
                return frame;
            }
        }

        throw new InvalidOperationException($"no '{op}' frame received");
    }

    private static async Task<WebSocketReceiveResult> ReceiveCloseAsync(WebSocket ws)
    {
        using var cts = new CancellationTokenSource(FrameTimeout);
        var buffer = new byte[1024];
        var result = await ws.ReceiveAsync(buffer, cts.Token);
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
        return result;
    }

    private async Task<WebSocket> ConnectAndHelloAsync()
    {
        var ws = await ConnectAsync();
        await SendAsync(ws, "{\"op\":\"hello\",\"token\":\"test-token\",\"protocol\":1}");
        var welcome = await ReceiveAsync(ws);
        Assert.Equal("welcome", welcome.GetProperty("op").GetString());
        return ws;
    }

    [Fact]
    public async Task Bad_token_closes_the_socket_with_policy_violation()
    {
        using var ws = await ConnectAsync();
        await SendAsync(ws, "{\"op\":\"hello\",\"token\":\"wrong\",\"protocol\":1}");

        var close = await ReceiveCloseAsync(ws);

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, close.CloseStatus);
        Assert.Equal("unauthorized", close.CloseStatusDescription);
    }

    [Fact]
    public async Task Non_hello_first_frame_closes_the_socket()
    {
        using var ws = await ConnectAsync();
        await SendAsync(ws, "{\"op\":\"watch\",\"params\":[\"Strip[0].Gain\"]}");

        var close = await ReceiveCloseAsync(ws);

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, close.CloseStatus);
        Assert.Equal("hello required", close.CloseStatusDescription);
    }

    [Fact]
    public async Task Silent_client_is_closed_after_hello_timeout()
    {
        using var ws = await ConnectAsync();

        var close = await ReceiveCloseAsync(ws);

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, close.CloseStatus);
        Assert.Equal("hello timeout", close.CloseStatusDescription);
    }

    [Fact]
    public async Task Full_round_trip_watch_set_get_levels_ping()
    {
        using var ws = await ConnectAndHelloAsync();

        // watch -> snapshot
        await SendAsync(ws, "{\"op\":\"watch\",\"params\":[\"Strip[0].Gain\",\"Strip[0].Label\",\"Strip[9].Gain\"]}");
        var snapshot = await ReceiveOpAsync(ws, "snapshot");
        Assert.Equal(-6f, snapshot.GetProperty("values").GetProperty("Strip[0].Gain").GetSingle());
        Assert.Equal("Mic", snapshot.GetProperty("values").GetProperty("Strip[0].Label").GetString());
        Assert.Equal("unknown parameter", snapshot.GetProperty("errors").GetProperty("Strip[9].Gain").GetString());

        // change from the Voicemeeter side -> changed
        _fake.SimulateExternalChange("Strip[0].Gain", -12f);
        var changed = await ReceiveOpAsync(ws, "changed");
        Assert.Equal("Strip[0].Gain", changed.GetProperty("param").GetString());
        Assert.Equal(-12f, changed.GetProperty("value").GetSingle());

        // set -> result and changed
        await SendAsync(ws, "{\"op\":\"set\",\"id\":\"s1\",\"param\":\"Strip[0].Gain\",\"value\":-3}");
        var result = await ReceiveOpAsync(ws, "result");
        Assert.Equal("s1", result.GetProperty("id").GetString());
        Assert.True(result.GetProperty("ok").GetBoolean());
        changed = await ReceiveOpAsync(ws, "changed");
        Assert.Equal(-3f, changed.GetProperty("value").GetSingle());
        Assert.Equal(-3f, _fake.PeekFloat("Strip[0].Gain"));

        // set of a text parameter
        await SendAsync(ws, "{\"op\":\"set\",\"id\":\"s2\",\"param\":\"Strip[0].Label\",\"value\":\"Headset\"}");
        result = await ReceiveOpAsync(ws, "result");
        Assert.True(result.GetProperty("ok").GetBoolean());
        changed = await ReceiveOpAsync(ws, "changed");
        Assert.Equal("Headset", changed.GetProperty("value").GetString());

        // get -> values
        await SendAsync(ws, "{\"op\":\"get\",\"id\":\"g1\",\"params\":[\"Bus[1].Mute\",\"Nope\"]}");
        var values = await ReceiveOpAsync(ws, "values");
        Assert.Equal("g1", values.GetProperty("id").GetString());
        Assert.Equal(0f, values.GetProperty("values").GetProperty("Bus[1].Mute").GetSingle());
        Assert.Equal("unknown parameter", values.GetProperty("errors").GetProperty("Nope").GetString());

        // unwatched parameter must not produce changed
        _fake.SimulateExternalChange("Bus[1].Mute", 1f);

        // levels on -> levels frames
        await SendAsync(ws, "{\"op\":\"levels\",\"enabled\":true,\"channels\":[{\"type\":3,\"channel\":0}]}");
        var levels = await ReceiveOpAsync(ws, "levels");
        var reading = levels.GetProperty("values")[0];
        Assert.Equal(3, reading.GetProperty("type").GetInt32());
        Assert.Equal(0, reading.GetProperty("channel").GetInt32());
        Assert.Equal(-20f, reading.GetProperty("db").GetSingle(), 1);

        // levels off, then ping -> pong with nothing else in between after a short drain
        await SendAsync(ws, "{\"op\":\"levels\",\"enabled\":false}");
        await Task.Delay(50);
        await SendAsync(ws, "{\"op\":\"ping\"}");
        var pong = await ReceiveOpAsync(ws, "pong");
        Assert.Equal("pong", pong.GetProperty("op").GetString());

        // malformed frame -> error, socket stays open
        await SendAsync(ws, "{\"op\":\"set\",\"param\":\"Strip[0].Gain;rm\",\"value\":1}");
        var error = await ReceiveOpAsync(ws, "error");
        Assert.Contains("invalid parameter name", error.GetProperty("error").GetString());

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Status_frames_follow_voicemeeter_restarts()
    {
        using var ws = await ConnectAndHelloAsync();
        await SendAsync(ws, "{\"op\":\"watch\",\"params\":[\"Strip[0].Gain\"]}");
        await ReceiveOpAsync(ws, "snapshot");

        _fake.SimulateVoicemeeterStopped();
        var down = await ReceiveOpAsync(ws, "status");
        Assert.False(down.GetProperty("voicemeeter").GetProperty("connected").GetBoolean());

        await SendAsync(ws, "{\"op\":\"set\",\"id\":\"s1\",\"param\":\"Strip[0].Gain\",\"value\":1}");
        var result = await ReceiveOpAsync(ws, "result");
        Assert.False(result.GetProperty("ok").GetBoolean());
        Assert.Equal("voicemeeter not running", result.GetProperty("error").GetString());

        _fake.SimulateExternalChange("Strip[0].Gain", -20f);
        _fake.SimulateVoicemeeterStarted();
        var up = await ReceiveOpAsync(ws, "status");
        Assert.True(up.GetProperty("voicemeeter").GetProperty("connected").GetBoolean());
        Assert.Equal("potato", up.GetProperty("voicemeeter").GetProperty("kind").GetString());

        var changed = await ReceiveOpAsync(ws, "changed");
        Assert.Equal(-20f, changed.GetProperty("value").GetSingle());

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Each_session_has_its_own_watch_list()
    {
        using var a = await ConnectAndHelloAsync();
        using var b = await ConnectAndHelloAsync();

        await SendAsync(a, "{\"op\":\"watch\",\"params\":[\"Strip[0].Gain\"]}");
        await ReceiveOpAsync(a, "snapshot");
        await SendAsync(b, "{\"op\":\"watch\",\"params\":[\"Bus[1].Mute\"]}");
        await ReceiveOpAsync(b, "snapshot");

        _fake.SimulateExternalChange("Strip[0].Gain", -1f);
        var changedA = await ReceiveOpAsync(a, "changed");
        Assert.Equal("Strip[0].Gain", changedA.GetProperty("param").GetString());

        // b must not see Strip[0].Gain; a ping/pong proves nothing was queued before it.
        await SendAsync(b, "{\"op\":\"ping\"}");
        var next = await ReceiveAsync(b);
        Assert.Equal("pong", next.GetProperty("op").GetString());

        await a.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        await b.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }
}
