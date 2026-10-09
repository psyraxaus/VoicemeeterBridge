# VoicemeeterBridge

A Windows tray app that exposes the local Voicemeeter instance to other machines on the LAN over a WebSocket, with reliable two-way state.

Voicemeeter's Remote API (`VoicemeeterRemote64.dll`) only works on the PC Voicemeeter runs on. This app wraps it and publishes it at `ws://<pc>:5088/ws` so that a client elsewhere (for example ProsimCompanion) can watch parameters, receive every change as it happens, and set values.

- [docs/protocol.md](docs/protocol.md) describes the wire protocol in full.
- [samples/BridgeProbe](samples/BridgeProbe) is a small console client.

## Requirements

- Windows 10/11 x64.
- Voicemeeter (any edition: Standard, Banana or Potato) installed from VB-Audio. The bridge finds the DLL through the Voicemeeter uninstall registry key and loads it in place; nothing is copied.
- .NET 10 SDK to build. The published output can be self-contained.

## Build

From Git Bash in the repository root:

```bash
dotnet build
dotnet test
```

To produce a folder you can copy to the flight-sim PC:

```bash
dotnet publish src/VoicemeeterBridge -c Release -r win-x64 --self-contained -o publish
```

Copy the `publish` folder anywhere, for example `C:\Tools\VoicemeeterBridge`, and run `VoicemeeterBridge.exe`.

### Installer

[Inno Setup 6](https://jrsoftware.org/isinfo.php) must be installed. Then:

```bash
build/build-installer.sh
```

This runs the tests, publishes a self-contained build to `publish/`, and compiles [installer/VoicemeeterBridge.iss](installer/VoicemeeterBridge.iss) into `dist/VoicemeeterBridge-Setup-<version>.exe`. Add `--skip-tests` to go faster. If Inno Setup is somewhere unusual, set `ISCC=/path/to/ISCC.exe` in the environment.

The installer is per-user and needs no admin rights: files go to `%LOCALAPPDATA%\Programs\VoicemeeterBridge`, and the optional "start when I sign in" task writes the same `HKCU\...\Run` entry as the tray menu. If the bridge is running, Setup asks you to close it first. Uninstall removes the Run entry and asks whether to delete the settings and logs folder (say No to keep the token).

### Versioning

The app uses [semantic versioning](https://semver.org): `MAJOR.MINOR.PATCH`.

| Bump | When |
|---|---|
| MAJOR | A breaking change to the WebSocket protocol or the configuration file. `protocol` in `hello`/`welcome` changes too. |
| MINOR | New frames, options or features that keep existing clients working. |
| PATCH | Bug fixes only. |

The version lives in one place: `<Version>` in [Directory.Build.props](Directory.Build.props). It flows into the assembly and file version, the log's first line, the tray tooltip, the `agentVersion` field of the `welcome` frame, and the installer's name and version info. To release:

```bash
# 1. edit <Version> in Directory.Build.props, then
git commit -am "Release 0.2.0"
git tag v0.2.0
build/build-installer.sh
```

## Install on the flight-sim PC

1. Run `VoicemeeterBridge-Setup-<version>.exe` on the PC that runs Voicemeeter, or copy the published folder there by hand.
2. Run `VoicemeeterBridge.exe` (the installer offers to start it). A circle icon appears in the notification area: green when Voicemeeter is connected, grey when it is not.
3. Right-click the icon, choose **Copy token**, and paste the token into the client's settings. The token was generated on first run and stored in `%LOCALAPPDATA%\VoicemeeterBridge\appsettings.json`.
4. Right-click the icon and tick **Start with Windows** so the bridge starts with your session. This writes an entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. The bridge must run in the logged-in user session, not as a service, because the Remote API only talks to the Voicemeeter window in the same session.
5. Add the firewall rule below.

Only one instance can run at a time. Starting it twice shows a message and exits.

## Firewall rule, scoped to one client IP

The bridge listens on every interface by default. Open the port only for the client PC. Run this in an **elevated** PowerShell on the flight-sim PC, replacing `192.168.1.50` with the client's IP address:

```powershell
New-NetFirewallRule -DisplayName "VoicemeeterBridge (from ProsimCompanion)" -Direction Inbound -Protocol TCP -LocalPort 5088 -RemoteAddress 192.168.1.50 -Action Allow -Profile Private,Domain
```

From Git Bash:

```bash
powershell -Command "Start-Process powershell -Verb RunAs -ArgumentList '-Command New-NetFirewallRule -DisplayName \"VoicemeeterBridge (from ProsimCompanion)\" -Direction Inbound -Protocol TCP -LocalPort 5088 -RemoteAddress 192.168.1.50 -Action Allow -Profile Private,Domain'"
```

To remove it later:

```powershell
Remove-NetFirewallRule -DisplayName "VoicemeeterBridge (from ProsimCompanion)"
```

If you prefer, set `ListenAddress` in the configuration to the IP of the adapter facing the client PC instead of `0.0.0.0`.

## Configuration

Settings come from two files. The second overrides the first, key by key.

1. `appsettings.json` next to the executable (shipped defaults, safe to leave alone).
2. `%LOCALAPPDATA%\VoicemeeterBridge\appsettings.json` (your overrides; created on first run to hold the token).

Example override file:

```json
{
  "Bridge": {
    "ListenAddress": "0.0.0.0",
    "Port": 5088,
    "Token": "cc4e84cb2ae8c5fc0d1b3c828bc1a93c",
    "PollIntervalMs": 30,
    "LevelPollIntervalMs": 50,
    "ReconnectIntervalMs": 2000,
    "MaxSetsPerSecondPerParameter": 20,
    "HelloTimeoutMs": 5000,
    "DllPath": ""
  },
  "Serilog": {
    "MinimumLevel": { "Default": "Information" }
  }
}
```

| Key | Default | Meaning |
|---|---|---|
| `ListenAddress` | `0.0.0.0` | IP to bind. Use a specific adapter IP to limit exposure. |
| `Port` | `5088` | TCP port for `/ws` and `/health`. |
| `Token` | generated | Shared secret clients must present in `hello`. Delete the value to get a new one on next start. |
| `PollIntervalMs` | `30` | How often `IsParametersDirty` is polled. Lower is faster but costs CPU. |
| `LevelPollIntervalMs` | `50` | How often subscribed level meters are read. |
| `ReconnectIntervalMs` | `2000` | Login retry interval while Voicemeeter is closed. |
| `MaxSetsPerSecondPerParameter` | `20` | Rate limit for writes to one parameter; extra writes are coalesced. |
| `HelloTimeoutMs` | `5000` | A new socket must send `hello` within this time. |
| `DllPath` | empty | Explicit path to `VoicemeeterRemote64.dll`. Leave empty to use the registry. |
| `Serilog:MinimumLevel:Default` | `Information` | `Debug` logs every rejected frame and failed set. `Verbose` is noisier still. |

Restart the bridge after editing the file.

### Command line

| Switch | Effect |
|---|---|
| `--fake` | Run without Voicemeeter, against an in-memory mixer seeded with 8 strips and 8 buses. Useful for developing a client on a PC without Voicemeeter. |

## Logs

Daily rolling files under `%LOCALAPPDATA%\VoicemeeterBridge\logs\bridge-YYYYMMDD.log`, 14 days retained. The tray menu's **Open log folder** goes straight there.

## Troubleshooting

**The icon is grey / `status` says `connected: false`.**
Voicemeeter is not running, or the bridge is not in the same user session as Voicemeeter. Start Voicemeeter; the bridge reconnects within 2 seconds. Do not run the bridge as a service or from a different user account.

**"VoicemeeterBridge failed to start" mentioning the registry or the DLL.**
Voicemeeter is not installed, or was installed in an unusual way. Set `DllPath` in the override file to the full path of `VoicemeeterRemote64.dll` (normally `C:\Program Files (x86)\VB\Voicemeeter\VoicemeeterRemote64.dll`).

**"failed to start" mentioning the address or port.**
Something else owns port 5088. Change `Port` in the override file, and update the firewall rule and the client.

**The client connects but is closed at once with reason `unauthorized`.**
Token mismatch. Use **Copy token** from the tray and paste it into the client again. Tokens are compared byte for byte; watch for trailing spaces.

**The client is closed with `hello timeout`.**
The client opened the socket but did not send `{"op":"hello",...}` as its first frame within 5 seconds.

**The client cannot connect at all from another PC, but `http://localhost:5088/health` works on the flight-sim PC.**
Firewall. Check the rule above, make sure the network profile is Private (not Public), and that `RemoteAddress` matches the client's current IP.

**A `set` returns `unknown parameter`.**
The name does not exist in this Voicemeeter edition (for example `Strip[7]` on Banana) or is misspelled. `welcome` tells you which edition is running. The full parameter list is in VB-Audio's Remote API PDF.

**Values written by the client never produce a `changed`.**
The client has to `watch` the parameter first. `changed` is sent only for watched names. Also check that the value really changed: writing the same value twice produces one `changed`.

**Level frames show `-200` all the time.**
The meter is silent, or the channel index is wrong. Level channels are audio channels, not strip numbers: see the levels section in [docs/protocol.md](docs/protocol.md).

**Quick check from the flight-sim PC.**

```bash
curl http://localhost:5088/health
dotnet samples/BridgeProbe/bin/Debug/net10.0-windows/win-x64/BridgeProbe.dll ws://localhost:5088/ws <token> --watch "Strip[0].Gain" --set "Strip[0].Gain=-6" --once
```

## Project layout

```
src/VoicemeeterBridge/        the tray app and WebSocket host
  Native/                     IVoicemeeterClient, NativeVoicemeeterClient (P/Invoke), FakeVoicemeeterClient
  Mixer/                      MixerStateService: the one thread that talks to the DLL
  Protocol/                   frame parser, validators and frame builders
  WebSockets/                 ClientSession and the /ws endpoint
  Tray/                       NotifyIcon and Start-with-Windows registration
  Configuration/              options and token store
tests/VoicemeeterBridge.Tests/ xUnit tests (all run against the fake client; no Voicemeeter needed)
samples/BridgeProbe/           console client
docs/protocol.md               wire protocol
```
