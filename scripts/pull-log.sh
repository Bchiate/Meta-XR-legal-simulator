#!/usr/bin/env bash
# Pull the game's own log files from the headset (Core/LogFile.cs): his.log = the current/last run, his.1.log, his.2.log older.
# Usage: scripts/pull-log.sh [dest dir, default Logs/headset]
set -uo pipefail
source "$(dirname "$0")/env.sh"
DEST="${1:-$(dirname "$0")/../Logs/headset}"; mkdir -p "$DEST"
SRC=/sdcard/Android/data/com.futureclarity.homeinvasionsimulator/files
for f in his.log his.1.log his.2.log; do $ADB pull "$SRC/$f" "$DEST/$f" >/dev/null 2>&1 && echo "$DEST/$f ($(wc -l < "$DEST/$f" | tr -d " ") lines)"; done
exit 0
