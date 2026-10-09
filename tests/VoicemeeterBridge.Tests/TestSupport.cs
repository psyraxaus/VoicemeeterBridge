using Microsoft.Extensions.Options;
using Serilog;
using VoicemeeterBridge.Configuration;
using VoicemeeterBridge.Mixer;
using VoicemeeterBridge.Native;

namespace VoicemeeterBridge.Tests;

/// <summary>Shared helpers for the test suite.</summary>
internal static class TestSupport
{
    /// <summary>Fast intervals so tests finish quickly.</summary>
    public static BridgeOptions FastOptions() => new()
    {
        PollIntervalMs = 5,
        LevelPollIntervalMs = 5,
        ReconnectIntervalMs = 20,
        MaxSetsPerSecondPerParameter = 20,
        HelloTimeoutMs = 500,
        Token = "test-token",
    };

    /// <summary>Creates and starts a mixer service over the given fake.</summary>
    public static async Task<MixerStateService> StartMixerAsync(FakeVoicemeeterClient fake, BridgeOptions? options = null)
    {
        var service = new MixerStateService(fake, Options.Create(options ?? FastOptions()), Serilog.Core.Logger.None);
        await service.StartAsync(CancellationToken.None);
        return service;
    }

    /// <summary>Polls until <paramref name="condition"/> is true or the timeout elapses.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000, string? what = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what ?? "condition"}");
            }

            await Task.Delay(5);
        }
    }
}
