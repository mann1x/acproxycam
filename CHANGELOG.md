# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

---

### [1.6.0] - 2026-02-16

#### Added
- **Vanilla-klipper mode** — full support for Klipper without Anycubic firmware (no MQTT/SSH required)
  - Auto-detected from h264-streamer `/api/config` mode field
  - SSH credential retrieval and MQTT connection completely skipped
  - Configurable Moonraker host and port (supports Moonraker running on a different host)
  - MJPEG streaming via h264-streamer, Obico integration via Moonraker
  - CLI Add/Modify printer flows adapt automatically (skip MQTT/SSH/LAN/LED questions)
  - Pre-flight checks skip SSH and MQTT port verification in vanilla-klipper mode

#### Fixed
- rkmpi output mode recommendation now correctly checks both `h264_enabled` and `acproxycam_flv_proxy` from h264-streamer config — previously defaulted to "Proxy native H.264" even when H.264 was disabled on the printer
- Modify Printer now always fetches fresh h264-streamer config for rkmpi output mode recommendation, even when video source wasn't changed

### [1.5.1] - 2026-02-16

#### Fixed
- MJPEG stream parser corrupting JPEG frames — binary body data was read through line-based reader, truncating at first 0x0A byte and producing invalid frames for H.264 encoding (root cause of H.264 "screen tearing")
- H.264 encoder framerate hint now uses measured input FPS instead of hardcoded value
- FPS display in dashboard shows actual encoding output rate when MJPEG→H.264 encoding is active
- CLI video source recommendation now correctly suggests "Encode MJPEG→H.264" when FLV proxy is enabled but printer doesn't provide native H.264

#### Changed
- Replaced x264 `tune=zerolatency` with explicit `rc-lookahead=0:sync-lookahead=0` for better encoding quality
- H.264 encoder PTS now uses wall-clock milliseconds instead of frame counter for accurate timing
- FLV proxy reconnection improved to avoid circular dependency with h264-streamer
- Version logged at daemon startup for easier build identification
- CLI warns when its version differs from running daemon

### [1.5.0] - 2026-02-06

#### Added
- MJPEG source mode for connecting to h264-streamer MJPEG streams
- MJPEG→H.264 server-side encoding with hardware encoder auto-detection (VAAPI, V4L2M2M, NVENC, QSV, libx264)
- FLV streaming endpoint (`/flv`) compatible with Anycubic slicer
- FLV proxy announcement to h264-streamer for offloading H.264 encoding from the printer
- FlvMuxer for AVCC H.264 to FLV container conversion
- FfmpegEncoderDetector with real test-frame verification (catches non-functional encoders like h264_v4l2m2m on Pi 5)
- Video source configuration in CLI (h264/mjpeg modes, h264-streamer integration settings)
- Printer pre-flight connectivity checker
- Embedded resources for status display (DejaVu font, fb_status binary)
- GPU passthrough Docker Compose files (VAAPI, NVIDIA, Raspberry Pi V4L2M2M)
- H.264 encoding settings in CLI (encoder, bitrate, rate control, GOP, preset, profile)

#### Fixed
- H.264 encoder fallback from non-functional hardware encoders (e.g. h264_v4l2m2m on Pi 5) to libx264
- Silent encoder failures now properly logged and trigger encoder fallback
- FFmpeg library initialization in MJPEG→H.264 encoding path (was missing `FfmpegDecoder.Initialize()`)
- Annex B extradata parsing for libx264 encoder (was only handling AVCC format from hardware encoders)
- FLV decoder config race condition - video data now waits for SPS/PPS before streaming
- FLV client connection leak - stale clients now detected via TCP socket polling
- Obico client connection issues when disabled
- FFmpeg crash on invalid streams

#### Changed
- Improved CLI prompts for printer configuration

### [1.4.4] - 2026-01-28

#### Added
- Initial public release
- Multi-format streaming (MJPEG, H.264 WebSocket, HLS, LL-HLS)
- Obico integration with dual server support (local + cloud)
- Multi-printer support with per-printer configuration
- LED management with auto-control and standby timeout
- BedMesh calibration and analysis tools
- Home Assistant integration (REST API)
- Docker support with multi-arch images
- Encrypted configuration (AES-256-GCM)
