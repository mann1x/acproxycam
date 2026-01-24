#!/bin/bash
# Monitor Anycubic native API traffic on port 18086
# Usage: ./monitor-api.sh [printer_ip] [duration_seconds]
#
# This script uses tcpdump to capture HTTP API traffic to the printer's
# native API port (18086). This is used by the Anycubic firmware for
# print control commands.
#
# Note: Traffic from the Anycubic Slicer typically goes through the cloud,
# not directly to this port. This is useful for capturing local API calls.

PRINTER_IP=${1:-192.168.178.43}
DURATION=${2:-300}

echo "=== Anycubic Native API Monitor ==="
echo "Printer IP: $PRINTER_IP"
echo "Duration: ${DURATION}s"
echo "Port: 18086"
echo ""
echo "Press Ctrl+C to stop"
echo ""

# Capture HTTP traffic on the native API port
# -A: Print packet contents in ASCII
# -s 0: Capture full packets (no truncation)
sudo timeout "$DURATION" tcpdump -i any host "$PRINTER_IP" and port 18086 -A -s 0
