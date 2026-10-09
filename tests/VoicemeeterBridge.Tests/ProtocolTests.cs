using System.Text.Json;
using VoicemeeterBridge.Mixer;
using VoicemeeterBridge.Native;
using VoicemeeterBridge.Protocol;

namespace VoicemeeterBridge.Tests;

public class ParameterNameTests
{
    [Theory]
    [InlineData("Strip[0].Gain")]
    [InlineData("Bus[7].Mute")]
    [InlineData("Strip[0].Label")]
    [InlineData("Strip[0].device.name")]
    [InlineData("Strip[1].EQ.channel[0].cell[3].gain")]
    [InlineData("Option.sr")]
    [InlineData("Recorder.load")]
    [InlineData("vban.instream[0].on")]
    [InlineData("Command.Restart")]
    [InlineData("Strip[123].A1")]
    public void Accepts_well_formed_names(string name) => Assert.True(ParameterName.IsValid(name));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Strip[0]..Gain")]
    [InlineData("Strip[0].Gain ")]
    [InlineData(" Strip[0].Gain")]
    [InlineData("Strip[0].Gain;")]
    [InlineData("Strip[0].Gain=1")]
    [InlineData("Strip[-1].Gain")]
    [InlineData("Strip[1234].Gain")]
    [InlineData("Strip[0].")]
    [InlineData(".Gain")]
    [InlineData("Strip[0]Gain")]
    [InlineData("Strip[a].Gain")]
    [InlineData("Strip[0].Gain\n")]
    [InlineData("Strip[0].Gain,Strip[1].Gain")]
    [InlineData("0Strip.Gain")]
    public void Rejects_malformed_names(string? name) => Assert.False(ParameterName.IsValid(name));

    [Fact]
    public void Rejects_over_long_names() =>
        Assert.False(ParameterName.IsValid("A." + new string('b', ParameterName.MaxLength)));
}

public class ClientMessageParserTests
{
    [Fact]
    public void Parses_hello()
    {
        var message = Assert.IsType<HelloMessage>(ClientMessageParser.Parse("{\"op\":\"hello\",\"token\":\"abc\",\"protocol\":1}"));
        Assert.Equal("abc", message.Token);
        Assert.Equal(1, message.Protocol);
    }

    [Fact]
    public void Hello_without_protocol_is_rejected() =>
        Assert.Throws<ProtocolException>(() => ClientMessageParser.Parse("{\"op\":\"hello\",\"token\":\"abc\"}"));

    [Fact]
    public void Parses_watch_and_unwatch()
    {
        var watch = Assert.IsType<WatchMessage>(ClientMessageParser.Parse("{\"op\":\"watch\",\"params\":[\"Strip[0].Gain\",\"Bus[1].Mute\"]}"));
        Assert.Equal(["Strip[0].Gain", "Bus[1].Mute"], watch.Params);

        var unwatch = Assert.IsType<UnwatchMessage>(ClientMessageParser.Parse("{\"op\":\"unwatch\",\"params\":[\"Strip[0].Gain\"]}"));
        Assert.Equal(["Strip[0].Gain"], unwatch.Params);
    }

    [Fact]
    public void Watch_with_bad_name_is_rejected()
    {
        var ex = Assert.Throws<ProtocolException>(() => ClientMessageParser.Parse("{\"op\":\"watch\",\"params\":[\"Strip[0].Gain\",\"bad name\"]}"));
        Assert.Contains("bad name", ex.Message);
    }

    [Fact]
    public void Watch_without_params_array_is_rejected() =>
        Assert.Throws<ProtocolException>(() => ClientMessageParser.Parse("{\"op\":\"watch\",\"params\":\"Strip[0].Gain\"}"));

    [Fact]
    public void Parses_numeric_set()
    {
        var set = Assert.IsType<SetMessage>(ClientMessageParser.Parse("{\"op\":\"set\",\"id\":\"abc\",\"param\":\"Strip[0].Gain\",\"value\":-6.0}"));
        Assert.Equal("abc", set.Id);
        Assert.Equal("Strip[0].Gain", set.Param);
        Assert.Equal(MixerValue.FromNumber(-6f), set.Value);
    }

    [Fact]
    public void Parses_string_set_and_booleans()
    {
        var text = Assert.IsType<SetMessage>(ClientMessageParser.Parse("{\"op\":\"set\",\"param\":\"Strip[0].Label\",\"value\":\"Mic\"}"));
        Assert.Null(text.Id);
        Assert.Equal("Mic", text.Value.Text);

        var flag = Assert.IsType<SetMessage>(ClientMessageParser.Parse("{\"op\":\"set\",\"param\":\"Strip[0].Mute\",\"value\":true}"));
        Assert.Equal(1f, flag.Value.Number);
    }

    [Theory]
    [InlineData("{\"op\":\"set\",\"param\":\"Strip[0].Gain\"}")]
    [InlineData("{\"op\":\"set\",\"value\":1}")]
    [InlineData("{\"op\":\"set\",\"param\":\"Strip[0].Gain\",\"value\":null}")]
    [InlineData("{\"op\":\"set\",\"param\":\"Strip[0].Gain\",\"value\":[1]}")]
    [InlineData("{\"op\":\"set\",\"param\":\"Strip[0];Gain\",\"value\":1}")]
    [InlineData("{\"op\":\"set\",\"param\":\"Strip[0].Label\",\"value\":\"line\\nbreak\"}")]
    public void Malformed_set_is_rejected(string json) =>
        Assert.Throws<ProtocolException>(() => ClientMessageParser.Parse(json));

    [Fact]
    public void Parses_get()
    {
        var get = Assert.IsType<GetMessage>(ClientMessageParser.Parse("{\"op\":\"get\",\"id\":\"q1\",\"params\":[\"Bus[0].Gain\"]}"));
        Assert.Equal("q1", get.Id);
        Assert.Equal(["Bus[0].Gain"], get.Params);
    }

    [Fact]
    public void Parses_levels()
    {
        var on = Assert.IsType<LevelsMessage>(ClientMessageParser.Parse("{\"op\":\"levels\",\"enabled\":true,\"channels\":[{\"type\":3,\"channel\":0},{\"type\":0,\"channel\":2}]}"));
        Assert.True(on.Enabled);
        Assert.Equal([new LevelChannel(3, 0), new LevelChannel(0, 2)], on.Channels);

        var off = Assert.IsType<LevelsMessage>(ClientMessageParser.Parse("{\"op\":\"levels\",\"enabled\":false}"));
        Assert.False(off.Enabled);
        Assert.Empty(off.Channels);
    }

    [Theory]
    [InlineData("{\"op\":\"levels\",\"enabled\":true}")]
    [InlineData("{\"op\":\"levels\",\"enabled\":true,\"channels\":[{\"type\":4,\"channel\":0}]}")]
    [InlineData("{\"op\":\"levels\",\"enabled\":true,\"channels\":[{\"type\":3,\"channel\":-1}]}")]
    [InlineData("{\"op\":\"levels\",\"enabled\":true,\"channels\":[{\"type\":3}]}")]
    public void Malformed_levels_is_rejected(string json) =>
        Assert.Throws<ProtocolException>(() => ClientMessageParser.Parse(json));

    [Fact]
    public void Parses_ping() => Assert.IsType<PingMessage>(ClientMessageParser.Parse("{\"op\":\"ping\"}"));

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"op\":\"dance\"}")]
    [InlineData("{\"op\":42}")]
    public void Garbage_is_rejected(string json) =>
        Assert.Throws<ProtocolException>(() => ClientMessageParser.Parse(json));
}

public class ServerMessagesTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Welcome_carries_status()
    {
        var json = Parse(ServerMessages.Welcome(new VoicemeeterStatus(true, VoicemeeterKind.Potato, "3.1.1.2")));
        Assert.Equal("welcome", json.GetProperty("op").GetString());
        Assert.Equal(1, json.GetProperty("protocol").GetInt32());
        Assert.True(json.GetProperty("voicemeeter").GetProperty("connected").GetBoolean());
        Assert.Equal("potato", json.GetProperty("voicemeeter").GetProperty("kind").GetString());
        Assert.Equal("3.1.1.2", json.GetProperty("voicemeeter").GetProperty("version").GetString());
    }

    [Fact]
    public void Disconnected_status_has_no_kind_or_version()
    {
        var json = Parse(ServerMessages.Status(VoicemeeterStatus.Disconnected));
        var vm = json.GetProperty("voicemeeter");
        Assert.False(vm.GetProperty("connected").GetBoolean());
        Assert.False(vm.TryGetProperty("kind", out _));
    }

    [Fact]
    public void Snapshot_splits_values_and_errors()
    {
        var json = Parse(ServerMessages.Snapshot(new Dictionary<string, ParameterReadResult>
        {
            ["Strip[0].Gain"] = ParameterReadResult.Success(MixerValue.FromNumber(-6f)),
            ["Strip[0].Label"] = ParameterReadResult.Success(MixerValue.FromText("Mic")),
            ["Nope"] = ParameterReadResult.Failure("unknown parameter"),
        }));
        Assert.Equal(-6f, json.GetProperty("values").GetProperty("Strip[0].Gain").GetSingle());
        Assert.Equal("Mic", json.GetProperty("values").GetProperty("Strip[0].Label").GetString());
        Assert.Equal("unknown parameter", json.GetProperty("errors").GetProperty("Nope").GetString());
    }

    [Fact]
    public void Snapshot_omits_errors_when_there_are_none()
    {
        var json = Parse(ServerMessages.Snapshot(new Dictionary<string, ParameterReadResult>
        {
            ["Strip[0].Gain"] = ParameterReadResult.Success(MixerValue.FromNumber(0f)),
        }));
        Assert.False(json.TryGetProperty("errors", out _));
    }

    [Fact]
    public void Result_and_levels_shapes()
    {
        var ok = Parse(ServerMessages.Result("abc", true));
        Assert.Equal("abc", ok.GetProperty("id").GetString());
        Assert.True(ok.GetProperty("ok").GetBoolean());
        Assert.False(ok.TryGetProperty("error", out _));

        var bad = Parse(ServerMessages.Result("abc", false, "unknown parameter"));
        Assert.False(bad.GetProperty("ok").GetBoolean());
        Assert.Equal("unknown parameter", bad.GetProperty("error").GetString());

        var levels = Parse(ServerMessages.Levels([new LevelReading(new LevelChannel(3, 0), -18.24f)]));
        var first = levels.GetProperty("values")[0];
        Assert.Equal(3, first.GetProperty("type").GetInt32());
        Assert.Equal(0, first.GetProperty("channel").GetInt32());
        Assert.Equal(-18.2f, first.GetProperty("db").GetSingle(), 1);
    }
}
