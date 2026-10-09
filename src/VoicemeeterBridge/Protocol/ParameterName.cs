using System.Text.RegularExpressions;

namespace VoicemeeterBridge.Protocol;

/// <summary>
/// Validates Voicemeeter parameter names before they reach the native DLL. The pattern is
/// deliberately conservative: dotted identifiers with optional small numeric indexes,
/// for example <c>Strip[0].Gain</c>, <c>Bus[2].Label</c> or
/// <c>Strip[1].EQ.channel[0].cell[3].gain</c>.
/// </summary>
public static partial class ParameterName
{
    /// <summary>Longest accepted name.</summary>
    public const int MaxLength = 96;

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]*(\[[0-9]{1,3}\])?(\.[A-Za-z][A-Za-z0-9]*(\[[0-9]{1,3}\])?)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    /// <summary>Returns true when <paramref name="name"/> is safe to pass to the DLL.</summary>
    public static bool IsValid(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= MaxLength && Pattern().IsMatch(name);
}
