using Microsoft.Win32;

namespace VoicemeeterBridge.Native;

/// <summary>
/// Finds the Voicemeeter install folder from the uninstall registry key written by
/// the VB-Audio installer. The DLL is loaded from there in place; it is never copied.
/// </summary>
public static class VoicemeeterLocator
{
    /// <summary>Registry path (under HKLM) of the Voicemeeter uninstall entry.</summary>
    public const string UninstallKeyPath =
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\VB:Voicemeeter {17359A74-1236-5467}";

    /// <summary>File name of the 64-bit Remote API DLL.</summary>
    public const string DllFileName = "VoicemeeterRemote64.dll";

    /// <summary>
    /// Resolves the full path of <see cref="DllFileName"/>.
    /// </summary>
    /// <param name="overridePath">
    /// Optional explicit path from configuration. When set it must exist and it wins.
    /// </param>
    /// <returns>The full DLL path.</returns>
    /// <exception cref="FileNotFoundException">Voicemeeter is not installed or the DLL is missing.</exception>
    public static string ResolveDllPath(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (File.Exists(overridePath))
            {
                return Path.GetFullPath(overridePath);
            }

            throw new FileNotFoundException(
                $"Configured Voicemeeter DLL path '{overridePath}' does not exist.", overridePath);
        }

        var folder = ReadInstallFolder()
            ?? throw new FileNotFoundException(
                "Voicemeeter does not appear to be installed: registry key " +
                $"HKLM\\{UninstallKeyPath} has no UninstallString.");

        var dllPath = Path.Combine(folder, DllFileName);
        if (!File.Exists(dllPath))
        {
            throw new FileNotFoundException(
                $"Voicemeeter install folder '{folder}' does not contain {DllFileName}.", dllPath);
        }

        return dllPath;
    }

    /// <summary>
    /// Reads the install folder from the registry, or <c>null</c> when absent. The
    /// installer writes no dedicated folder value, so the folder is taken from the
    /// <c>UninstallString</c> executable path.
    /// </summary>
    public static string? ReadInstallFolder()
    {
        using var key = Registry.LocalMachine.OpenSubKey(UninstallKeyPath, writable: false);
        if (key?.GetValue("UninstallString") is not string uninstall || string.IsNullOrWhiteSpace(uninstall))
        {
            return null;
        }

        var exe = uninstall.Trim().Trim('"');
        var folder = Path.GetDirectoryName(exe);
        return string.IsNullOrWhiteSpace(folder) ? null : folder;
    }
}
