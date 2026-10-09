namespace VoicemeeterBridge.Native;

/// <summary>
/// The Voicemeeter edition reported by <c>VBVMR_GetVoicemeeterType</c>.
/// Numeric values match the native API.
/// </summary>
public enum VoicemeeterKind
{
    /// <summary>No edition known yet (not connected).</summary>
    Unknown = 0,

    /// <summary>Voicemeeter (standard, 2 hardware inputs).</summary>
    Standard = 1,

    /// <summary>Voicemeeter Banana.</summary>
    Banana = 2,

    /// <summary>Voicemeeter Potato.</summary>
    Potato = 3,
}

/// <summary>
/// Level meter source selector for <c>VBVMR_GetLevel</c>. Numeric values match the native API.
/// </summary>
public enum LevelType
{
    /// <summary>Input strip level before the fader.</summary>
    PreFaderInput = 0,

    /// <summary>Input strip level after the fader.</summary>
    PostFaderInput = 1,

    /// <summary>Input strip level after mute.</summary>
    PostMuteInput = 2,

    /// <summary>Output bus level.</summary>
    Output = 3,
}

/// <summary>Result of <c>VBVMR_Login</c>. Numeric values match the native API.</summary>
public enum LoginResult
{
    /// <summary>Logged in and Voicemeeter is running.</summary>
    Ok = 0,

    /// <summary>Logged in, but the Voicemeeter application is not running yet.</summary>
    OkVoicemeeterNotRunning = 1,

    /// <summary>The DLL could not create its client object.</summary>
    NoClient = -1,

    /// <summary>Login was called while already logged in.</summary>
    UnexpectedLogin = -2,
}

/// <summary>Result of <c>VBVMR_IsParametersDirty</c>. Numeric values match the native API.</summary>
public enum DirtyResult
{
    /// <summary>Nothing changed since the last call.</summary>
    Clean = 0,

    /// <summary>At least one parameter changed since the last call.</summary>
    Dirty = 1,

    /// <summary>Generic error.</summary>
    Error = -1,

    /// <summary>Voicemeeter is not running (no server).</summary>
    NoServer = -2,
}
