# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

ACProxyCam is a C# (.NET 8.0) Linux daemon that proxies Anycubic 3D printer camera streams. It converts FLV/H.264 video from printers to MJPEG format for Mainsail/Fluidd/Moonraker compatibility.

## Build Commands

```bash
# Build for Linux x64
dotnet publish src/ACProxyCam/ACProxyCam.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true

# Build for Linux arm64 (Raspberry Pi 4+)
dotnet publish src/ACProxyCam/ACProxyCam.csproj -c Release -r linux-arm64 --self-contained true -p:PublishSingleFile=true
```

Output: `src/ACProxyCam/bin/Release/net8.0/<rid>/publish/acproxycam`

## Architecture

The application uses a **daemon + client model**:

```
Program.cs (Entry Point)
├── Daemon Mode (--daemon flag)
│   └── DaemonService
│       ├── ConfigManager (encrypted config at /etc/acproxycam/config.json)
│       ├── IpcServer (Unix socket for CLI communication)
│       └── PrinterManager
│           └── PrinterThread (per-printer worker)
│               ├── SshCredentialService → retrieves MQTT creds via SSH
│               ├── MqttCameraController → monitors MQTT, sends startCapture, intercepts stops
│               ├── FfmpegDecoder → H.264 FLV to BGR24 frames
│               └── MjpegServer → HTTP /stream, /snapshot, /status
│
└── Management Mode (no args)
    └── ManagementCli (Spectre.Console interactive UI)
        └── IpcClient (communicates with daemon)
```

### Key Components

| Directory | Purpose |
|-----------|---------|
| `Models/` | Data structures: `AppConfig`, `PrinterConfig`, `PrinterStatus` |
| `Daemon/` | Service logic: `DaemonService`, `PrinterManager`, `PrinterThread`, `ConfigManager`, `IpcServer` |
| `Services/` | I/O: `FfmpegDecoder`, `MjpegServer`, `MqttCameraController`, `SshCredentialService`, `CpuAffinityService` |
| `Client/` | CLI: `ManagementCli`, `IpcClient`, `IConsoleUI`, `SpectreConsoleUI`, `SimpleConsoleUI` |

### Per-Printer Flow

1. SSH to printer → read `/userdata/app/gk/config/device_account.json` for MQTT credentials
2. MQTT connect (port 9883, TLS) → subscribe to `#` → detect model code via regex
3. Publish `startCapture` to `anycubic/anycubicCloud/v1/web/printer/{modelCode}/{deviceId}/video`
4. **MQTT stays connected** to monitor for external stop commands and enable instant recovery
5. HTTP GET `http://<printer>:18088/flv` → FFmpeg decode H.264 → SkiaSharp encode JPEG
6. Serve MJPEG on configured port

### Stream Recovery

ACProxyCam implements robust stream recovery to handle external camera stops (e.g., from Anycubic slicer):

1. **MQTT Interception**: Monitors all MQTT messages for `stopCapture` commands from external sources
2. **Instant Restart**: When external stop detected, immediately sends `startCapture` command
3. **Quick Recovery**: If stream drops, attempts up to 3 quick restarts via existing MQTT connection
4. **Snapshot Recovery**: If snapshot requested but no frame available, triggers camera restart

Key code in `MqttCameraController.cs`:
- `CameraStopDetected` event fires when external stop command detected
- Tracks own message IDs to avoid reacting to self-sent commands

### State Machine

`PrinterStatus.State`: `Stopped` → `Initializing` → `Connecting` → `Running` ↔ `Paused` | `Failed` → `Retrying`

## Key Dependencies

- **FFmpeg.AutoGen** - P/Invoke bindings for video decoding (unsafe code)
- **SkiaSharp** - Cross-platform JPEG encoding
- **MQTTnet** - MQTT client for printer control
- **SSH.NET** - SSH for credential retrieval
- **Spectre.Console** - Terminal UI

## Configuration Options

### Per-Printer Settings (`PrinterConfig`)

| Setting | Default | Description |
|---------|---------|-------------|
| `MaxFps` | 0 | Max FPS when clients connected (0 = unlimited/source rate) |
| `IdleFps` | 1 | FPS when no clients (for snapshot availability, 0 = disabled) |
| `JpegQuality` | 80 | JPEG encoding quality (1-100) |
| `SendStopCommand` | false | Send stopCapture via MQTT when stopping (disabled to avoid interfering with slicers) |

### CPU Affinity

`CpuAffinityService` distributes printer threads across CPU cores:
- Queries available CPUs via `/sys/devices/system/cpu/online`
- Assigns threads starting from the last CPU (to avoid CPU 0)
- Uses Linux `sched_setaffinity` syscall

## Important Implementation Details

- **Encryption**: AES-256-GCM with key derived from `/etc/machine-id` + PBKDF2. Encrypted fields prefixed with `encrypted:`.
- **FFmpeg**: Uses system FFmpeg libraries (`libavcodec`, `libavformat`). Code in `FfmpegDecoder.cs` uses unsafe C#.
- **Threading**: One thread per printer (`PrinterThread`). Thread-safe operations use explicit `lock()` on object monitors. State fields marked `volatile`.
- **IPC**: Unix socket at `/run/acproxycam/acproxycam.sock` for daemon-CLI communication.
- **MQTT Connection**: Stays connected during streaming for instant camera restart on external stop.
- **MQTT topic detection**: Regex pattern in `MqttCameraController.cs` extracts model code from subscribed topics.
- **Default SSH creds**: `root/rockchip` for Anycubic printers.

## CLI Input Validation

The management CLI (`ManagementCli.cs`) validates all inputs:

| Input | Validation |
|-------|------------|
| Printer name | Alphanumeric, dashes, underscores only; max 50 chars |
| IP address | Valid IPv4 format |
| TCP ports | Range 1-65535; checks for conflicts and system availability |
| Username | Alphanumeric, dots, dashes, underscores; max 32 chars |
| FPS/Quality | Integer range validation |

Port entry allows retry on conflict instead of failing.

### Pre-flight Check

When adding a printer, optional connectivity check verifies:
1. Ping response
2. SSH port (default 22) reachable
3. MQTT port (default 9883) reachable
4. Camera stream port (18088) reachable

## Test Environment

| Host | Architecture | IP | SSH User |
|------|-------------|-----|----------|
| DietPi (arm64) | linux-arm64 | 192.168.178.12 | claude_test |
| DietPi (x64) | linux-x64 | 192.168.178.2 | claude_test |
| Printer | Anycubic Kobra S1 | 192.168.178.43 | root (password: rockchip) |

Deploy commands:
```bash
# Stop service before deployment
ssh claude_test@192.168.178.12 "sudo systemctl stop acproxycam"

# Copy binary
scp src/ACProxyCam/bin/Release/net8.0/linux-arm64/publish/acproxycam claude_test@192.168.178.12:/tmp/acproxycam_new

# Install and restart
ssh claude_test@192.168.178.12 "sudo cp /tmp/acproxycam_new /usr/local/bin/acproxycam && sudo chmod +x /usr/local/bin/acproxycam && sudo systemctl start acproxycam"
```
