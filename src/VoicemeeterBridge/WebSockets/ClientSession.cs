using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using VoicemeeterBridge.Configuration;
using VoicemeeterBridge.Mixer;
using VoicemeeterBridge.Protocol;

namespace VoicemeeterBridge.WebSockets;

/// <summary>
/// One connected client. Owns the socket for its lifetime: a receive loop parses and
/// dispatches frames, a send loop drains an outbound queue, and mixer events are filtered
/// through this session's own watch list and level subscriptions.
/// </summary>
public sealed class ClientSession
{
    private const int MaxFrameBytes = 64 * 1024;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private readonly WebSocket _socket;
    private readonly IMixerStateService _mixer;
    private readonly SessionRegistry _registry;
    private readonly BridgeOptions _options;
    private readonly ILogger _log;
    private readonly Channel<string> _outbound = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly object _gate = new();
    private readonly HashSet<string> _watched = new(StringComparer.Ordinal);
    private readonly HashSet<LevelChannel> _levels = new();
    private bool _authenticated;

    /// <summary>
    /// Creates a session over an accepted socket.
    /// </summary>
    /// <param name="socket">The accepted WebSocket.</param>
    /// <param name="mixer">Mixer service shared by all sessions.</param>
    /// <param name="registry">Counter of authenticated sessions.</param>
    /// <param name="options">Bridge settings (token, hello timeout).</param>
    /// <param name="log">Serilog logger.</param>
    /// <param name="remote">Remote address for log lines.</param>
    public ClientSession(
        WebSocket socket,
        IMixerStateService mixer,
        SessionRegistry registry,
        BridgeOptions options,
        ILogger log,
        string remote)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        _socket = socket;
        _mixer = mixer;
        _registry = registry;
        _options = options;
        _log = log.ForContext<ClientSession>().ForContext("Remote", remote);
    }

    /// <summary>
    /// Runs the session until the client disconnects, the server stops, or a protocol
    /// violation closes the socket.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sendLoop = SendLoopAsync(cts.Token);
        WebSocketCloseStatus? closeStatus = null;
        string? closeReason = null;

        try
        {
            var (ok, status, reason) = await AuthenticateAsync(cts.Token);
            if (!ok)
            {
                closeStatus = status;
                closeReason = reason;
                return;
            }

            while (!cts.IsCancellationRequested)
            {
                var text = await ReceiveTextAsync(cts.Token);
                if (text is null)
                {
                    closeStatus = WebSocketCloseStatus.NormalClosure;
                    closeReason = "bye";
                    return;
                }

                await DispatchAsync(text, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            closeStatus = WebSocketCloseStatus.EndpointUnavailable;
            closeReason = "server shutting down";
        }
        catch (FrameTooLargeException)
        {
            closeStatus = WebSocketCloseStatus.MessageTooBig;
            closeReason = "frame too large";
        }
        catch (BinaryFrameException)
        {
            closeStatus = WebSocketCloseStatus.InvalidMessageType;
            closeReason = "text frames only";
        }
        catch (WebSocketException ex)
        {
            _log.Debug("Socket error: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Session failed");
            closeStatus = WebSocketCloseStatus.InternalServerError;
            closeReason = "internal error";
        }
        finally
        {
            await TeardownAsync(sendLoop, cts, closeStatus, closeReason);
        }
    }

    // ---- Handshake ---------------------------------------------------------------------

    private async Task<(bool Ok, WebSocketCloseStatus Status, string Reason)> AuthenticateAsync(CancellationToken cancellationToken)
    {
        using var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        helloTimeout.CancelAfter(Math.Max(100, _options.HelloTimeoutMs));

        string? text;
        try
        {
            text = await ReceiveTextAsync(helloTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log.Information("Closed: no hello within {TimeoutMs} ms", _options.HelloTimeoutMs);
            return (false, WebSocketCloseStatus.PolicyViolation, "hello timeout");
        }

        if (text is null)
        {
            return (false, WebSocketCloseStatus.NormalClosure, "bye");
        }

        ClientMessage message;
        try
        {
            message = ClientMessageParser.Parse(text);
        }
        catch (ProtocolException ex)
        {
            _log.Information("Closed: bad first frame ({Error})", ex.Message);
            return (false, WebSocketCloseStatus.PolicyViolation, "hello required");
        }

        if (message is not HelloMessage hello)
        {
            _log.Information("Closed: first frame was not hello");
            return (false, WebSocketCloseStatus.PolicyViolation, "hello required");
        }

        if (hello.Protocol != ClientMessageParser.ProtocolVersion)
        {
            _log.Information("Closed: unsupported protocol {Protocol}", hello.Protocol);
            return (false, WebSocketCloseStatus.PolicyViolation, "unsupported protocol");
        }

        if (!TokenMatches(hello.Token))
        {
            _log.Warning("Closed: bad token");
            return (false, WebSocketCloseStatus.PolicyViolation, "unauthorized");
        }

        _authenticated = true;
        _registry.Add();
        _mixer.StatusChanged += OnStatusChanged;
        _mixer.ParameterChanged += OnParameterChanged;
        _mixer.LevelsRead += OnLevelsRead;
        Send(ServerMessages.Welcome(_mixer.Status));
        _log.Information("Client authenticated");
        return (true, WebSocketCloseStatus.NormalClosure, string.Empty);
    }

    private bool TokenMatches(string presented)
    {
        var expected = _options.Token;
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(presented))
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(expected);
        var b = Encoding.UTF8.GetBytes(presented);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    // ---- Dispatch ----------------------------------------------------------------------

    private async Task DispatchAsync(string text, CancellationToken cancellationToken)
    {
        ClientMessage message;
        try
        {
            message = ClientMessageParser.Parse(text);
        }
        catch (ProtocolException ex)
        {
            _log.Debug("Rejected frame: {Error}", ex.Message);
            Send(ServerMessages.Error(ex.Message));
            return;
        }

        switch (message)
        {
            case HelloMessage:
                Send(ServerMessages.Error("already authenticated"));
                break;

            case PingMessage:
                Send(ServerMessages.Pong());
                break;

            case WatchMessage watch:
                await HandleWatchAsync(watch.Params, cancellationToken);
                break;

            case UnwatchMessage unwatch:
                await HandleUnwatchAsync(unwatch.Params, cancellationToken);
                break;

            case GetMessage get:
                var values = await _mixer.GetAsync(get.Params, cancellationToken);
                Send(ServerMessages.Values(get.Id, values));
                break;

            case SetMessage set:
                // A set can wait on the per-parameter rate limit; do not stall the receive loop.
                _ = HandleSetAsync(set, cancellationToken);
                break;

            case LevelsMessage levels:
                await HandleLevelsAsync(levels, cancellationToken);
                break;
        }
    }

    private async Task HandleWatchAsync(IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        var added = new List<string>();
        var existing = new List<string>();
        lock (_gate)
        {
            foreach (var name in names)
            {
                (_watched.Add(name) ? added : existing).Add(name);
            }
        }

        var snapshot = new Dictionary<string, ParameterReadResult>(StringComparer.Ordinal);
        if (added.Count > 0)
        {
            foreach (var (k, v) in await _mixer.WatchAsync(added, cancellationToken))
            {
                snapshot[k] = v;
            }
        }

        if (existing.Count > 0)
        {
            foreach (var (k, v) in await _mixer.GetAsync(existing, cancellationToken))
            {
                snapshot[k] = v;
            }
        }

        Send(ServerMessages.Snapshot(snapshot));
    }

    private async Task HandleUnwatchAsync(IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        var removed = new List<string>();
        lock (_gate)
        {
            foreach (var name in names)
            {
                if (_watched.Remove(name))
                {
                    removed.Add(name);
                }
            }
        }

        if (removed.Count > 0)
        {
            await _mixer.UnwatchAsync(removed, cancellationToken);
        }
    }

    private async Task HandleSetAsync(SetMessage set, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _mixer.SetAsync(set.Param, set.Value, cancellationToken);
            Send(ServerMessages.Result(set.Id, result.Ok, result.Error));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Set {Parameter} failed", set.Param);
            Send(ServerMessages.Result(set.Id, false, "internal error"));
        }
    }

    private async Task HandleLevelsAsync(LevelsMessage levels, CancellationToken cancellationToken)
    {
        var wanted = levels.Enabled ? new HashSet<LevelChannel>(levels.Channels) : new HashSet<LevelChannel>();
        List<LevelChannel> added;
        List<LevelChannel> removed;
        lock (_gate)
        {
            added = wanted.Where(c => !_levels.Contains(c)).ToList();
            removed = _levels.Where(c => !wanted.Contains(c)).ToList();
            _levels.Clear();
            _levels.UnionWith(wanted);
        }

        if (removed.Count > 0)
        {
            await _mixer.UnsubscribeLevelsAsync(removed, cancellationToken);
        }

        if (added.Count > 0)
        {
            await _mixer.SubscribeLevelsAsync(added, cancellationToken);
        }
    }

    // ---- Mixer events (raised on the mixer thread; keep them cheap) --------------------

    private void OnStatusChanged(object? sender, VoicemeeterStatus status) => Send(ServerMessages.Status(status));

    private void OnParameterChanged(object? sender, ParameterChange change)
    {
        bool watched;
        lock (_gate)
        {
            watched = _watched.Contains(change.Name);
        }

        if (watched)
        {
            Send(ServerMessages.Changed(change.Name, change.Value));
        }
    }

    private void OnLevelsRead(object? sender, IReadOnlyList<LevelReading> readings)
    {
        List<LevelReading>? mine = null;
        lock (_gate)
        {
            if (_levels.Count == 0)
            {
                return;
            }

            foreach (var reading in readings)
            {
                if (_levels.Contains(reading.Channel))
                {
                    (mine ??= new List<LevelReading>()).Add(reading);
                }
            }
        }

        if (mine is not null)
        {
            Send(ServerMessages.Levels(mine));
        }
    }

    // ---- Socket plumbing ---------------------------------------------------------------

    private void Send(string frame) => _outbound.Writer.TryWrite(frame);

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _outbound.Reader.ReadAllAsync(cancellationToken))
            {
                var bytes = Encoding.UTF8.GetBytes(frame);
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException ex)
        {
            _log.Debug("Send loop ended: {Message}", ex.Message);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private sealed class FrameTooLargeException : Exception
    {
    }

    private sealed class BinaryFrameException : Exception
    {
    }

    /// <summary>Reads one complete text frame, or <c>null</c> when the client sent a close frame.</summary>
    private async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Text)
            {
                throw new BinaryFrameException();
            }

            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxFrameBytes)
            {
                throw new FrameTooLargeException();
            }

            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            }
        }
    }

    private async Task TeardownAsync(
        Task sendLoop,
        CancellationTokenSource cts,
        WebSocketCloseStatus? closeStatus,
        string? closeReason)
    {
        _mixer.StatusChanged -= OnStatusChanged;
        _mixer.ParameterChanged -= OnParameterChanged;
        _mixer.LevelsRead -= OnLevelsRead;

        if (_authenticated)
        {
            _registry.Remove();
            await ReleaseMixerResourcesAsync();
        }

        // Stop sending before closing: a WebSocket allows one outstanding send at a time.
        _outbound.Writer.TryComplete();
        await cts.CancelAsync();
        await sendLoop;

        if (closeStatus is not null && (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived))
        {
            using var closeCts = new CancellationTokenSource(CloseTimeout);
            try
            {
                await _socket.CloseAsync(closeStatus.Value, closeReason, closeCts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
            {
                _socket.Abort();
            }
        }

        _log.Information("Client disconnected");
    }

    private async Task ReleaseMixerResourcesAsync()
    {
        List<string> watched;
        List<LevelChannel> levels;
        lock (_gate)
        {
            watched = _watched.ToList();
            levels = _levels.ToList();
            _watched.Clear();
            _levels.Clear();
        }

        try
        {
            using var timeout = new CancellationTokenSource(CloseTimeout);
            if (watched.Count > 0)
            {
                await _mixer.UnwatchAsync(watched, timeout.Token);
            }

            if (levels.Count > 0)
            {
                await _mixer.UnsubscribeLevelsAsync(levels, timeout.Token);
            }
        }
        catch (OperationCanceledException)
        {
            _log.Warning("Timed out releasing watches; the mixer thread may be stopping");
        }
    }
}
