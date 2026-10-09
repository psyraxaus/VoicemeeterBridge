using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Options;
using VoicemeeterBridge.Configuration;
using VoicemeeterBridge.Native;

namespace VoicemeeterBridge.Mixer;

/// <summary>
/// Background service that owns the <see cref="IVoicemeeterClient"/> on one dedicated
/// thread. It polls <c>IsParametersDirty</c>, diffs watched parameters against a cache,
/// publishes changes, coalesces incoming writes, and keeps retrying the login while
/// Voicemeeter is closed or restarting.
/// </summary>
public sealed class MixerStateService : BackgroundService, IMixerStateService
{
    private enum ParameterKind
    {
        Unknown,
        Number,
        Text,
    }

    private sealed class PendingSet
    {
        public MixerValue Value;
        public readonly List<TaskCompletionSource<SetResult>> Waiters = new();
    }

    private readonly IVoicemeeterClient _client;
    private readonly BridgeOptions _options;
    private readonly ILogger _log;
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // Everything below is touched only from the mixer thread.
    private readonly Dictionary<string, int> _watchRefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MixerValue> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParameterKind> _kinds = new(StringComparer.Ordinal);
    private readonly Dictionary<LevelChannel, int> _levelRefs = new();
    private readonly Dictionary<string, PendingSet> _pendingSets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastSetAtMs = new(StringComparer.Ordinal);
    private bool _loggedIn;
    private bool _connected;
    private long _lastLoginAttemptMs = long.MinValue;
    private long _lastLevelPollMs = long.MinValue;

    private volatile VoicemeeterStatus _status = VoicemeeterStatus.Disconnected;

    /// <summary>
    /// Creates the service.
    /// </summary>
    /// <param name="client">The Voicemeeter client; this service becomes its only user.</param>
    /// <param name="options">Bridge settings (poll intervals, reconnect interval, rate limit).</param>
    /// <param name="log">Serilog logger.</param>
    public MixerStateService(IVoicemeeterClient client, IOptions<BridgeOptions> options, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        _client = client;
        _options = options.Value;
        _log = log.ForContext<MixerStateService>();
    }

    /// <inheritdoc />
    public VoicemeeterStatus Status => _status;

    /// <inheritdoc />
    public event EventHandler<VoicemeeterStatus>? StatusChanged;

    /// <inheritdoc />
    public event EventHandler<ParameterChange>? ParameterChanged;

    /// <inheritdoc />
    public event EventHandler<IReadOnlyList<LevelReading>>? LevelsRead;

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => RunLoop(stoppingToken, finished))
        {
            Name = "VoicemeeterMixer",
            IsBackground = true,
        };
        thread.Start();
        return finished.Task;
    }

    private void RunLoop(CancellationToken stoppingToken, TaskCompletionSource finished)
    {
        using var registration = stoppingToken.Register(() => _wake.Set());
        _log.Information("Mixer thread started (poll {PollMs} ms, levels {LevelMs} ms, reconnect {ReconnectMs} ms)",
            _options.PollIntervalMs, _options.LevelPollIntervalMs, _options.ReconnectIntervalMs);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    DrainCommands();
                    Tick();
                    FlushPendingSets();
                }
                catch (Exception ex)
                {
                    _log.Error(ex, "Unhandled error on mixer thread; continuing");
                }

                _wake.WaitOne(Math.Max(1, _options.PollIntervalMs));
            }
        }
        finally
        {
            Shutdown();
            finished.TrySetResult();
        }
    }

    private void Shutdown()
    {
        FailAllPending("bridge shutting down");
        if (_loggedIn)
        {
            _client.Logout();
            _loggedIn = false;
        }

        _connected = false;
        PublishStatus(VoicemeeterStatus.Disconnected);
        _log.Information("Mixer thread stopped");
    }

    private long NowMs => _clock.ElapsedMilliseconds;

    // ---- Commands from other threads -------------------------------------------------

    private Task<T> Enqueue<T>(Func<T> work, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Enqueue(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                tcs.TrySetResult(work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });
        _wake.Set();
        return tcs.Task;
    }

    private void DrainCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            command();
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, ParameterReadResult>> WatchAsync(
        IReadOnlyCollection<string> parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return Enqueue<IReadOnlyDictionary<string, ParameterReadResult>>(() =>
        {
            var snapshot = new Dictionary<string, ParameterReadResult>(StringComparer.Ordinal);
            foreach (var name in parameters)
            {
                _watchRefs[name] = _watchRefs.GetValueOrDefault(name) + 1;
                snapshot[name] = ReadIntoCache(name);
            }

            return snapshot;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task UnwatchAsync(IReadOnlyCollection<string> parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return Enqueue(() =>
        {
            foreach (var name in parameters)
            {
                if (!_watchRefs.TryGetValue(name, out var refs))
                {
                    continue;
                }

                if (refs <= 1)
                {
                    _watchRefs.Remove(name);
                    _cache.Remove(name);
                }
                else
                {
                    _watchRefs[name] = refs - 1;
                }
            }

            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<string, ParameterReadResult>> GetAsync(
        IReadOnlyCollection<string> parameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return Enqueue<IReadOnlyDictionary<string, ParameterReadResult>>(() =>
        {
            var values = new Dictionary<string, ParameterReadResult>(StringComparer.Ordinal);
            foreach (var name in parameters)
            {
                values[name] = ReadParameter(name);
            }

            return values;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SetResult> SetAsync(string parameter, MixerValue value, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameter);
        var tcs = new TaskCompletionSource<SetResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Enqueue(() =>
        {
            if (cancellationToken.IsCancellationRequested)
            {
                tcs.TrySetCanceled(cancellationToken);
                return;
            }

            if (!_connected)
            {
                tcs.TrySetResult(SetResult.Failure(VoicemeeterResult.Describe(VoicemeeterResult.NoServer)));
                return;
            }

            if (!_pendingSets.TryGetValue(parameter, out var pending))
            {
                pending = new PendingSet();
                _pendingSets[parameter] = pending;
            }

            pending.Value = value;
            pending.Waiters.Add(tcs);
        });
        _wake.Set();
        return tcs.Task;
    }

    /// <inheritdoc />
    public Task SubscribeLevelsAsync(IReadOnlyCollection<LevelChannel> channels, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channels);
        return Enqueue(() =>
        {
            foreach (var channel in channels)
            {
                _levelRefs[channel] = _levelRefs.GetValueOrDefault(channel) + 1;
            }

            return true;
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task UnsubscribeLevelsAsync(IReadOnlyCollection<LevelChannel> channels, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channels);
        return Enqueue(() =>
        {
            foreach (var channel in channels)
            {
                if (!_levelRefs.TryGetValue(channel, out var refs))
                {
                    continue;
                }

                if (refs <= 1)
                {
                    _levelRefs.Remove(channel);
                }
                else
                {
                    _levelRefs[channel] = refs - 1;
                }
            }

            return true;
        }, cancellationToken);
    }

    // ---- Poll loop ---------------------------------------------------------------------

    private void Tick()
    {
        if (!_loggedIn)
        {
            TryLogin();
            if (!_loggedIn)
            {
                return;
            }
        }

        var dirty = _client.IsParametersDirty();
        switch (dirty)
        {
            case DirtyResult.Dirty:
                if (!_connected)
                {
                    OnConnected();
                }

                RefreshWatched();
                break;

            case DirtyResult.Clean:
                if (!_connected)
                {
                    OnConnected();
                    RefreshWatched();
                }

                break;

            case DirtyResult.NoServer:
                if (_connected)
                {
                    _log.Warning("Voicemeeter stopped answering; waiting for it to come back");
                    OnDisconnected();
                }

                break;

            default:
                _log.Warning("IsParametersDirty returned {Code}; dropping the login and retrying", (int)dirty);
                OnDisconnected();
                _client.Logout();
                _loggedIn = false;
                break;
        }

        if (_connected)
        {
            PollLevels();
        }
    }

    private void TryLogin()
    {
        var now = NowMs;
        if (_lastLoginAttemptMs != long.MinValue && now - _lastLoginAttemptMs < _options.ReconnectIntervalMs)
        {
            return;
        }

        _lastLoginAttemptMs = now;
        var result = _client.Login();
        switch (result)
        {
            case LoginResult.Ok:
            case LoginResult.OkVoicemeeterNotRunning:
            case LoginResult.UnexpectedLogin:
                _loggedIn = true;
                _log.Information("Logged in to Voicemeeter Remote API ({Result})", result);
                break;

            default:
                _log.Warning("Voicemeeter login failed ({Result}); retrying in {ReconnectMs} ms", result, _options.ReconnectIntervalMs);
                break;
        }
    }

    private void OnConnected()
    {
        _client.GetVoicemeeterType(out var kind);
        _client.GetVoicemeeterVersion(out var version);
        _connected = true;
        _kinds.Clear();
        _log.Information("Voicemeeter connected: {Kind} {Version}", kind, version);
        PublishStatus(new VoicemeeterStatus(true, kind, version));
    }

    private void OnDisconnected()
    {
        FailAllPending(VoicemeeterResult.Describe(VoicemeeterResult.NoServer));
        _connected = false;
        PublishStatus(VoicemeeterStatus.Disconnected);
    }

    private void PublishStatus(VoicemeeterStatus status)
    {
        if (_status == status)
        {
            return;
        }

        _status = status;
        RaiseSafely(() => StatusChanged?.Invoke(this, status));
    }

    private void RefreshWatched()
    {
        if (_watchRefs.Count == 0)
        {
            return;
        }

        foreach (var name in _watchRefs.Keys.ToArray())
        {
            var read = ReadParameter(name);
            if (!read.Ok)
            {
                if (read.Error == VoicemeeterResult.Describe(VoicemeeterResult.NoServer))
                {
                    OnDisconnected();
                    return;
                }

                continue;
            }

            var value = read.Value!.Value;
            if (_cache.TryGetValue(name, out var previous) && previous == value)
            {
                continue;
            }

            _cache[name] = value;
            var change = new ParameterChange(name, value);
            RaiseSafely(() => ParameterChanged?.Invoke(this, change));
        }
    }

    private ParameterReadResult ReadIntoCache(string name)
    {
        var read = ReadParameter(name);
        if (read.Ok)
        {
            _cache[name] = read.Value!.Value;
        }

        return read;
    }

    private ParameterReadResult ReadParameter(string name)
    {
        if (!_connected)
        {
            return ParameterReadResult.Failure(VoicemeeterResult.Describe(VoicemeeterResult.NoServer));
        }

        var kind = _kinds.GetValueOrDefault(name, ParameterKind.Unknown);
        if (kind != ParameterKind.Text)
        {
            var code = _client.GetParameterFloat(name, out var number);
            if (code == VoicemeeterResult.Ok)
            {
                _kinds[name] = ParameterKind.Number;
                return ParameterReadResult.Success(MixerValue.FromNumber(number));
            }

            // The DLL answers -1 for a float read of a text parameter; anything else is a real failure.
            if (code != VoicemeeterResult.Error || kind == ParameterKind.Number)
            {
                return ParameterReadResult.Failure(VoicemeeterResult.Describe(code));
            }
        }

        var textCode = _client.GetParameterString(name, out var text);
        if (textCode == VoicemeeterResult.Ok)
        {
            _kinds[name] = ParameterKind.Text;
            return ParameterReadResult.Success(MixerValue.FromText(text));
        }

        return ParameterReadResult.Failure(VoicemeeterResult.Describe(textCode));
    }

    private void FlushPendingSets()
    {
        if (_pendingSets.Count == 0)
        {
            return;
        }

        var minIntervalMs = _options.MaxSetsPerSecondPerParameter <= 0
            ? 0
            : 1000 / _options.MaxSetsPerSecondPerParameter;
        var now = NowMs;

        foreach (var (name, pending) in _pendingSets.ToArray())
        {
            if (_lastSetAtMs.TryGetValue(name, out var last) && now - last < minIntervalMs)
            {
                continue;
            }

            _pendingSets.Remove(name);
            _lastSetAtMs[name] = now;

            SetResult result;
            if (!_connected)
            {
                result = SetResult.Failure(VoicemeeterResult.Describe(VoicemeeterResult.NoServer));
            }
            else
            {
                var code = pending.Value.IsText
                    ? _client.SetParameterString(name, pending.Value.Text!)
                    : _client.SetParameterFloat(name, pending.Value.Number!.Value);
                result = code == VoicemeeterResult.Ok
                    ? SetResult.Success
                    : SetResult.Failure(VoicemeeterResult.Describe(code));
                if (!result.Ok)
                {
                    _log.Debug("Set {Parameter} = {Value} failed: {Error}", name, pending.Value, result.Error);
                }
            }

            foreach (var waiter in pending.Waiters)
            {
                waiter.TrySetResult(result);
            }
        }
    }

    private void FailAllPending(string error)
    {
        if (_pendingSets.Count == 0)
        {
            return;
        }

        var failure = SetResult.Failure(error);
        foreach (var pending in _pendingSets.Values)
        {
            foreach (var waiter in pending.Waiters)
            {
                waiter.TrySetResult(failure);
            }
        }

        _pendingSets.Clear();
    }

    private void PollLevels()
    {
        if (_levelRefs.Count == 0)
        {
            return;
        }

        var now = NowMs;
        if (_lastLevelPollMs != long.MinValue && now - _lastLevelPollMs < _options.LevelPollIntervalMs)
        {
            return;
        }

        _lastLevelPollMs = now;
        var readings = new List<LevelReading>(_levelRefs.Count);
        foreach (var channel in _levelRefs.Keys)
        {
            var code = _client.GetLevel((LevelType)channel.Type, channel.Channel, out var linear);
            if (code == VoicemeeterResult.Ok)
            {
                readings.Add(new LevelReading(channel, LevelReading.LinearToDb(linear)));
            }
        }

        if (readings.Count > 0)
        {
            RaiseSafely(() => LevelsRead?.Invoke(this, readings));
        }
    }

    private void RaiseSafely(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Event handler threw on the mixer thread");
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        base.Dispose();
        _wake.Dispose();
    }
}
