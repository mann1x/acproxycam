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
│               ├── MqttCameraController → sends startCapture command
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
| `Services/` | I/O: `FfmpegDecoder`, `MjpegServer`, `MqttCameraController`, `SshCredentialService` |
| `Client/` | CLI: `ManagementCli`, `IpcClient` |

### Per-Printer Flow

1. SSH to printer → read `/userdata/app/gk/config/device_account.json` for MQTT credentials
2. MQTT connect (port 9883, TLS) → subscribe to `#` → detect model code via regex
3. Publish `startCapture` to `anycubic/anycubicCloud/v1/web/printer/{modelCode}/{deviceId}/video`
4. HTTP GET `http://<printer>:8080/flv` → FFmpeg decode H.264 → SkiaSharp encode JPEG
5. Serve MJPEG on configured port

### State Machine

`PrinterStatus.State`: `Stopped` → `Initializing` → `Connecting` → `Running` ↔ `Paused` | `Failed` → `Retrying`

## Key Dependencies

- **FFmpeg.AutoGen** - P/Invoke bindings for video decoding (unsafe code)
- **SkiaSharp** - Cross-platform JPEG encoding
- **MQTTnet** - MQTT client for printer control
- **SSH.NET** - SSH for credential retrieval
- **Spectre.Console** - Terminal UI

## Important Implementation Details

- **Encryption**: AES-256-GCM with key derived from `/etc/machine-id` + PBKDF2. Encrypted fields prefixed with `encrypted:`.
- **FFmpeg**: Uses system FFmpeg libraries (`libavcodec`, `libavformat`). Code in `FfmpegDecoder.cs` uses unsafe C#.
- **Threading**: One thread per printer (`PrinterThread`). Thread-safe operations use explicit `lock()` on object monitors.
- **IPC**: Unix socket at `/run/acproxycam/acproxycam.sock` for daemon-CLI communication.
- **MQTT topic detection**: Regex pattern in `MqttCameraController.cs` extracts model code from subscribed topics.
- **Default SSH creds**: `root/rockchip` for Anycubic printers.
