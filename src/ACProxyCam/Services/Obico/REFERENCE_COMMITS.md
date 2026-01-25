# Obico Reference Implementation Commits

This file tracks the git commits of the reference implementations used when developing
ACProxyCam's Obico integration. When these upstream projects are updated, compare the
diffs from these commits to identify changes that may need to be ported to ACProxyCam.

## Last Updated: 2026-01-25

### moonraker-obico
- **Repository**: https://github.com/TheSpaghettiDetective/moonraker-obico
- **Commit**: `df0005c2f1a9137d3fbb44a5139caa9f8843ed92`
- **Date**: 2026-01-18
- **Message**: Bump version 2.1.4

### obico-server
- **Repository**: https://github.com/TheSpaghettiDetective/obico-server
- **Commit**: `9b73caa7b373e89fd23bf2fed646e629ee602640`
- **Date**: 2026-01-19
- **Message**: Check in built bundles

### rinkhals
- **Repository**: https://github.com/jbatonnet/Rinkhals
- **Commit**: `deab69a5208e1a88075ffb13b9433a86d46f93cc`
- **Date**: 2026-01-11
- **Message**: Bugfix: kobra.py UnboundLocalError

## How to Check for Updates

```bash
# Clone/update reference repos to D:\INSTALL\acproxycam\obico\
cd D:\INSTALL\acproxycam\obico

# For each repo, compare changes since the tracked commit:

# moonraker-obico
cd moonraker-obico
git fetch origin
git log df0005c2f1a9137d3fbb44a5139caa9f8843ed92..origin/main --oneline
git diff df0005c2f1a9137d3fbb44a5139caa9f8843ed92..origin/main

# obico-server
cd ../obico-server
git fetch origin
git log 9b73caa7b373e89fd23bf2fed646e629ee602640..origin/master --oneline
git diff 9b73caa7b373e89fd23bf2fed646e629ee602640..origin/master

# rinkhals
cd ../rinkhals
git fetch origin
git log deab69a5208e1a88075ffb13b9433a86d46f93cc..origin/main --oneline
git diff deab69a5208e1a88075ffb13b9433a86d46f93cc..origin/main
```

## Key Files to Watch

### moonraker-obico
- `moonraker_obico/app.py` - Main application logic, print state handling
- `moonraker_obico/moonraker_conn.py` - Moonraker API communication
- `moonraker_obico/server_conn.py` - Obico server WebSocket protocol
- `moonraker_obico/printer_state.py` - Printer state tracking

### obico-server
- `backend/api/octoprint_messages.py` - WebSocket message handling
- `backend/app/models/other_models.py` - Print tracking logic (update_current_print)
- `backend/api/consumers.py` - WebSocket consumers

### rinkhals
- `files/3-rinkhals/opt/rinkhals/` - Klipper/Moonraker integration for Anycubic printers

## After Updating ACProxyCam

When you've ported changes from upstream, update the commit hashes in this file to
reflect the new baseline for future comparisons.
