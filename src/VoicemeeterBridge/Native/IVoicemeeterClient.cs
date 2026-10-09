namespace VoicemeeterBridge.Native;

/// <summary>
/// Thin wrapper over the Voicemeeter Remote API. Every call returns the raw native
/// result code so the caller can decide how to react; nothing here throws for a
/// Voicemeeter-side failure. The native implementation is not thread safe: the whole
/// instance must be used from one thread.
/// </summary>
public interface IVoicemeeterClient : IDisposable
{
    /// <summary>Opens the client connection to the Voicemeeter engine.</summary>
    LoginResult Login();

    /// <summary>Closes the client connection.</summary>
    /// <returns>Native result code.</returns>
    int Logout();

    /// <summary>Reads the running edition.</summary>
    /// <param name="kind">Receives the edition on success.</param>
    /// <returns>Native result code.</returns>
    int GetVoicemeeterType(out VoicemeeterKind kind);

    /// <summary>Reads the running version as a dotted string, for example <c>3.1.1.2</c>.</summary>
    /// <param name="version">Receives the version on success.</param>
    /// <returns>Native result code.</returns>
    int GetVoicemeeterVersion(out string version);

    /// <summary>
    /// Checks whether any parameter changed since the previous call. The native DLL
    /// refreshes its parameter cache only when this is polled.
    /// </summary>
    DirtyResult IsParametersDirty();

    /// <summary>Reads a float parameter.</summary>
    /// <param name="name">Parameter name, for example <c>Strip[0].Gain</c>.</param>
    /// <param name="value">Receives the value on success.</param>
    /// <returns>Native result code.</returns>
    int GetParameterFloat(string name, out float value);

    /// <summary>Reads a string parameter.</summary>
    /// <param name="name">Parameter name, for example <c>Strip[0].Label</c>.</param>
    /// <param name="value">Receives the value on success.</param>
    /// <returns>Native result code.</returns>
    int GetParameterString(string name, out string value);

    /// <summary>Writes a float parameter.</summary>
    /// <returns>Native result code.</returns>
    int SetParameterFloat(string name, float value);

    /// <summary>Writes a string parameter.</summary>
    /// <returns>Native result code.</returns>
    int SetParameterString(string name, string value);

    /// <summary>Reads a level meter. The value is linear amplitude, not decibels.</summary>
    /// <param name="type">Which meter stage to read.</param>
    /// <param name="channel">Zero based channel index.</param>
    /// <param name="value">Receives the linear level on success.</param>
    /// <returns>Native result code.</returns>
    int GetLevel(LevelType type, int channel, out float value);
}
