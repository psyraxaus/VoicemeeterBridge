using System.Text.Json;
using System.Text.Json.Serialization;
using VoicemeeterBridge.Mixer;
using VoicemeeterBridge.Native;

namespace VoicemeeterBridge.Protocol;

/// <summary>
/// Builds the JSON text for every frame the agent sends. Each method returns a complete
/// frame so the session can hand it straight to the socket.
/// </summary>
public static class ServerMessages
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>Shared serializer settings, also used by tests to read frames back.</summary>
    public static JsonSerializerOptions Options => JsonOptions;

    /// <summary><c>welcome</c>: sent once after a successful <c>hello</c>.</summary>
    public static string Welcome(VoicemeeterStatus status) =>
        Serialize(new
        {
            op = "welcome",
            protocol = ClientMessageParser.ProtocolVersion,
            agentVersion = AppVersion.Current,
            voicemeeter = StatusBody(status),
        });

    /// <summary><c>status</c>: sent whenever the Voicemeeter connection changes.</summary>
    public static string Status(VoicemeeterStatus status) =>
        Serialize(new { op = "status", voicemeeter = StatusBody(status) });

    /// <summary><c>snapshot</c>: current values after a <c>watch</c>.</summary>
    public static string Snapshot(IReadOnlyDictionary<string, ParameterReadResult> results)
    {
        var (values, errors) = Split(results);
        return Serialize(new { op = "snapshot", values, errors });
    }

    /// <summary><c>values</c>: reply to a <c>get</c>.</summary>
    public static string Values(string? id, IReadOnlyDictionary<string, ParameterReadResult> results)
    {
        var (values, errors) = Split(results);
        return Serialize(new { op = "values", id, values, errors });
    }

    /// <summary><c>changed</c>: a watched parameter has a new value.</summary>
    public static string Changed(string param, MixerValue value) =>
        Serialize(new { op = "changed", param, value = value.AsObject() });

    /// <summary><c>result</c>: reply to a <c>set</c>.</summary>
    public static string Result(string? id, bool ok, string? error = null) =>
        Serialize(new { op = "result", id, ok, error });

    /// <summary><c>levels</c>: one batch of meter readings.</summary>
    public static string Levels(IEnumerable<LevelReading> readings) =>
        Serialize(new
        {
            op = "levels",
            values = readings.Select(r => new { type = r.Channel.Type, channel = r.Channel.Channel, db = MathF.Round(r.Db, 1) }),
        });

    /// <summary><c>pong</c>: reply to <c>ping</c>.</summary>
    public static string Pong() => "{\"op\":\"pong\"}";

    /// <summary><c>error</c>: a frame was rejected and carried no <c>id</c> to answer with a <c>result</c>.</summary>
    public static string Error(string message) => Serialize(new { op = "error", error = message });

    private static object StatusBody(VoicemeeterStatus status) => new
    {
        connected = status.Connected,
        kind = status.Connected ? KindName(status.Kind) : null,
        version = status.Connected ? status.Version : null,
    };

    /// <summary>Protocol name of a Voicemeeter edition.</summary>
    public static string? KindName(VoicemeeterKind kind) => kind switch
    {
        VoicemeeterKind.Standard => "standard",
        VoicemeeterKind.Banana => "banana",
        VoicemeeterKind.Potato => "potato",
        _ => null,
    };

    private static (Dictionary<string, object> Values, Dictionary<string, string>? Errors) Split(
        IReadOnlyDictionary<string, ParameterReadResult> results)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        Dictionary<string, string>? errors = null;
        foreach (var (name, result) in results)
        {
            if (result.Ok)
            {
                values[name] = result.Value!.Value.AsObject();
            }
            else
            {
                errors ??= new Dictionary<string, string>(StringComparer.Ordinal);
                errors[name] = result.Error!;
            }
        }

        return (values, errors);
    }

    private static string Serialize<T>(T frame) => JsonSerializer.Serialize(frame, JsonOptions);
}
