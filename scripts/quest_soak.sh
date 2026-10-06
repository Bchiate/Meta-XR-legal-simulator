#!/usr/bin/env bash
# Soak test capture: record the Quest's VrApi + Unity logcat and the skin/SoC/battery sensors while Brett plays, then print
# the thermal report. Read-only on the headset (logcat + dumpsys). Play on battery for a fair test (charging adds heat).
#   scripts/quest_soak.sh [minutes=45] [label]    → Logs/soak-<label|time>.log, report at the end (Ctrl-C also reports)
#   scripts/quest_thermal_report.py Logs/soak-....log [--compare Logs/device-0.1.15-play2.log]   (re-read later)
set -uo pipefail
source "$(dirname "$0")/env.sh"
MIN="${1:-45}"; LABEL="${2:-$(date +%Y%m%d-%H%M)}"
OUT="$HIS_LOGS/soak-$LABEL.log"
$ADB get-state >/dev/null 2>&1 || { echo "[soak] no headset on adb"; exit 1; }
echo "[soak] recording $MIN min to $OUT (Ctrl-C to stop early)"
: > "$OUT"
$ADB logcat -v time -T 1 VrApi:I Unity:I '*:S' >> "$OUT" &
LOGCAT=$!

sensors() {
  local dump batt
  dump=$($ADB shell dumpsys thermalservice 2>/dev/null)
  batt=$($ADB shell dumpsys battery 2>/dev/null)
  # "Current temperatures from HAL" section: skin (surf-virt-usr), SoC, battery sensors.
  local line
  line=$(printf '%s\n' "$dump" | awk '/Current temperatures from HAL/{f=1;next} /^[A-Z]/{f=0} f' |
    sed -nE 's/.*mValue=([-0-9.]+), mType=[0-9]+, mName=(surf-virt-usr|soc-usr|batt-virt-usr|gpuss-0),.*/\2=\1/p' | tr '\n' ' ')
  local tstat plugged
  tstat=$(printf '%s\n' "$dump" | sed -n 's/^Thermal Status: \([0-9]*\).*/\1/p' | head -1)
  plugged=$(printf '%s\n' "$batt" | grep -E "(AC|USB|Wireless) powered: true" | head -1 | awk '{print $1}')
  local row="$(date '+%m-%d %H:%M:%S').000 [soak] ${line}status=${tstat:-?} plugged=${plugged:-no}"
  echo "$row" >> "$OUT"; echo "  $row"
}

finish() {
  kill "$LOGCAT" 2>/dev/null; wait "$LOGCAT" 2>/dev/null
  echo
  python3 "$(dirname "$0")/quest_thermal_report.py" "$OUT" --every 5
  exit 0
}
trap finish INT TERM

END=$(( $(date +%s) + MIN * 60 ))
while [ "$(date +%s)" -lt "$END" ]; do
  sensors
  sleep 30
done
finish
