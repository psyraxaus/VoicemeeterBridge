using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

// BridgeProbe: a tiny console client for VoicemeeterBridge.
//
//   BridgeProbe <ws://host:5088/ws> <token> [options]
//
//   --watch  a,b,c       parameters to watch (default: a few strip/bus controls)
//   --get    a,b,c       read these once after connecting
//   --set    name=value  write one parameter (value is a number, or text in quotes)
//   --levels t:c,t:c     stream level meters, e.g. 3:0,3:1 (type:channel)
//   --once               exit after the set result (or after the snapshot when no --set)
//
// Every frame from the agent is printed as received.

if (args.Length < 2 || args[0] is "-h" or "--help")
{
    Console.Error.WriteLine("usage: BridgeProbe <ws://host:5088/ws> <token> [--watch a,b] [--get a,b] [--set name=value] [--levels 3:0,3:1] [--once]");
    return 1;
}

var uri = new Uri(args[0]);
var token = args[1];
var watch = new List<string> { "Strip[0].Gain", "Strip[0].Mute", "Bus[0].Gain", "Bus[0].Mute" };
var get = new List<string>();
string? setName = null;
string? setValue = null;
var levels = new List<(int Type, int Channel)>();
var once = false;

for (int i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--watch":
            watch = Split(args[++i]);
            break;
        case "--get":
            get = Split(args[++i]);
            break;
        case "--set":
            var eq = args[++i].IndexOf('=');
            if (eq <= 0)
            {
                Console.Error.WriteLine("--set needs name=value");
                return 1;
            }

            setName = args[i][..eq];
            setValue = args[i][(eq + 1)..];
            break;
        case "--levels":
            foreach (var pair in Split(args[++i]))
            {
                var parts = pair.Split(':');
                levels.Add((int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture)));
            }

            break;
        case "--once":
            once = true;
            break;
        default:
            Console.Error.WriteLine($"unknown option {args[i]}");
            return 1;
    }
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

using var ws = new ClientWebSocket();
Console.WriteLine($"connecting to {uri} ...");
await ws.ConnectAsync(uri, cts.Token);

await SendAsync(new { op = "hello", token, protocol = 1 });
if (watch.Count > 0)
{
    await SendAsync(new { op = "watch", @params = watch });
}

if (get.Count > 0)
{
    await SendAsync(new { op = "get", id = "get-1", @params = get });
}

if (levels.Count > 0)
{
    await SendAsync(new { op = "levels", enabled = true, channels = levels.Select(l => new { type = l.Type, channel = l.Channel }) });
}

if (setName is not null)
{
    object value = double.TryParse(setValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
        ? number
        : setValue!.Trim('"');
    await SendAsync(new { op = "set", id = "set-1", param = setName, value });
}

var pingTimer = new PeriodicTimer(TimeSpan.FromSeconds(15));
var pingTask = Task.Run(async () =>
{
    while (await pingTimer.WaitForNextTickAsync(cts.Token))
    {
        await SendAsync(new { op = "ping" });
    }
}, cts.Token);

var buffer = new byte[64 * 1024];
try
{
    while (ws.State == WebSocketState.Open && !cts.IsCancellationRequested)
    {
        var result = await ws.ReceiveAsync(buffer, cts.Token);
        if (result.MessageType == WebSocketMessageType.Close)
        {
            Console.WriteLine($"closed by agent: {ws.CloseStatus} {ws.CloseStatusDescription}");
            break;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {text}");

        if (!once)
        {
            continue;
        }

        using var doc = JsonDocument.Parse(text);
        var op = doc.RootElement.GetProperty("op").GetString();
        if ((setName is not null && op == "result") || (setName is null && op == "snapshot"))
        {
            break;
        }
    }
}
catch (OperationCanceledException)
{
}

if (ws.State == WebSocketState.Open)
{
    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    try
    {
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token);
    }
    catch (Exception)
    {
        // The agent may already be gone; nothing to do.
    }
}

return 0;

static List<string> Split(string csv) =>
    csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

async Task SendAsync(object frame)
{
    var json = JsonSerializer.Serialize(frame);
    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} -> {json}");
    await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cts.Token);
}
