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
- **Config file permissions**: All files in `/etc/acproxycam/` must be owned by `acproxycam:acproxycam` with mode 600 (rw-------). When manually editing the config with sudo/root, ownership changes to root which breaks the daemon. Restore permissions before starting:
  ```bash
  sudo chown acproxycam:acproxycam /etc/acproxycam/config.json
  sudo chmod 600 /etc/acproxycam/config.json
  ```
- **FFmpeg**: Uses system FFmpeg libraries (`libavcodec`, `libavformat`). Code in `FfmpegDecoder.cs` uses unsafe C#.
- **Threading**: One thread per printer (`PrinterThread`). Thread-safe operations use explicit `lock()` on object monitors. State fields marked `volatile`.
- **IPC**: Unix socket at `/run/acproxycam/acproxycam.sock` for daemon-CLI communication.
- **MQTT Connection**: Stays connected during streaming for instant camera restart on external stop.
- **MQTT topic detection**: Regex pattern in `MqttCameraController.cs` extracts model code from subscribed topics.
- **Default SSH creds**: `root/rockchip` for Anycubic printers.

## Obico Reference Implementation Tracking

The Obico integration in ACProxyCam was developed by studying the reference implementations:
- **moonraker-obico** - Official Moonraker agent for Obico
- **obico-server** - Obico server backend
- **rinkhals** - Klipper/Moonraker integration for Anycubic printers

### Reference Commits File

**Location**: `src/ACProxyCam/Services/Obico/REFERENCE_COMMITS.md`

This file tracks the exact git commits used when developing the Obico integration. When upstream projects are updated, compare diffs from these commits to identify changes that may need to be ported to ACProxyCam.

### Local Reference Repositories

Reference source code is cloned to `D:\INSTALL\acproxycam\obico\`:
- `moonraker-obico/` - https://github.com/TheSpaghettiDetective/moonraker-obico
- `obico-server/` - https://github.com/TheSpaghettiDetective/obico-server
- `rinkhals/` - https://github.com/jbatonnet/Rinkhals

### Checking for Updates

```bash
cd D:\INSTALL\acproxycam\obico

# Check moonraker-obico for changes since tracked commit
cd moonraker-obico && git fetch origin
git log df0005c2f1a9137d3fbb44a5139caa9f8843ed92..origin/main --oneline

# Check obico-server for changes
cd ../obico-server && git fetch origin
git log 9b73caa7b373e89fd23bf2fed646e629ee602640..origin/master --oneline

# Check rinkhals for changes
cd ../rinkhals && git fetch origin
git log deab69a5208e1a88075ffb13b9433a86d46f93cc..origin/main --oneline
```

After porting relevant changes to ACProxyCam, update the commit hashes in `REFERENCE_COMMITS.md`.

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
5. Native API port (18086) - used by Anycubic firmware for print control

## Test Environment

| Host | Architecture | IP | SSH User | Services |
|------|-------------|-----|----------|----------|
| DietPi (arm64) | linux-arm64 | 192.168.178.12 | claude_test | ACProxyCam daemon |
| DietPi (x64) | linux-x64 | 192.168.178.2 | claude_test | Obico server (Docker: `obico-server-web-1`), Janus (native) |
| Printer | Anycubic Kobra S1 | 192.168.178.43 | root (password: rockchip) | Camera stream 18088, Native API 18086 |

### Debugging Tools

The `tools/` directory contains scripts for monitoring printer communication:

```bash
# Monitor MQTT messages (requires verbose logging enabled in daemon)
ssh claude_test@192.168.178.12 "sudo journalctl -u acproxycam -f | grep --line-buffered 'MQTT MSG'"

# Monitor native API traffic on port 18086
ssh claude_test@192.168.178.12 "sudo tcpdump -i any host 192.168.178.43 and port 18086 -A -s 0"
```

See `tools/README.md` for details on enabling verbose MQTT logging.

### Automated Testing with Expect

The CLI has a hidden `--simple-ui` argument that uses `SimpleConsoleUI` instead of `SpectreConsoleUI`. This allows automated testing with `expect` scripts since it doesn't use advanced terminal features.

```bash
# Run CLI with simple UI for expect automation
sudo /usr/local/bin/acproxycam --simple-ui
```

Example expect script for testing BedMesh calibration:
```expect
#!/usr/bin/expect -f
set timeout 300
spawn sudo /usr/local/bin/acproxycam --simple-ui

expect "Select option:"
send "b"  ;# BedMesh menu

expect "Select option:"
send "1"  ;# Calibrate

expect "Select printer"
send "1\r"  ;# First printer

expect -re "heat.?soak|minutes"
send "0\r"  ;# No heat soak

expect -re "complete|Complete|saved"
puts "CALIBRATION COMPLETED"
```

`expect` is available on the test servers for automated testing.

### Deploy and Test via SSH

Use Windows OpenSSH (not Git for Windows SSH) to deploy and test. Do NOT use Python paramiko - use direct SSH and scp commands instead.

**Important**: Always use the full path to Windows OpenSSH or ensure it's in your PATH before Git's SSH:
```bash
# Windows OpenSSH is typically at:
C:\Windows\System32\OpenSSH\ssh.exe
C:\Windows\System32\OpenSSH\scp.exe

# Or use with explicit key file:
ssh -i ~/.ssh/claude_test claude_test@192.168.178.12
```

```bash
# Stop service before deployment
ssh claude_test@192.168.178.12 "sudo systemctl stop acproxycam"

# Copy binary
scp src/ACProxyCam/bin/Release/net8.0/linux-arm64/publish/acproxycam claude_test@192.168.178.12:/tmp/acproxycam_new

# Install and restart
ssh claude_test@192.168.178.12 "sudo cp /tmp/acproxycam_new /usr/local/bin/acproxycam && sudo chmod +x /usr/local/bin/acproxycam && sudo systemctl start acproxycam"

# Check service status
ssh claude_test@192.168.178.12 "sudo systemctl status acproxycam"
```

### Running Tests via SSH

For interactive testing with expect, SSH into the test server and run commands directly:

```bash
# SSH into test server
ssh claude_test@192.168.178.12

# On the server, create and run expect scripts directly
cat > /tmp/test_calibration.exp << 'EOF'
#!/usr/bin/expect -f
set timeout 600
spawn sudo /usr/local/bin/acproxycam --simple-ui

expect "Select option:"
send "b"

expect "Select option:"
send "1"

expect -re "Select printer|Enter.*number"
send "1\r"

expect -re "heat.?soak|minutes|Heat"
send "0\r"

expect -re "name.*calibration|Name|optional"
send "\r"

set timeout 900
expect {
    -re "complete|Complete|saved|SUCCESS|finished" {
        puts "\nCALIBRATION COMPLETED SUCCESSFULLY"
    }
    -re "fail|error|Error|FAIL" {
        puts "\nCALIBRATION FAILED"
    }
    timeout {
        puts "\nCALIBRATION TIMEOUT"
    }
}
send "q"
expect eof
EOF

chmod +x /tmp/test_calibration.exp
expect /tmp/test_calibration.exp
```

**Important**: Always use direct SSH for testing - do not use Python paramiko wrappers as they have issues with terminal handling and expect scripts.

### Building Docker Image via SSH

Docker images can be built remotely on the test server since the local Windows machine doesn't have Docker installed.

```bash
# 1. Sync the entire repo to the test server
scp -r . claude_test@192.168.178.12:/tmp/acproxycam-build/

# 2. Build for single architecture (for local testing)
ssh claude_test@192.168.178.12 "cd /tmp/acproxycam-build && sudo docker buildx build --platform linux/arm64 -t acproxycam:test --load -f docker/Dockerfile ."

# 3. Build for both architectures (without pushing)
ssh claude_test@192.168.178.12 "cd /tmp/acproxycam-build && sudo docker buildx build --platform linux/amd64,linux/arm64 -t acproxycam:latest -f docker/Dockerfile ."

# 4. Run the container for testing
ssh claude_test@192.168.178.12 "sudo docker run -d --name acproxycam-test --network host -v acproxycam-config:/etc/acproxycam -v /etc/machine-id:/etc/machine-id:ro acproxycam:test"

# 5. View logs
ssh claude_test@192.168.178.12 "sudo docker logs -f acproxycam-test"

# 6. Stop and remove test container
ssh claude_test@192.168.178.12 "sudo docker stop acproxycam-test && sudo docker rm acproxycam-test"
```

**Note**: For production builds and publishing to GitHub Container Registry, use the GitHub Actions workflow (`.github/workflows/docker.yml`) which automatically builds and pushes on version tags.

## Creating a Release

### Method 1: GitHub Actions (Preferred)

The GitHub Actions workflow (`.github/workflows/release.yml`) automatically builds release artifacts when a version tag is pushed.

#### Step 1: Bump Version

Edit `src/ACProxyCam/ACProxyCam.csproj` and update the version numbers:

```xml
<Version>1.X.0</Version>
<AssemblyVersion>1.X.0.0</AssemblyVersion>
<FileVersion>1.X.0.0</FileVersion>
<InformationalVersion>1.X.0.$([System.DateTime]::UtcNow.ToString("yyyyMMddHHmm"))</InformationalVersion>
```

#### Step 2: Commit and Tag

```bash
git add -A
git commit -m "Release v1.X.0 - Description of changes

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
git tag v1.X.0
git push origin main --tags
```

#### Pre-release Tags

Tags containing `-alpha`, `-beta`, `-rc`, `-dev`, or `-preview` are automatically marked as pre-releases:

```bash
git tag v1.4.0-beta.1    # Creates pre-release
git tag v1.4.0-rc.1      # Creates pre-release
git tag v1.4.0           # Creates normal release
```

You can also manually trigger a pre-release from the Actions tab by checking the "Mark as pre-release" option.

#### Step 3: Wait for GitHub Actions to Complete

The workflow will:
1. Build for both `linux-x64` and `linux-arm64`
2. Build and push Docker images (multi-arch: amd64, arm64)
3. Create zip files with the `acproxycam` binary
4. Generate SHA256 checksums
5. Create a **draft** GitHub release with all artifacts attached

**Monitor the workflow:**
```bash
# Check workflow status
gh run list --limit 1

# Wait for completion and view details
gh run view <RUN_ID>
```

#### Step 4: Create Release Notes and Publish

**When user asks to "create a release", Claude must:**

1. **Wait for artifacts**: Monitor the GitHub Actions workflow until it completes successfully
2. **Get all changes since last stable release**:
   ```bash
   # Find last stable release (excluding pre-releases)
   git tag -l | grep -v -E '(-alpha|-beta|-rc|-dev|-preview)' | sort -V | tail -1

   # Get all commits since last stable release
   git log v{LAST_STABLE}..v{NEW_VERSION} --oneline
   ```
3. **Download checksums** from the draft release artifacts
4. **Create proper release notes** with:
   - `## What's New` section with user-friendly descriptions (not just commit messages)
   - Group changes by category (Bug Fixes, Improvements, New Features)
   - Include ALL changes from pre-releases between last stable and this version
   - `## Installation` section with download/install commands
   - `## Checksums (SHA256)` section with actual checksums
5. **Publish the release** using:
   ```bash
   gh release edit v{VERSION} --title "v{VERSION} - Short Description" --notes "RELEASE_NOTES" --draft=false
   ```

**Artifacts created:**
- `acproxycam-linux-x64-v{VERSION}.zip`
- `acproxycam-linux-x64-v{VERSION}.zip.sha256`
- `acproxycam-linux-arm64-v{VERSION}.zip`
- `acproxycam-linux-arm64-v{VERSION}.zip.sha256`
- Docker images: `ghcr.io/mann1x/acproxycam:{VERSION}` and `docker.io/mannixita/acproxycam:{VERSION}`

### Method 2: Manual Build (Alternative)

Use `build.bat` for local builds when GitHub Actions is not available.

```cmd
build.bat
```

This outputs to `D:\INSTALL\acproxycam\releases\`.

Then create the release manually:

```bash
gh release create v1.X.0 \
  "D:/INSTALL/acproxycam/releases/acproxycam-linux-x64-v1.X.0.zip" \
  "D:/INSTALL/acproxycam/releases/acproxycam-linux-x64-v1.X.0.zip.sha256" \
  "D:/INSTALL/acproxycam/releases/acproxycam-linux-arm64-v1.X.0.zip" \
  "D:/INSTALL/acproxycam/releases/acproxycam-linux-arm64-v1.X.0.zip.sha256" \
  --title "v1.X.0 - Release Title" \
  --notes "RELEASE_NOTES_HERE"
```

**Requirements:**
- 7-Zip installed at `c:\Program Files\7-Zip\7z.exe`
- .NET 8.0 SDK

### Release Notes Template

```markdown
## What's New

### Feature Category
- Feature or fix description

### Bug Fixes
- Bug fix description

## Installation

\```bash
# Download for your architecture
wget https://github.com/mann1x/acproxycam/releases/download/v1.X.0/acproxycam-linux-arm64-v1.X.0.zip
unzip acproxycam-linux-arm64-v1.X.0.zip
chmod +x acproxycam

# Run with sudo for installation
sudo ./acproxycam
\```

## Checksums (SHA256)

\```
CHECKSUM_X64  acproxycam-linux-x64-v1.X.0.zip
CHECKSUM_ARM64  acproxycam-linux-arm64-v1.X.0.zip
\```
```
