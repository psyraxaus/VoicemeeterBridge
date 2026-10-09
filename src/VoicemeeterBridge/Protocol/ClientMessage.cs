using System.Text.Json;
using VoicemeeterBridge.Mixer;

namespace VoicemeeterBridge.Protocol;

/// <summary>Base type for every frame a client can send. See <c>docs/protocol.md</c>.</summary>
public abstract record ClientMessage;

/// <summary>First frame on every connection: presents the token.</summary>
public sealed record HelloMessage(string Token, int Protocol) : ClientMessage;

/// <summary>Adds parameters to this session's watch list.</summary>
public sealed record WatchMessage(IReadOnlyList<string> Params) : ClientMessage;

/// <summary>Removes parameters from this session's watch list.</summary>
public sealed record UnwatchMessage(IReadOnlyList<string> Params) : ClientMessage;

/// <summary>Writes one parameter.</summary>
public sealed record SetMessage(string? Id, string Param, MixerValue Value) : ClientMessage;

/// <summary>Reads parameters once.</summary>
public sealed record GetMessage(string? Id, IReadOnlyList<string> Params) : ClientMessage;

/// <summary>Turns level streaming on (for the given meters) or off.</summary>
public sealed record LevelsMessage(bool Enabled, IReadOnlyList<LevelChannel> Channels) : ClientMessage;

/// <summary>Keep-alive request.</summary>
public sealed record PingMessage : ClientMessage;

/// <summary>Thrown by <see cref="ClientMessageParser"/> for a frame that cannot be used.</summary>
public sealed class ProtocolException : Exception
{
    /// <summary>Creates the exception with a client-facing message.</summary>
    public ProtocolException(string message) : base(message)
    {
    }
}

/// <summary>Parses client JSON text frames into <see cref="ClientMessage"/> records.</summary>
public static class ClientMessageParser
{
    /// <summary>Protocol version this agent speaks.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>Largest number of parameter names accepted in one frame.</summary>
    public const int MaxParamsPerFrame = 512;

    /// <summary>Longest accepted text value for a <c>set</c>.</summary>
    public const int MaxTextValueLength = 256;

    /// <summary>Largest accepted level meter channel index.</summary>
    public const int MaxLevelChannel = 255;

    /// <summary>
    /// Parses one frame.
    /// </summary>
    /// <param name="json">The text frame body.</param>
    /// <returns>The parsed message.</returns>
    /// <exception cref="ProtocolException">The frame is malformed or fails validation.</exception>
    public static ClientMessage Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ProtocolException($"invalid json: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new ProtocolException("frame must be a json object");
            }

            var op = GetString(root, "op") ?? throw new ProtocolException("missing op");
            return op switch
            {
                "hello" => ParseHello(root),
                "watch" => new WatchMessage(GetParams(root)),
                "unwatch" => new UnwatchMessage(GetParams(root)),
                "set" => ParseSet(root),
                "get" => new GetMessage(GetString(root, "id"), GetParams(root)),
                "levels" => ParseLevels(root),
                "ping" => new PingMessage(),
                _ => throw new ProtocolException($"unknown op '{op}'"),
            };
        }
    }

    private static HelloMessage ParseHello(JsonElement root)
    {
        var token = GetString(root, "token") ?? string.Empty;
        if (!root.TryGetProperty("protocol", out var protocolElement) || protocolElement.ValueKind != JsonValueKind.Number
            || !protocolElement.TryGetInt32(out var protocol))
        {
            throw new ProtocolException("hello requires a numeric protocol");
        }

        return new HelloMessage(token, protocol);
    }

    private static SetMessage ParseSet(JsonElement root)
    {
        var param = GetString(root, "param") ?? throw new ProtocolException("set requires param");
        if (!ParameterName.IsValid(param))
        {
            throw new ProtocolException($"invalid parameter name '{param}'");
        }

        if (!root.TryGetProperty("value", out var valueElement))
        {
            throw new ProtocolException("set requires value");
        }

        MixerValue value = valueElement.ValueKind switch
        {
            JsonValueKind.Number when valueElement.TryGetSingle(out var f) && float.IsFinite(f) => MixerValue.FromNumber(f),
            JsonValueKind.Number => throw new ProtocolException("value is not a finite number"),
            JsonValueKind.String => ParseText(valueElement.GetString()!),
            JsonValueKind.True => MixerValue.FromNumber(1f),
            JsonValueKind.False => MixerValue.FromNumber(0f),
            _ => throw new ProtocolException("value must be a number or a string"),
        };

        return new SetMessage(GetString(root, "id"), param, value);
    }

    private static MixerValue ParseText(string text)
    {
        if (text.Length > MaxTextValueLength)
        {
            throw new ProtocolException($"text value longer than {MaxTextValueLength} characters");
        }

        foreach (var ch in text)
        {
            if (ch < 0x20 || ch > 0xFF)
            {
                throw new ProtocolException("text value may contain printable Latin-1 characters only");
            }
        }

        return MixerValue.FromText(text);
    }

    private static LevelsMessage ParseLevels(JsonElement root)
    {
        var enabled = root.TryGetProperty("enabled", out var enabledElement) && enabledElement.ValueKind == JsonValueKind.True;
        var channels = new List<LevelChannel>();
        if (root.TryGetProperty("channels", out var channelsElement))
        {
            if (channelsElement.ValueKind != JsonValueKind.Array)
            {
                throw new ProtocolException("channels must be an array");
            }

            foreach (var item in channelsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("type", out var typeElement) || !typeElement.TryGetInt32(out var type)
                    || !item.TryGetProperty("channel", out var channelElement) || !channelElement.TryGetInt32(out var channel))
                {
                    throw new ProtocolException("each channel needs integer type and channel");
                }

                if (type < 0 || type > 3)
                {
                    throw new ProtocolException($"level type {type} out of range 0..3");
                }

                if (channel < 0 || channel > MaxLevelChannel)
                {
                    throw new ProtocolException($"level channel {channel} out of range 0..{MaxLevelChannel}");
                }

                channels.Add(new LevelChannel(type, channel));
            }
        }

        if (enabled && channels.Count == 0)
        {
            throw new ProtocolException("levels enabled requires at least one channel");
        }

        return new LevelsMessage(enabled, channels);
    }

    private static IReadOnlyList<string> GetParams(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var paramsElement) || paramsElement.ValueKind != JsonValueKind.Array)
        {
            throw new ProtocolException("params must be an array of names");
        }

        var names = new List<string>();
        foreach (var item in paramsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new ProtocolException("params must contain strings only");
            }

            var name = item.GetString()!;
            if (!ParameterName.IsValid(name))
            {
                throw new ProtocolException($"invalid parameter name '{name}'");
            }

            names.Add(name);
        }

        if (names.Count > MaxParamsPerFrame)
        {
            throw new ProtocolException($"too many params (max {MaxParamsPerFrame})");
        }

        return names;
    }

    private static string? GetString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element))
        {
            return null;
        }

        return element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : throw new ProtocolException($"{property} must be a string");
    }
}
