using Microsoft.Win32;

namespace VoicemeeterBridge.Tray;

/// <summary>
/// Registers the bridge in the current user's <c>Run</c> key so it starts with the
/// interactive session. A user-session app, never a service: the Voicemeeter Remote API
/// only works from the desktop session Voicemeeter itself runs in.
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Value name under the Run key.</summary>
    public const string ValueName = "VoicemeeterBridge";

    /// <summary>Command line that will be registered.</summary>
    public static string Command => $"\"{Environment.ProcessPath}\"";

    /// <summary>True when the Run key points at this executable.</summary>
    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value
            && string.Equals(value, Command, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Writes the Run key entry.</summary>
    public static void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException($"Could not open HKCU\\{RunKeyPath} for writing.");
        key.SetValue(ValueName, Command, RegistryValueKind.String);
    }

    /// <summary>Removes the Run key entry if present.</summary>
    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
