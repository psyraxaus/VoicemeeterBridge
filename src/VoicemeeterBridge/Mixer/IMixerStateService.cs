namespace VoicemeeterBridge.Mixer;

/// <summary>Raised when a watched parameter takes a new value.</summary>
/// <param name="Name">Parameter name as watched.</param>
/// <param name="Value">The new value.</param>
public sealed record ParameterChange(string Name, MixerValue Value);

/// <summary>
/// The single owner of the Voicemeeter connection. Every request is marshalled onto
/// the mixer thread; results come back as tasks. Events are raised on the mixer
/// thread, so handlers must return quickly and must not call back into this service
/// synchronously.
/// </summary>
public interface IMixerStateService
{
    /// <summary>Latest known connection status.</summary>
    VoicemeeterStatus Status { get; }

    /// <summary>Raised whenever <see cref="Status"/> changes.</summary>
    event EventHandler<VoicemeeterStatus>? StatusChanged;

    /// <summary>Raised when a watched parameter changes, from any source.</summary>
    event EventHandler<ParameterChange>? ParameterChanged;

    /// <summary>Raised on every level poll with readings for every subscribed meter.</summary>
    event EventHandler<IReadOnlyList<LevelReading>>? LevelsRead;

    /// <summary>
    /// Adds parameters to the watch list (reference counted across callers) and returns
    /// their current values.
    /// </summary>
    Task<IReadOnlyDictionary<string, ParameterReadResult>> WatchAsync(
        IReadOnlyCollection<string> parameters, CancellationToken cancellationToken);

    /// <summary>Releases one watch reference for each parameter.</summary>
    Task UnwatchAsync(IReadOnlyCollection<string> parameters, CancellationToken cancellationToken);

    /// <summary>Reads parameters directly from Voicemeeter without watching them.</summary>
    Task<IReadOnlyDictionary<string, ParameterReadResult>> GetAsync(
        IReadOnlyCollection<string> parameters, CancellationToken cancellationToken);

    /// <summary>
    /// Queues a write. Writes to the same parameter are coalesced (last value wins) and
    /// rate limited; the returned task completes when the final write has been sent.
    /// </summary>
    Task<SetResult> SetAsync(string parameter, MixerValue value, CancellationToken cancellationToken);

    /// <summary>Adds level meters to the poll set (reference counted across callers).</summary>
    Task SubscribeLevelsAsync(IReadOnlyCollection<LevelChannel> channels, CancellationToken cancellationToken);

    /// <summary>Releases one subscription reference for each meter.</summary>
    Task UnsubscribeLevelsAsync(IReadOnlyCollection<LevelChannel> channels, CancellationToken cancellationToken);
}
