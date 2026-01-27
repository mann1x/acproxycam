# ACProxyCam Docker

Docker container for ACProxyCam - an Anycubic 3D printer camera proxy that converts FLV/H.264 streams to MJPEG/HLS for Mainsail/Fluidd/Moonraker compatibility.

## Features

- Web-based terminal with full ANSI color support (ttyd + xterm.js)
- Janus WebRTC gateway integration
- Optional authentication via environment variables
- Auto-restart on daemon crash (s6-overlay)
- Persistent configuration via volume mount
- Logs to stdout/stderr for Docker logging (`docker logs`)
- Multi-architecture: linux/amd64 (x64) and linux/arm64 (Raspberry Pi 4+)

## Image

```
ghcr.io/mann1x/acproxycam:latest
```

## Quick Start

### Using Docker Run

```bash
docker run -d \
  --name acproxycam \
  --restart unless-stopped \
  --network host \
  -v acproxycam-config:/etc/acproxycam \
  -v /etc/machine-id:/etc/machine-id:ro \
  ghcr.io/mann1x/acproxycam:latest
```

### With Authentication

```bash
docker run -d \
  --name acproxycam \
  --restart unless-stopped \
  --network host \
  -e TTYD_USER=admin \
  -e TTYD_PASS=yourpassword \
  -v acproxycam-config:/etc/acproxycam \
  -v /etc/machine-id:/etc/machine-id:ro \
  ghcr.io/mann1x/acproxycam:latest
```

### Using Docker Compose

Create a `docker-compose.yaml`:

```yaml
version: '3.8'

services:
  acproxycam:
    image: ghcr.io/mann1x/acproxycam:latest
    container_name: acproxycam
    restart: unless-stopped
    network_mode: host
    environment:
      # Web terminal settings
      - TTYD_PORT=7681
      - TTYD_USER=admin        # Optional: leave empty for no auth
      - TTYD_PASS=changeme     # Optional: leave empty for no auth
      # Janus WebRTC gateway
      - JANUS_ENABLED=true     # Set to false to disable
    volumes:
      # Persistent configuration
      - acproxycam-config:/etc/acproxycam
      # Machine ID for encryption key (recommended)
      - /etc/machine-id:/etc/machine-id:ro

volumes:
  acproxycam-config:
```

Then run:

```bash
docker-compose up -d
```

## Configuration

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `TTYD_PORT` | `7681` | Web terminal HTTP port |
| `TTYD_USER` | (empty) | Username for web terminal authentication |
| `TTYD_PASS` | (empty) | Password for web terminal authentication |
| `JANUS_ENABLED` | `true` | Enable/disable Janus WebRTC gateway |

**Note:** If `TTYD_USER` or `TTYD_PASS` is empty, the web terminal will be accessible without authentication.

### Ports

The container uses `--network host` mode, so all ports are directly accessible on the host.

| Port | Service | Description |
|------|---------|-------------|
| 7681 | ttyd | Web terminal (configurable via `TTYD_PORT`) |
| 8080+ | Streams | Camera streams (per-printer, configured in CLI) |
| 8188 | Janus | WebRTC WebSocket API |
| 8088 | Janus | Janus HTTP API |

### Volumes

| Container Path | Description |
|----------------|-------------|
| `/etc/acproxycam` | Persistent configuration directory (config.json) |
| `/etc/machine-id` | Host machine ID for encryption key derivation (bind mount, read-only) |

## Usage

### Accessing the Web Terminal

1. Open your browser and navigate to `http://<host-ip>:7681`
2. If authentication is enabled, enter your credentials
3. The ACProxyCam management interface will appear

### Adding a Printer

1. Press `[A]` to add a new printer
2. Enter the printer details:
   - **Name**: Unique identifier (e.g., `kobra-s1`)
   - **IP/Hostname**: Printer's IP address (e.g., `192.168.1.100`)
   - **HTTP Port**: Port for camera streams (default: `8080`)
   - **SSH credentials**: Usually `root` / `rockchip` for Anycubic printers
   - **MQTT Port**: Usually `9883`
3. Configure streaming options (H264, HLS, MJPEG)
4. The printer will start streaming automatically

### Accessing Camera Streams

Once a printer is configured, streams are available at:

| Stream Type | URL |
|-------------|-----|
| MJPEG Stream | `http://<host-ip>:<port>/stream` |
| MJPEG Snapshot | `http://<host-ip>:<port>/snapshot` |
| HLS Stream | `http://<host-ip>:<port>/hls/stream.m3u8` |
| LL-HLS Stream | `http://<host-ip>:<port>/llhls/stream.m3u8` |
| H264 Stream | `http://<host-ip>:<port>/h264` |
| Status | `http://<host-ip>:<port>/status` |

Replace `<port>` with the HTTP port configured for each printer.

### Menu Options

In Docker mode, the following options are available:

| Key | Action |
|-----|--------|
| `A` | Add a new printer |
| `D` | Delete a printer |
| `M` | Modify printer settings |
| `T` | Toggle camera LED |
| `Space` | Pause/Resume streaming |
| `Enter` | Show printer details |
| `B` | BedMesh calibration menu |
| `O` | Obico integration menu |

**Note:** Service control options (Stop/Start, Restart, Uninstall) are hidden in Docker mode since the container manages the service lifecycle.

## Network Mode

### Host Network (Recommended)

```yaml
network_mode: host
```

Host networking provides:
- Direct access to printers on the local network
- No port mapping required
- Full multicast/broadcast support for printer discovery

### Bridge Network (Alternative)

If host networking is not suitable, you can use bridge mode:

```yaml
ports:
  - "7681:7681"   # Web terminal
  - "8080:8080"   # Printer 1 streams
  - "8081:8081"   # Printer 2 streams
  # Add more ports as needed
```

**Note:** Bridge mode requires manually exposing each printer's stream port.

## Encryption

ACProxyCam encrypts sensitive configuration data (SSH passwords) using a key derived from `/etc/machine-id`.

### Recommended Setup

Bind mount the host's machine-id to maintain encryption key consistency:

```yaml
volumes:
  - /etc/machine-id:/etc/machine-id:ro
```

### Alternative Setup

If the host machine-id is not mounted, a unique machine-id will be:
1. Generated on first container start
2. Persisted in `/etc/acproxycam/.machine-id`
3. Restored on subsequent starts

**Warning:** If you lose the config volume without backing up, encrypted passwords cannot be recovered.

## Logs

### View Logs

```bash
# Follow logs in real-time
docker logs -f acproxycam

# Last 100 lines
docker logs --tail 100 acproxycam

# With timestamps
docker logs -t acproxycam
```

### Log Format

Logs include timestamps and severity levels:
```
[2024-01-15 10:30:45] [INF] ACProxyCam daemon starting...
[2024-01-15 10:30:45] [INF] Configuration loaded: 2 printers
[2024-01-15 10:30:46] [INF] [kobra-s1] Connecting to printer...
```

## Building from Source

### Prerequisites

- Docker with buildx support
- Git

### Build Commands

```bash
# Clone the repository
git clone https://github.com/mann1x/acproxycam.git
cd acproxycam

# Build for current architecture
docker build -t acproxycam:local -f docker/Dockerfile .

# Build multi-architecture image
docker buildx build --platform linux/amd64,linux/arm64 \
  -t acproxycam:local \
  -f docker/Dockerfile .
```

## Troubleshooting

### Web terminal not loading

1. Check if container is running:
   ```bash
   docker ps | grep acproxycam
   ```

2. Check container logs:
   ```bash
   docker logs acproxycam
   ```

3. Verify port is accessible:
   ```bash
   curl http://localhost:7681/
   ```

### Cannot connect to printer

1. Verify printer is reachable from Docker host:
   ```bash
   ping <printer-ip>
   ```

2. Check SSH connectivity:
   ```bash
   ssh root@<printer-ip>
   ```

3. Ensure `--network host` is used (required for local network access)

### Configuration lost after restart

1. Check volume is mounted:
   ```bash
   docker inspect acproxycam | grep -A5 Mounts
   ```

2. Verify volume exists:
   ```bash
   docker volume inspect acproxycam-config
   ```

### Stream not working

1. Check printer status in the web terminal
2. Verify the printer's camera is enabled
3. Check if the stream port is accessible:
   ```bash
   curl http://localhost:8080/status
   ```

### Colors not working in terminal

- Use a modern browser (Chrome, Firefox, Edge)
- ttyd uses xterm.js which supports full 256-color ANSI
- Check browser console for JavaScript errors

## Container Architecture

```
┌────────────────────────────────────────────────────────────┐
│                    Docker Container                         │
│  ┌───────────────────────────────────────────────────────┐ │
│  │                 s6-overlay (PID 1)                     │ │
│  │                                                        │ │
│  │  ┌─────────────┐  ┌──────────────┐  ┌──────────────┐  │ │
│  │  │    ttyd     │  │  acproxycam  │  │    janus     │  │ │
│  │  │ web terminal│  │    daemon    │  │   (webrtc)   │  │ │
│  │  │   :7681     │  │  :8080+ etc  │  │    :8188     │  │ │
│  │  └──────┬──────┘  └──────────────┘  └──────────────┘  │ │
│  │         │              ▲                               │ │
│  │         └──────────────┘                               │ │
│  │           IPC socket                                   │ │
│  └───────────────────────────────────────────────────────┘ │
│                                                            │
│  Volume: /etc/acproxycam  │  Logs: stdout (docker logs)   │
└────────────────────────────────────────────────────────────┘
```

- **s6-overlay**: Process supervisor, handles service lifecycle and auto-restart
- **ttyd**: Web terminal server, spawns CLI on each connection
- **acproxycam daemon**: Main service, manages printer connections and streams
- **janus**: WebRTC gateway (optional, for WebRTC streaming)
