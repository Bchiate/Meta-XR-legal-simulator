#!/usr/bin/env bash
# Tail Unity logs from the headset for N seconds (default 30). Usage: scripts/logs.sh [seconds] [extra logcat filters...]
set -uo pipefail
source "$(dirname "$0")/env.sh"
SECS="${1:-30}"; shift || true
$ADB logcat -s Unity:V AndroidRuntime:E OpenXR:W "$@" & PID=$!
sleep "$SECS"; kill $PID 2>/dev/null; wait $PID 2>/dev/null; exit 0
