namespace VoicemeeterBridge.WebSockets;

/// <summary>Counts live, authenticated client sessions so the tray can show them.</summary>
public sealed class SessionRegistry
{
    private int _count;

    /// <summary>Number of authenticated sessions right now.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Raised on any thread when <see cref="Count"/> changes.</summary>
    public event EventHandler<int>? CountChanged;

    /// <summary>Records a session that passed authentication.</summary>
    public void Add()
    {
        var count = Interlocked.Increment(ref _count);
        CountChanged?.Invoke(this, count);
    }

    /// <summary>Records that an authenticated session closed.</summary>
    public void Remove()
    {
        var count = Interlocked.Decrement(ref _count);
        CountChanged?.Invoke(this, count);
    }
}
