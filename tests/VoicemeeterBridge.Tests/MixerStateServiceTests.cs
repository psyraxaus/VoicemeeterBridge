using System.Collections.Concurrent;
using VoicemeeterBridge.Mixer;
using VoicemeeterBridge.Native;

namespace VoicemeeterBridge.Tests;

public class MixerStateServiceTests
{
    [Fact]
    public async Task Watch_returns_snapshot_and_publishes_only_real_changes()
    {
        var fake = new FakeVoicemeeterClient();
        fake.SimulateExternalChange("Strip[0].Gain", -6f);
        fake.SimulateExternalChange("Strip[0].Label", "Mic");
        using var mixer = await TestSupport.StartMixerAsync(fake);
        await TestSupport.WaitUntilAsync(() => mixer.Status.Connected, what: "connect");

        var changes = new ConcurrentQueue<ParameterChange>();
        mixer.ParameterChanged += (_, c) => changes.Enqueue(c);

        var snapshot = await mixer.WatchAsync(["Strip[0].Gain", "Strip[0].Label", "Strip[9].Gain"], CancellationToken.None);

        Assert.Equal(-6f, snapshot["Strip[0].Gain"].Value!.Value.Number);
        Assert.Equal("Mic", snapshot["Strip[0].Label"].Value!.Value.Text);
        Assert.False(snapshot["Strip[9].Gain"].Ok);
        Assert.Equal("unknown parameter", snapshot["Strip[9].Gain"].Error);

        // Dirty, but same value: no event.
        fake.SimulateExternalChange("Strip[0].Gain", -6f);
        await Task.Delay(50);
        Assert.Empty(changes);

        // Real change: one event with the new value.
        fake.SimulateExternalChange("Strip[0].Gain", -12f);
        await TestSupport.WaitUntilAsync(() => changes.Count >= 1, what: "change event");
        Assert.True(changes.TryDequeue(out var change));
        Assert.Equal("Strip[0].Gain", change!.Name);
        Assert.Equal(-12f, change.Value.Number);

        // Text change too.
        fake.SimulateExternalChange("Strip[0].Label", "Headset");
        await TestSupport.WaitUntilAsync(() => changes.Count >= 1, what: "label change");
        Assert.True(changes.TryDequeue(out change));
        Assert.Equal("Headset", change!.Value.Text);

        await mixer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Unwatched_parameters_stop_publishing()
    {
        var fake = new FakeVoicemeeterClient();
        fake.SimulateExternalChange("Bus[1].Mute", 0f);
        using var mixer = await TestSupport.StartMixerAsync(fake);
        await TestSupport.WaitUntilAsync(() => mixer.Status.Connected);

        var changes = new ConcurrentQueue<ParameterChange>();
        mixer.ParameterChanged += (_, c) => changes.Enqueue(c);

        await mixer.WatchAsync(["Bus[1].Mute"], CancellationToken.None);
        await mixer.UnwatchAsync(["Bus[1].Mute"], CancellationToken.None);
        fake.SimulateExternalChange("Bus[1].Mute", 1f);
        await Task.Delay(50);

        Assert.Empty(changes);
        await mixer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Rapid_sets_are_coalesced_and_last_value_wins()
    {
        var fake = new FakeVoicemeeterClient();
        fake.SimulateExternalChange("Strip[0].Gain", 0f);
        using var mixer = await TestSupport.StartMixerAsync(fake);
        await TestSupport.WaitUntilAsync(() => mixer.Status.Connected);

        var tasks = new List<Task<SetResult>>();
        for (int i = 1; i <= 20; i++)
        {
            tasks.Add(mixer.SetAsync("Strip[0].Gain", MixerValue.FromNumber(-i), CancellationToken.None));
        }

        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.All(results, r => Assert.True(r.Ok));
        Assert.Equal(-20f, fake.PeekFloat("Strip[0].Gain"));
        Assert.InRange(fake.SetCallCount, 1, 3);
        await mixer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Sets_to_different_parameters_are_not_coalesced_together()
    {
        var fake = new FakeVoicemeeterClient();
        using var mixer = await TestSupport.StartMixerAsync(fake);
        await TestSupport.WaitUntilAsync(() => mixer.Status.Connected);

        var a = mixer.SetAsync("Strip[0].Gain", MixerValue.FromNumber(-1f), CancellationToken.None);
        var b = mixer.SetAsync("Strip[1].Gain", MixerValue.FromNumber(-2f), CancellationToken.None);
        var c = mixer.SetAsync("Strip[0].Label", MixerValue.FromText("Mic"), CancellationToken.None);
        await Task.WhenAll(a, b, c).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(-1f, fake.PeekFloat("Strip[0].Gain"));
        Assert.Equal(-2f, fake.PeekFloat("Strip[1].Gain"));
        Assert.Equal("Mic", fake.PeekString("Strip[0].Label"));
        Assert.Equal(3, fake.SetCallCount);
        await mixer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Set_fails_while_voicemeeter_is_not_running()
    {
        var fake = new FakeVoicemeeterClient();
        fake.SimulateVoicemeeterStopped();
        using var mixer = await TestSupport.StartMixerAsync(fake);
        await TestSupport.WaitUntilAsync(() => fake.IsLoggedIn);

        var result = await mixer.SetAsync("Strip[0].Gain", MixerValue.FromNumber(1f), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal("voicemeeter not running", result.Error);
        await mixer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Reconnects_when_voicemeeter_starts_later_and_rereads_watched_values()
    {
        var fake = new FakeVoicemeeterClient();
        fake.SimulateVoicemeeterStopped();
        using var mixer = await TestSupport.StartMixerAsync(fake);

        var statuses = new ConcurrentQueue<VoicemeeterStatus>();
        mixer.StatusChanged += (_, s) => statuses.Enqueue(s);
        var changes = new ConcurrentQueue<ParameterChange>();
        mixer.ParameterChanged += (_, c) => changes.Enqueue(c);

        await TestSupport.WaitUntilAsync(() => fake.IsLoggedIn);
        Assert.False(mixer.Status.Connected);

        var snapshot = await mixer.WatchAsync(["Strip[0].Gain"], CancellationToken.None);
        Assert.False(snapshot["Strip[0].Gain"].Ok);

        fake.SimulateExternalChange("Strip[0].Gain", -3f);
        fake.SimulateVoicemeeterStarted();

        await TestSupport.WaitUntilAsync(() => mixer.Status.Connected, what: "connect");
        Assert.Equal(VoicemeeterKind.Potato, mixer.Status.Kind);
        Assert.Equal("3.1.1.2", mixer.Status.Version);
        await TestSupport.WaitUntilAsync(() => changes.Count >= 1, what: "re-read after connect");
        Assert.True(changes.TryDequeue(out var change));
        Assert.Equal(-3f, change!.Value.Number);

        await mixer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Publishes_disconnected_when_voicemeeter_stops_and_recovers_after_restart()
    {
        var fake = new FakeVoicemeeterClient();
        fake.SimulateExternalChange("Strip[0].Gain", 0f);
        using var mixer = await TestSupport.StartMixerAsync(fake);
        await TestSupport.WaitUntilAsync(() => mixer.Status.Connected);
        await mixer.WatchAsync(["Strip[0].Gain"], CancellationToken.None);

        var statuses = new ConcurrentQueue<VoicemeeterStatus>();
        mixer.StatusChanged += (_, s) => statuses.Enqueue(s);
        var changes = new ConcurrentQueue<ParameterChange>();
        mixer.ParameterChanged += (_, c) => changes.Enqueue(c);

        fake.SimulateVoicemeeterStopped();
        await TestSupport.WaitUntilAsync(() => !mixer.Status.Connected, what: "disconnect");
        Assert.True(statuses.TryDequeue(out var s1));
        Assert.False(s1!.Connected);

        // Value changes while the engine is "down"; the bridge must publish it after reconnect.
        fake.SimulateExternalChange("Strip[0].Gain", -9f);
        fake.SimulateVoicemeeterStarted();
        await TestSupport.WaitUntilAsync(() => mixer.Status.Connected, what: "reconnect");
        await TestSupport.WaitUntilAsync(() => changes.Count >= 1, what: "value after reconnect");
        Assert.True(changes.TryDequeue(out var change));
        Assert.Equal(-9f, change!.Value.Number);

        // The login was kept, so no second login was needed.
        Assert.Equal(1, fake.LoginCallCount);
        await mixer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Get_reads_live_values_and_reports_unknown_parameters()
    {
        var fake = new FakeVoicemeeterClient();
        fake.SimulateExternalChange("Bus[0].Gain", -1.5f);
        fake.SimulateExternalChange("Bus[0].Label", "Main");
        using var mixer = await TestSupport.StartMixerAsync(fake);
        await TestSupport.WaitUntilAsync(() => mixer.Status.Connected);

        var values = await mixer.GetAsync(["Bus[0].Gain", "Bus[0].Label", "Nope"], CancellationToken.None);

        Assert.Equal(-1.5f, values["Bus[0].Gain"].Value!.Value.Number);
        Assert.Equal("Main", values["Bus[0].Label"].Value!.Value.Text);
        Assert.Equal("unknown parameter", values["Nope"].Error);
        await mixer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Levels_are_polled_for_subscribed_channels_and_reported_in_db()
    {
        var fake = new FakeVoicemeeterClient();
        fake.SetLevel(LevelType.Output, 0, 0.5f);
        fake.SetLevel(LevelType.Output, 1, 0f);
        using var mixer = await TestSupport.StartMixerAsync(fake);
        await TestSupport.WaitUntilAsync(() => mixer.Status.Connected);

        var batches = new ConcurrentQueue<IReadOnlyList<LevelReading>>();
        mixer.LevelsRead += (_, l) => batches.Enqueue(l);

        await Task.Delay(30);
        Assert.Empty(batches);

        await mixer.SubscribeLevelsAsync([new LevelChannel(3, 0), new LevelChannel(3, 1)], CancellationToken.None);
        await TestSupport.WaitUntilAsync(() => batches.Count >= 1, what: "levels");

        Assert.True(batches.TryDequeue(out var batch));
        var half = batch!.Single(r => r.Channel.Channel == 0);
        var silent = batch.Single(r => r.Channel.Channel == 1);
        Assert.Equal(-6.02f, half.Db, 2);
        Assert.Equal(LevelReading.MinDb, silent.Db);

        await mixer.UnsubscribeLevelsAsync([new LevelChannel(3, 0), new LevelChannel(3, 1)], CancellationToken.None);
        batches.Clear();
        await Task.Delay(30);
        Assert.Empty(batches);

        await mixer.StopAsync(CancellationToken.None);
    }
}
