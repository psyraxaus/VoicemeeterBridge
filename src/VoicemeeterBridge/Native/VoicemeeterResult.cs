namespace VoicemeeterBridge.Native;

/// <summary>
/// Helpers for the integer result codes returned by the Voicemeeter Remote API.
/// </summary>
public static class VoicemeeterResult
{
    /// <summary>Operation succeeded.</summary>
    public const int Ok = 0;

    /// <summary>Generic error.</summary>
    public const int Error = -1;

    /// <summary>Voicemeeter is not running.</summary>
    public const int NoServer = -2;

    /// <summary>The parameter name is not known to this Voicemeeter edition.</summary>
    public const int UnknownParameter = -3;

    /// <summary>Structure mismatch between DLL and application.</summary>
    public const int StructureMismatch = -5;

    /// <summary>
    /// Produces a short human readable description for a native result code.
    /// </summary>
    /// <param name="code">The code returned by the native call.</param>
    /// <returns>A description suitable for logs and protocol error strings.</returns>
    public static string Describe(int code) => code switch
    {
        Ok => "ok",
        Error => "error",
        NoServer => "voicemeeter not running",
        UnknownParameter => "unknown parameter",
        StructureMismatch => "structure mismatch",
        _ => $"native error {code}",
    };
}
