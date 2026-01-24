#!/bin/bash
# Monitor MQTT messages from ACProxyCam daemon logs
# Usage: ./monitor-mqtt.sh [lines]
#
# This script monitors MQTT messages logged by the ACProxyCam daemon.
# Requires verbose MQTT logging to be enabled in PrinterThread.cs:
#   _mqttController.MessageReceived += (s, msg) => LogStatus($"MQTT MSG: {msg}");
#
# The daemon subscribes to all MQTT topics (#) on the printer's broker,
# so this captures all messages including those from the Anycubic cloud/slicer.

LINES=${1:-100}

echo "=== ACProxyCam MQTT Message Monitor ==="
echo "Showing last $LINES MQTT messages..."
echo "Press Ctrl+C to stop following"
echo ""

# Show recent messages then follow
sudo journalctl -u acproxycam -n "$LINES" --no-pager -f | grep --line-buffered "MQTT MSG"
