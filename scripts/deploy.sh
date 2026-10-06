#!/usr/bin/env bash
# Install + launch the last APK on the connected Quest and tail Unity logs. Usage: scripts/deploy.sh [--no-log]
set -euo pipefail
source "$(dirname "$0")/env.sh"
$ADB devices | grep -qE "device$" || { echo "[deploy] No Quest connected (adb devices). Plug in via USB and accept the debugging prompt in the headset."; exit 1; }
echo "[deploy] Installing $HIS_APK"
$ADB install -r -g "$HIS_APK"
$ADB shell am force-stop "$HIS_PACKAGE" || true
$ADB logcat -c
$ADB shell monkey -p "$HIS_PACKAGE" -c android.intent.category.LAUNCHER 1 >/dev/null
echo "[deploy] Launched $HIS_PACKAGE"
[[ "${1:-}" == "--no-log" ]] && exit 0
echo "[deploy] Tailing Unity logcat for 20s..."
"$(dirname "$0")/logs.sh" 20
