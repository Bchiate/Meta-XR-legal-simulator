#!/usr/bin/env bash
# Start/stop the GUI Editor with the Meta XR Simulator runtime. Usage: scripts/editor.sh start|stop|status
set -uo pipefail
export HIS_NO_ADB=1   # no headset needed here; a cold `adb devices` probe costs ~3 s (starts the adb server) and can stall
source "$(dirname "$0")/env.sh"
SIM=/Applications/MetaXRSimulator.app/Contents/Resources/MetaXRSimulator/meta_openxr_simulator.json
case "${1:-status}" in
  start)
    pgrep -f "^[^ ]*/Unity\.app/Contents/MacOS/Unity -project[Pp]ath" >/dev/null && { echo "editor already running"; exit 0; }
    XR_RUNTIME_JSON="$SIM" nohup "$UNITY_EDITOR" -projectPath "$HIS_PROJECT" -logFile "$HIS_LOGS/editor-gui.log" >/dev/null 2>&1 &
    echo "editor starting (pid $!) — window must be visible on screen for Play Mode to tick";;
  stop)
    pkill -f "^[^ ]*/Unity\.app/Contents/MacOS/Unity -project[Pp]ath" && echo "stopped" || echo "not running";;
  status)
    P=$(pgrep -f "^[^ ]*/Unity\.app/Contents/MacOS/Unity -project[Pp]ath" | head -1); [ -n "$P" ] && ps -o pid,%cpu,stat,etime -p "$P" | tail -1 || echo "not running";;
esac
