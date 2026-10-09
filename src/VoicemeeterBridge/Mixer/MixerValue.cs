namespace VoicemeeterBridge.Mixer;

/// <summary>
/// A Voicemeeter parameter value. Voicemeeter parameters are either numbers (gain,
/// mute, routing flags) or text (labels, device names); this type carries exactly one.
/// </summary>
public readonly record struct MixerValue
{
    private MixerValue(float? number, string? text)
    {
        Number = number;
        Text = text;
    }

    /// <summary>The numeric value, or <c>null</c> for a text value.</summary>
    public float? Number { get; }

    /// <summary>The text value, or <c>null</c> for a numeric value.</summary>
    public string? Text { get; }

    /// <summary>True when this is a text value.</summary>
    public bool IsText => Text is not null;

    /// <summary>Wraps a number.</summary>
    public static MixerValue FromNumber(float value) => new(value, null);

    /// <summary>Wraps text.</summary>
    public static MixerValue FromText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new(null, value);
    }

    /// <summary>The boxed CLR value, handy for JSON serialisation.</summary>
    public object AsObject() => IsText ? Text! : Number!.Value;

    /// <inheritdoc />
    public override string ToString() => IsText ? $"\"{Text}\"" : Number!.Value.ToString("0.###");
}

/// <summary>
/// Identifies one level meter: the stage (<see cref="Native.LevelType"/>) and the channel index.
/// </summary>
/// <param name="Type">Meter stage, as the numeric value of <see cref="Native.LevelType"/>.</param>
/// <param name="Channel">Zero based channel index.</param>
public readonly record struct LevelChannel(int Type, int Channel);

/// <summary>One level meter reading in decibels full scale.</summary>
/// <param name="Channel">Which meter.</param>
/// <param name="Db">Level in dBFS, clamped at <see cref="MinDb"/> for silence.</param>
public readonly record struct LevelReading(LevelChannel Channel, float Db)
{
    /// <summary>Floor used instead of negative infinity for a silent channel.</summary>
    public const float MinDb = -200f;

    /// <summary>Converts the linear amplitude returned by the DLL to dBFS.</summary>
    public static float LinearToDb(float linear)
    {
        if (linear <= 0f || float.IsNaN(linear))
        {
            return MinDb;
        }

        var db = 20f * MathF.Log10(linear);
        return db < MinDb ? MinDb : db;
    }
}

/// <summary>Connection status of the Voicemeeter engine as seen by the bridge.</summary>
/// <param name="Connected">True when the engine answers API calls.</param>
/// <param name="Kind">Edition, or <see cref="Native.VoicemeeterKind.Unknown"/> while disconnected.</param>
/// <param name="Version">Dotted version string, or empty while disconnected.</param>
public sealed record VoicemeeterStatus(bool Connected, Native.VoicemeeterKind Kind, string Version)
{
    /// <summary>Status used before the first successful connection.</summary>
    public static readonly VoicemeeterStatus Disconnected = new(false, Native.VoicemeeterKind.Unknown, string.Empty);
}

/// <summary>Outcome of a read of one parameter.</summary>
/// <param name="Value">The value when the read succeeded.</param>
/// <param name="Error">A short error description when it failed.</param>
public readonly record struct ParameterReadResult(MixerValue? Value, string? Error)
{
    /// <summary>True when <see cref="Value"/> is set.</summary>
    public bool Ok => Error is null;

    /// <summary>Builds a successful result.</summary>
    public static ParameterReadResult Success(MixerValue value) => new(value, null);

    /// <summary>Builds a failed result.</summary>
    public static ParameterReadResult Failure(string error) => new(null, error);
}

/// <summary>Outcome of a parameter write.</summary>
/// <param name="Ok">True when the DLL accepted the write.</param>
/// <param name="Error">A short error description when it failed.</param>
public readonly record struct SetResult(bool Ok, string? Error)
{
    /// <summary>Shared success value.</summary>
    public static readonly SetResult Success = new(true, null);

    /// <summary>Builds a failed result.</summary>
    public static SetResult Failure(string error) => new(false, error);
}
