namespace VoicemeeterBridge.Native;

/// <summary>
/// In-memory <see cref="IVoicemeeterClient"/> for tests and for running the bridge on a
/// machine without Voicemeeter (<c>--fake</c>). Unlike the native client this one is
/// thread safe so tests can poke it from the test thread while the mixer service polls.
/// </summary>
public sealed class FakeVoicemeeterClient : IVoicemeeterClient
{
    private readonly object _gate = new();
    private readonly Dictionary<string, float> _floats = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(LevelType, int), float> _levels = new();
    private bool _dirty = true;
    private bool _loggedIn;
    private bool _running = true;
    private int _setCallCount;
    private int _loginCallCount;

    /// <summary>Edition reported to callers. Defaults to Potato.</summary>
    public VoicemeeterKind Kind { get; set; } = VoicemeeterKind.Potato;

    /// <summary>Version string reported to callers.</summary>
    public string Version { get; set; } = "3.1.1.2";

    /// <summary>Number of float and string set calls so far.</summary>
    public int SetCallCount
    {
        get { lock (_gate) { return _setCallCount; } }
    }

    /// <summary>Number of <see cref="Login"/> calls so far.</summary>
    public int LoginCallCount
    {
        get { lock (_gate) { return _loginCallCount; } }
    }

    /// <summary>True while a login is active.</summary>
    public bool IsLoggedIn
    {
        get { lock (_gate) { return _loggedIn; } }
    }

    /// <summary>Whether the fake pretends the Voicemeeter application is running.</summary>
    public bool IsRunning
    {
        get { lock (_gate) { return _running; } }
    }

    /// <summary>
    /// Pretends Voicemeeter was closed. Calls return <see cref="VoicemeeterResult.NoServer"/>
    /// until <see cref="SimulateVoicemeeterStarted"/>.
    /// </summary>
    public void SimulateVoicemeeterStopped()
    {
        lock (_gate)
        {
            _running = false;
        }
    }

    /// <summary>Pretends Voicemeeter was started again. Everything is reported dirty.</summary>
    public void SimulateVoicemeeterStarted()
    {
        lock (_gate)
        {
            _running = true;
            _dirty = true;
        }
    }

    /// <summary>
    /// Changes a float as if the user moved a control in the Voicemeeter UI, which marks
    /// parameters dirty for the next poll.
    /// </summary>
    public void SimulateExternalChange(string name, float value)
    {
        lock (_gate)
        {
            _floats[name] = value;
            _dirty = true;
        }
    }

    /// <summary>String flavour of <see cref="SimulateExternalChange(string, float)"/>.</summary>
    public void SimulateExternalChange(string name, string value)
    {
        lock (_gate)
        {
            _strings[name] = value;
            _dirty = true;
        }
    }

    /// <summary>Sets the linear level that <see cref="GetLevel"/> will return.</summary>
    public void SetLevel(LevelType type, int channel, float linear)
    {
        lock (_gate)
        {
            _levels[(type, channel)] = linear;
        }
    }

    /// <summary>Reads the current float value, or <c>null</c> when never set.</summary>
    public float? PeekFloat(string name)
    {
        lock (_gate)
        {
            return _floats.TryGetValue(name, out var v) ? v : null;
        }
    }

    /// <summary>Reads the current string value, or <c>null</c> when never set.</summary>
    public string? PeekString(string name)
    {
        lock (_gate)
        {
            return _strings.TryGetValue(name, out var v) ? v : null;
        }
    }

    /// <inheritdoc />
    public LoginResult Login()
    {
        lock (_gate)
        {
            _loginCallCount++;
            if (_loggedIn)
            {
                return LoginResult.UnexpectedLogin;
            }

            _loggedIn = true;
            _dirty = true;
            return _running ? LoginResult.Ok : LoginResult.OkVoicemeeterNotRunning;
        }
    }

    /// <inheritdoc />
    public int Logout()
    {
        lock (_gate)
        {
            _loggedIn = false;
            return VoicemeeterResult.Ok;
        }
    }

    /// <inheritdoc />
    public int GetVoicemeeterType(out VoicemeeterKind kind)
    {
        lock (_gate)
        {
            if (!_running)
            {
                kind = VoicemeeterKind.Unknown;
                return VoicemeeterResult.NoServer;
            }

            kind = Kind;
            return VoicemeeterResult.Ok;
        }
    }

    /// <inheritdoc />
    public int GetVoicemeeterVersion(out string version)
    {
        lock (_gate)
        {
            if (!_running)
            {
                version = string.Empty;
                return VoicemeeterResult.NoServer;
            }

            version = Version;
            return VoicemeeterResult.Ok;
        }
    }

    /// <inheritdoc />
    public DirtyResult IsParametersDirty()
    {
        lock (_gate)
        {
            if (!_running)
            {
                return DirtyResult.NoServer;
            }

            var result = _dirty ? DirtyResult.Dirty : DirtyResult.Clean;
            _dirty = false;
            return result;
        }
    }

    /// <inheritdoc />
    public int GetParameterFloat(string name, out float value)
    {
        lock (_gate)
        {
            if (!_running)
            {
                value = 0f;
                return VoicemeeterResult.NoServer;
            }

            if (_floats.TryGetValue(name, out value))
            {
                return VoicemeeterResult.Ok;
            }

            if (_strings.ContainsKey(name))
            {
                return VoicemeeterResult.Error;
            }

            value = 0f;
            return VoicemeeterResult.UnknownParameter;
        }
    }

    /// <inheritdoc />
    public int GetParameterString(string name, out string value)
    {
        lock (_gate)
        {
            if (!_running)
            {
                value = string.Empty;
                return VoicemeeterResult.NoServer;
            }

            if (_strings.TryGetValue(name, out var s))
            {
                value = s;
                return VoicemeeterResult.Ok;
            }

            value = string.Empty;
            return _floats.ContainsKey(name) ? VoicemeeterResult.Error : VoicemeeterResult.UnknownParameter;
        }
    }

    /// <inheritdoc />
    public int SetParameterFloat(string name, float value)
    {
        lock (_gate)
        {
            _setCallCount++;
            if (!_running)
            {
                return VoicemeeterResult.NoServer;
            }

            if (_strings.ContainsKey(name))
            {
                return VoicemeeterResult.Error;
            }

            _floats[name] = value;
            _dirty = true;
            return VoicemeeterResult.Ok;
        }
    }

    /// <inheritdoc />
    public int SetParameterString(string name, string value)
    {
        lock (_gate)
        {
            _setCallCount++;
            if (!_running)
            {
                return VoicemeeterResult.NoServer;
            }

            if (_floats.ContainsKey(name))
            {
                return VoicemeeterResult.Error;
            }

            _strings[name] = value;
            _dirty = true;
            return VoicemeeterResult.Ok;
        }
    }

    /// <inheritdoc />
    public int GetLevel(LevelType type, int channel, out float value)
    {
        lock (_gate)
        {
            if (!_running)
            {
                value = 0f;
                return VoicemeeterResult.NoServer;
            }

            value = _levels.TryGetValue((type, channel), out var v) ? v : 0f;
            return VoicemeeterResult.Ok;
        }
    }

    /// <summary>Seeds a typical Potato layout so <c>--fake</c> mode has something to show.</summary>
    public void SeedDefaults()
    {
        lock (_gate)
        {
            for (int i = 0; i < 8; i++)
            {
                _floats[$"Strip[{i}].Gain"] = 0f;
                _floats[$"Strip[{i}].Mute"] = 0f;
                _floats[$"Strip[{i}].Solo"] = 0f;
                _floats[$"Strip[{i}].Mono"] = 0f;
                _strings[$"Strip[{i}].Label"] = $"Strip {i}";
                for (int b = 1; b <= 3; b++)
                {
                    _floats[$"Strip[{i}].A{b}"] = 0f;
                    _floats[$"Strip[{i}].B{b}"] = 0f;
                }
            }

            for (int i = 0; i < 8; i++)
            {
                _floats[$"Bus[{i}].Gain"] = 0f;
                _floats[$"Bus[{i}].Mute"] = 0f;
                _strings[$"Bus[{i}].Label"] = $"Bus {i}";
            }

            _dirty = true;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }
}
