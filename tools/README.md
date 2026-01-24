# ACProxyCam Tools

Debugging and monitoring scripts for ACProxyCam development.

## Scripts

### monitor-mqtt.sh

Monitors MQTT messages from the ACProxyCam daemon logs.

```bash
# Copy to server and run
scp tools/monitor-mqtt.sh claude_test@192.168.178.12:/tmp/
ssh claude_test@192.168.178.12 "chmod +x /tmp/monitor-mqtt.sh && /tmp/monitor-mqtt.sh"

# Or run directly via SSH
ssh claude_test@192.168.178.12 "sudo journalctl -u acproxycam -f | grep --line-buffered 'MQTT MSG'"
```

**Prerequisites**: Requires verbose MQTT logging enabled in `PrinterThread.cs`:
```csharp
_mqttController.MessageReceived += (s, msg) => LogStatus($"MQTT MSG: {msg}");
```

**What it captures**:
- All MQTT messages on the printer's broker (subscribed to `#`)
- Messages from Anycubic cloud/slicer
- Camera start/stop commands
- Light control commands
- Printer state reports

### monitor-api.sh

Captures HTTP traffic to the Anycubic native API port (18086).

```bash
# Copy to server and run
scp tools/monitor-api.sh claude_test@192.168.178.12:/tmp/
ssh claude_test@192.168.178.12 "chmod +x /tmp/monitor-api.sh && /tmp/monitor-api.sh 192.168.178.43 300"

# Or run directly via SSH
ssh claude_test@192.168.178.12 "sudo tcpdump -i any host 192.168.178.43 and port 18086 -A -s 0"
```

**What it captures**:
- Local API calls to the printer's native firmware
- HTTP requests/responses on port 18086

**Note**: The Anycubic Slicer typically communicates via the cloud, not directly to this port.

## Anycubic Printer Ports

| Port | Service | Description |
|------|---------|-------------|
| 22 | SSH | Remote access (root/rockchip) |
| 9883 | MQTT (TLS) | Printer communication broker |
| 18086 | HTTP API | Native firmware API |
| 18088 | HTTP | Camera FLV stream |
| 18910 | HTTP | GCode file upload |
