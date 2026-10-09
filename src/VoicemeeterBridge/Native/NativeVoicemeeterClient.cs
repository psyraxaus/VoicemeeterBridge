using System.Runtime.InteropServices;
using System.Text;

namespace VoicemeeterBridge.Native;

/// <summary>
/// <see cref="IVoicemeeterClient"/> backed by <c>VoicemeeterRemote64.dll</c>, loaded at
/// runtime with <see cref="NativeLibrary"/> from the folder Voicemeeter is installed in.
/// All exports take and return 32-bit values (Windows C <c>long</c>).
/// </summary>
public sealed unsafe class NativeVoicemeeterClient : IVoicemeeterClient
{
    private const int StringBufferSize = 512;

    private readonly IntPtr _module;
    private readonly delegate* unmanaged[Stdcall]<int> _login;
    private readonly delegate* unmanaged[Stdcall]<int> _logout;
    private readonly delegate* unmanaged[Stdcall]<int*, int> _getType;
    private readonly delegate* unmanaged[Stdcall]<int*, int> _getVersion;
    private readonly delegate* unmanaged[Stdcall]<int> _isDirty;
    private readonly delegate* unmanaged[Stdcall]<byte*, float*, int> _getFloat;
    private readonly delegate* unmanaged[Stdcall]<byte*, byte*, int> _getStringA;
    private readonly delegate* unmanaged[Stdcall]<byte*, float, int> _setFloat;
    private readonly delegate* unmanaged[Stdcall]<byte*, byte*, int> _setStringA;
    private readonly delegate* unmanaged[Stdcall]<int, int, float*, int> _getLevel;

    private bool _disposed;

    /// <summary>Full path of the DLL that was loaded.</summary>
    public string DllPath { get; }

    /// <summary>
    /// Loads the DLL at <paramref name="dllPath"/> and binds every export used by the bridge.
    /// </summary>
    /// <param name="dllPath">Full path to <c>VoicemeeterRemote64.dll</c>.</param>
    /// <exception cref="DllNotFoundException">The DLL could not be loaded.</exception>
    /// <exception cref="EntryPointNotFoundException">An expected export is missing.</exception>
    public NativeVoicemeeterClient(string dllPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dllPath);
        DllPath = dllPath;
        _module = NativeLibrary.Load(dllPath);

        _login = (delegate* unmanaged[Stdcall]<int>)Export("VBVMR_Login");
        _logout = (delegate* unmanaged[Stdcall]<int>)Export("VBVMR_Logout");
        _getType = (delegate* unmanaged[Stdcall]<int*, int>)Export("VBVMR_GetVoicemeeterType");
        _getVersion = (delegate* unmanaged[Stdcall]<int*, int>)Export("VBVMR_GetVoicemeeterVersion");
        _isDirty = (delegate* unmanaged[Stdcall]<int>)Export("VBVMR_IsParametersDirty");
        _getFloat = (delegate* unmanaged[Stdcall]<byte*, float*, int>)Export("VBVMR_GetParameterFloat");
        _getStringA = (delegate* unmanaged[Stdcall]<byte*, byte*, int>)Export("VBVMR_GetParameterStringA");
        _setFloat = (delegate* unmanaged[Stdcall]<byte*, float, int>)Export("VBVMR_SetParameterFloat");
        _setStringA = (delegate* unmanaged[Stdcall]<byte*, byte*, int>)Export("VBVMR_SetParameterStringA");
        _getLevel = (delegate* unmanaged[Stdcall]<int, int, float*, int>)Export("VBVMR_GetLevel");
    }

    private IntPtr Export(string name) => NativeLibrary.GetExport(_module, name);

    /// <inheritdoc />
    public LoginResult Login() => (LoginResult)_login();

    /// <inheritdoc />
    public int Logout() => _logout();

    /// <inheritdoc />
    public int GetVoicemeeterType(out VoicemeeterKind kind)
    {
        int raw = 0;
        int code = _getType(&raw);
        kind = code == VoicemeeterResult.Ok && Enum.IsDefined((VoicemeeterKind)raw)
            ? (VoicemeeterKind)raw
            : VoicemeeterKind.Unknown;
        return code;
    }

    /// <inheritdoc />
    public int GetVoicemeeterVersion(out string version)
    {
        int raw = 0;
        int code = _getVersion(&raw);
        version = code == VoicemeeterResult.Ok ? FormatVersion(raw) : string.Empty;
        return code;
    }

    /// <summary>Unpacks the four byte fields of the native version number.</summary>
    public static string FormatVersion(int packed) =>
        $"{(packed >> 24) & 0xFF}.{(packed >> 16) & 0xFF}.{(packed >> 8) & 0xFF}.{packed & 0xFF}";

    /// <inheritdoc />
    public DirtyResult IsParametersDirty() => (DirtyResult)_isDirty();

    /// <inheritdoc />
    public int GetParameterFloat(string name, out float value)
    {
        float v = 0f;
        int code;
        fixed (byte* pName = Ansi(name))
        {
            code = _getFloat(pName, &v);
        }

        value = code == VoicemeeterResult.Ok ? v : 0f;
        return code;
    }

    /// <inheritdoc />
    public int GetParameterString(string name, out string value)
    {
        var buffer = new byte[StringBufferSize];
        int code;
        fixed (byte* pName = Ansi(name))
        fixed (byte* pBuf = buffer)
        {
            code = _getStringA(pName, pBuf);
        }

        if (code != VoicemeeterResult.Ok)
        {
            value = string.Empty;
            return code;
        }

        int len = Array.IndexOf(buffer, (byte)0);
        if (len < 0)
        {
            len = buffer.Length;
        }

        value = Encoding.Latin1.GetString(buffer, 0, len);
        return code;
    }

    /// <inheritdoc />
    public int SetParameterFloat(string name, float value)
    {
        fixed (byte* pName = Ansi(name))
        {
            return _setFloat(pName, value);
        }
    }

    /// <inheritdoc />
    public int SetParameterString(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        fixed (byte* pName = Ansi(name))
        fixed (byte* pValue = Ansi(value))
        {
            return _setStringA(pName, pValue);
        }
    }

    /// <inheritdoc />
    public int GetLevel(LevelType type, int channel, out float value)
    {
        float v = 0f;
        int code = _getLevel((int)type, channel, &v);
        value = code == VoicemeeterResult.Ok ? v : 0f;
        return code;
    }

    /// <summary>Encodes a string as NUL terminated Latin-1 bytes for the ANSI exports.</summary>
    private static byte[] Ansi(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = new byte[Encoding.Latin1.GetByteCount(text) + 1];
        Encoding.Latin1.GetBytes(text, 0, text.Length, bytes, 0);
        return bytes;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_module != IntPtr.Zero)
        {
            NativeLibrary.Free(_module);
        }
    }
}
