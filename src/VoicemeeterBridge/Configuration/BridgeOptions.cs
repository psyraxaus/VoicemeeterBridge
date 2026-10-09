namespace VoicemeeterBridge.Configuration;

/// <summary>
/// Settings bound from the <c>Bridge</c> section of <c>appsettings.json</c> and the
/// per-user override file under <c>%LOCALAPPDATA%\VoicemeeterBridge</c>.
/// </summary>
public sealed class BridgeOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Bridge";

    /// <summary>IP address Kestrel binds to. <c>0.0.0.0</c> listens on every interface.</summary>
    public string ListenAddress { get; set; } = "0.0.0.0";

    /// <summary>TCP port for the WebSocket endpoint.</summary>
    public int Port { get; set; } = 5088;

    /// <summary>
    /// Shared secret a client must present in its <c>hello</c> frame. Generated on first
    /// run and written to the override file when empty.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>How often the mixer thread polls <c>IsParametersDirty</c>.</summary>
    public int PollIntervalMs { get; set; } = 30;

    /// <summary>How often subscribed level meters are read.</summary>
    public int LevelPollIntervalMs { get; set; } = 50;

    /// <summary>How often a login is retried while Voicemeeter is not reachable.</summary>
    public int ReconnectIntervalMs { get; set; } = 2000;

    /// <summary>Upper bound on writes per second for any single parameter.</summary>
    public int MaxSetsPerSecondPerParameter { get; set; } = 20;

    /// <summary>How long a new socket may stay silent before it must have sent <c>hello</c>.</summary>
    public int HelloTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Optional explicit path to <c>VoicemeeterRemote64.dll</c>. Leave empty to resolve it
    /// from the registry.
    /// </summary>
    public string? DllPath { get; set; }
}
