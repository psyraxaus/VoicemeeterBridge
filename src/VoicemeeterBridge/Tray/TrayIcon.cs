using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using VoicemeeterBridge.Configuration;
using VoicemeeterBridge.Mixer;
using VoicemeeterBridge.WebSockets;

namespace VoicemeeterBridge.Tray;

/// <summary>
/// The notification area icon. Must be created on the UI thread after the WinForms
/// application has been initialised; mixer and session events arrive on other threads
/// and are marshalled through the captured synchronization context.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly IMixerStateService _mixer;
    private readonly SessionRegistry _sessions;
    private readonly BridgeOptions _options;
    private readonly ILogger _log;
    private readonly Action _exit;
    private readonly SynchronizationContext _ui;
    private readonly Icon _connectedIcon;
    private readonly Icon _disconnectedIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startupItem;
    private bool _disposed;

    /// <summary>
    /// Creates and shows the icon.
    /// </summary>
    /// <param name="mixer">Source of the Voicemeeter connection status.</param>
    /// <param name="sessions">Source of the client count.</param>
    /// <param name="options">Bridge settings, for the token and port.</param>
    /// <param name="log">Serilog logger.</param>
    /// <param name="ui">Synchronization context of the UI thread.</param>
    /// <param name="exit">Called on the UI thread when the user picks Exit.</param>
    public TrayIcon(
        IMixerStateService mixer,
        SessionRegistry sessions,
        BridgeOptions options,
        ILogger log,
        SynchronizationContext ui,
        Action exit)
    {
        ArgumentNullException.ThrowIfNull(mixer);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(exit);
        _mixer = mixer;
        _sessions = sessions;
        _options = options;
        _log = log.ForContext<TrayIcon>();
        _ui = ui;
        _exit = exit;

        _connectedIcon = DrawIcon(Color.FromArgb(46, 160, 67));
        _disconnectedIcon = DrawIcon(Color.FromArgb(140, 140, 140));

        _statusItem = new ToolStripMenuItem { Enabled = false };
        _startupItem = new ToolStripMenuItem("Start with Windows", null, OnToggleStartup) { CheckOnClick = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open log folder", null, OnOpenLogFolder));
        menu.Items.Add(new ToolStripMenuItem("Copy token", null, OnCopyToken));
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, OnExit));
        menu.Opening += (_, _) => RefreshStartupCheck();

        _notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
        };

        _mixer.StatusChanged += OnStatusChanged;
        _sessions.CountChanged += OnCountChanged;
        UpdateStatus();
    }

    private void OnStatusChanged(object? sender, VoicemeeterStatus status) => _ui.Post(_ => UpdateStatus(), null);

    private void OnCountChanged(object? sender, int count) => _ui.Post(_ => UpdateStatus(), null);

    private void UpdateStatus()
    {
        if (_disposed)
        {
            return;
        }

        var status = _mixer.Status;
        var clients = _sessions.Count;
        var vm = status.Connected
            ? $"Voicemeeter {status.Kind} {status.Version}"
            : "Voicemeeter not running";
        var text = $"VoicemeeterBridge {AppVersion.Current}  port {_options.Port}\n{vm}\nClients: {clients}";

        // NotifyIcon.Text is limited to 127 characters.
        _notifyIcon.Text = text.Length > 127 ? text[..127] : text;
        _notifyIcon.Icon = status.Connected ? _connectedIcon : _disconnectedIcon;
        _statusItem.Text = $"{vm}, {clients} client{(clients == 1 ? string.Empty : "s")}";
    }

    private void RefreshStartupCheck()
    {
        try
        {
            _startupItem.Checked = StartupRegistration.IsEnabled();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not read the Run key");
        }
    }

    private void OnToggleStartup(object? sender, EventArgs e)
    {
        try
        {
            if (StartupRegistration.IsEnabled())
            {
                StartupRegistration.Disable();
                _log.Information("Removed Start with Windows entry");
            }
            else
            {
                StartupRegistration.Enable();
                _log.Information("Added Start with Windows entry: {Command}", StartupRegistration.Command);
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Could not change the Run key");
            MessageBox.Show(ex.Message, "VoicemeeterBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        RefreshStartupCheck();
    }

    private void OnOpenLogFolder(object? sender, EventArgs e)
    {
        try
        {
            Directory.CreateDirectory(BridgeApp.LogFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{BridgeApp.LogFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Could not open the log folder");
        }
    }

    private void OnCopyToken(object? sender, EventArgs e)
    {
        try
        {
            Clipboard.SetText(_options.Token ?? string.Empty);
            _notifyIcon.ShowBalloonTip(2000, "VoicemeeterBridge", "Token copied to the clipboard.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Could not copy the token");
        }
    }

    private void OnExit(object? sender, EventArgs e)
    {
        _log.Information("Exit chosen from the tray menu");
        _exit();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mixer.StatusChanged -= OnStatusChanged;
        _sessions.CountChanged -= OnCountChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _connectedIcon.Dispose();
        _disconnectedIcon.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>Draws a filled circle with a white "V" so the icon needs no embedded resource.</summary>
    private static Icon DrawIcon(Color fill)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(fill);
            g.FillEllipse(brush, 1, 1, 30, 30);
            using var pen = new Pen(Color.White, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, [new PointF(9, 10), new PointF(16, 23), new PointF(23, 10)]);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
