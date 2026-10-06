#!/usr/bin/env bash
# Grab what the headset is rendering. Usage: scripts/screenshot.sh [out.png]
set -euo pipefail
source "$(dirname "$0")/env.sh"
OUT="${1:-$HIS_PROJECT/Builds/quest-screenshot.png}"
$ADB exec-out screencap -p > "$OUT" && echo "[screenshot] $OUT"
