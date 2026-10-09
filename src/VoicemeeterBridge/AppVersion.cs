using System.Reflection;

namespace VoicemeeterBridge;

/// <summary>
/// The application version, taken from the assembly built with the <c>Version</c>
/// property in <c>Directory.Build.props</c>. Semantic versioning: MAJOR.MINOR.PATCH.
/// </summary>
public static class AppVersion
{
    /// <summary>Version string, for example <c>0.1.0</c>.</summary>
    public static string Current { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = typeof(AppVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Drop any "+metadata" suffix so the string stays MAJOR.MINOR.PATCH.
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        var version = assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
