# Shared paths for the HIS build/deploy scripts. `source scripts/env.sh`
export HIS_PROJECT="${HIS_PROJECT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)}"   # the project root (this file is in scripts/)
export UNITY_EDITOR="${HIS_UNITY_EDITOR:-/Applications/Unity/Hub/Editor/6000.6.0f1/Unity.app/Contents/MacOS/Unity}"   # HIS_UNITY_EDITOR: test stub
export ANDROID_PLAYER="/Applications/Unity/Hub/Editor/6000.6.0f1/PlaybackEngines/AndroidPlayer"
export ADB_BIN="$ANDROID_PLAYER/SDK/platform-tools/adb"
# Prefer the Wi-Fi connection (adb tcpip) when it is up, else whatever USB device is attached. HIS_NO_ADB=1 skips the
# probe (unity-batch.sh / build.sh don't need the headset, and `adb devices` can stall starting its server).
export ADB="$ADB_BIN"
if [ "${HIS_NO_ADB:-0}" != 1 ]; then
  _wifi=$("$ADB_BIN" devices 2>/dev/null | awk '$2=="device" && $1 ~ /:5555$/ {print $1}' | head -1)
  [ -n "$_wifi" ] && export ADB="$ADB_BIN -s $_wifi"
fi
export HIS_PACKAGE="com.futureclarity.homeinvasionsimulator"
export HIS_APK="$HIS_PROJECT/Builds/HomeInvasionSimulator.apk"
export HIS_LOGS="${HIS_TEST_LOGS:-$HIS_PROJECT/Logs}"   # HIS_TEST_LOGS: sandbox for script tests
# Heat/longevity (Brett: protect the Mac, avoid abrupt kills and aggressive on/off cycling — but don't be so conservative
# the Mac can't work normally within safe temps):
#   default   → full speed (the Mac is built for it; scripts/machealth.sh alerts on genuinely unhealthy temps).
#   .lowheat  → `taskpolicy -b` (darwin background: efficiency cores, lowest heat) — overnight / unattended.
#   .gentle   → `taskpolicy -c utility` (softer peaks, a bit slower).
if [ -f "$HIS_PROJECT/.lowheat" ]; then export HIS_NICE="taskpolicy -b"
elif [ -f "$HIS_PROJECT/.gentle" ]; then export HIS_NICE="taskpolicy -c utility"
else export HIS_NICE=""; fi
# Cool-down between back-to-back heavy runs (no tight on/off thermal cycling): wait until >= 10 s after the last Unity run
# ended; wait longer only while the CPU die is above 92 C (max 2 min). Brett 2026-10-05: "im cool with 95 cap" (was 30 s / 85 C /
# 3 min: up to 3 min idle before every run at a normal 85-87 C test load). Uses scripts/bin/mactemps when available.
his_cooldown() {
  local mark="$HIS_LOGS/.last-unity-end" now last cpu i
  [ -f "$mark" ] || return 0
  for i in $(seq 1 24); do
    now=$(date +%s); last=$(cat "$mark" 2>/dev/null || echo 0)
    cpu=$("$HIS_PROJECT/scripts/bin/mactemps" 2>/dev/null | sed -n 's/.*cpu=\([0-9]*\).*/\1/p') || cpu=""   # (set -e: no sensor ≠ abort)
    [ $((now - last)) -ge 10 ] && [ "${cpu:-0}" -lt 92 ] && return 0
    sleep 5
  done
}
his_mark_end() { date +%s > "$HIS_LOGS/.last-unity-end"; }

# ---- No unnecessary slowdowns (Brett 2026-09-24: "ensure nothing's slowing it down unnecessarily again") ----
# unity-batch.sh and build.sh queue here themselves: one Unity at a time on this project, first come first served, and
# they pause only while the Mac is genuinely hot. Callers (agents included) run them directly — no private wait/lock
# wrappers, no load-average gates, no multi-minute sleeps. scripts/machealth.sh alerts when queued work sits idle.
export HIS_QUEUE="$HIS_LOGS/.unity-queue"      # tickets "<epoch.µs>-<pid>" holding a label
# Anchored: argv[0] must be the Unity binary itself — a shell or monitor loop whose command text merely mentions
# "Unity.app/Contents/MacOS/Unity … -batchmode … <project>" must not hold the queue (the self-matching pgrep bug class).
HIS_UNITY_PATTERN="${HIS_UNITY_PATTERN:-^[^ ]*/Unity\.app/Contents/MacOS/Unity .*-batchmode.*$HIS_PROJECT}"
# ---- Two test lanes (Brett 2026-10-05: "you can let the laptop heat up a bit more if its safe im cool with 95 cap") ----
# Lane "main" = this project. Lane "2" = a full copy outside the repo with its own Library (scripts/lane2-setup.sh), so two
# Unity runs can go at once. A lane-2 run first rsyncs Assets/ Packages/ ProjectSettings/ CREDITS.md from main (it always
# tests the code as it is now) and copies its Logs outputs (poses/, security/, doorblood.png, clip-*.txt) back after.
# unity-batch.sh lets only ScenarioTests.*Headless and JudgeSelfTest.Run use lane 2 (they write nothing into Assets);
# builds, SfxLibraryBuilder, MainSceneBuilder, SecurityTests and other asset writers run in main. LANE=main|2 forces one.
export HIS_MAIN="$HIS_PROJECT"
export HIS_LANE2="${HIS_LANE2:-$(dirname "$HIS_PROJECT")/his-lane2}"   # optional second test lane: a copy of the project next to it
# Lane "ext" (Brett 2026-10-05 "can we speed it up macs pretty cold"): another checkout with its own Library — e.g. the
# cars worktree ../his-cars-wt — named by PROJECT=<path> on unity-batch.sh. Its own lock, no syncing: it tests its own tree.
export HIS_EXT="${PROJECT:-}"
his_lane_dir() { case "$1" in 2) echo "$HIS_LANE2";; ext) echo "$HIS_EXT";; *) echo "$HIS_MAIN";; esac; }
his_lane_busy() { # $1 = lane. Busy: its lock's holder is alive, or a batch Unity runs on its project (anchored on -projectPath).
  local d="$HIS_LOGS/.lane-$1$( [ "$1" = ext ] && printf %s "$HIS_EXT" | cksum | cut -d" " -f1).lock" pid
  if [ -d "$d" ]; then
    pid=$(cat "$d/pid" 2>/dev/null)
    if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then return 0; fi
    rm -rf "$d"                                           # a dead holder's lock
  fi
  pgrep -f "^[^ ]*/Unity\.app/Contents/MacOS/Unity .*-projectPath $(his_lane_dir "$1") " >/dev/null
}
his_lock() { # $1 = label shown to the waiters; HIS_LANES = the lanes this run may use ("main" unless the caller says).
  # FIFO per lane: a ticket takes a free lane it may use when no live ticket ahead of it may use that lane. Sets HIS_LANE,
  # HIS_PROJECT (the lane's folder), HIS_HELD. Released on exit (his_unlock).
  # Re-entrant: a script that holds the queue and then calls unity-batch.sh / build.sh must not queue behind itself.
  if [ -n "${HIS_HELD:-}" ] && [ -e "$HIS_HELD" ]; then return 0; fi
  # Call from a top-level script process: in ( ) / & subshells $$ is the parent's pid (ticket names would collide).
  mkdir -p "$HIS_QUEUE"
  local mine="${HIS_LANES:-main}"
  [ -n "$HIS_EXT" ] && mine="ext"                         # PROJECT=<path>: that checkout only
  [ "$mine" = ext ] || [ -d "$HIS_LANE2/Library" ] || mine="main"   # no lane 2 set up yet: main only
  HIS_TICKET="$HIS_QUEUE/$(perl -MTime::HiRes=time -e 'printf "%.6f", time')-$$"
  printf '%s\nlanes=%s\n' "${1:-?}" "$mine" > "$HIS_TICKET"; trap 'his_unlock' EXIT
  local f t tl lane ok waited=0
  while true; do
    for f in "$HIS_QUEUE"/*-*; do
      if [ -e "$f" ] && ! kill -0 "${f##*-}" 2>/dev/null; then rm -f "$f"; fi
    done
    for lane in $mine; do
      his_lane_busy "$lane" && continue
      ok=1
      for t in $(ls "$HIS_QUEUE" | sort); do
        [ "$HIS_QUEUE/$t" = "$HIS_TICKET" ] && break
        grep -q '^held=' "$HIS_QUEUE/$t" 2>/dev/null && continue    # running already (in its own lane): not competing
        tl=$(sed -n 's/^lanes=//p' "$HIS_QUEUE/$t" 2>/dev/null); [ -n "$tl" ] || tl=main   # (an old-style ticket: main)
        case " $tl " in *" $lane "*) ok=0; break;; esac
      done
      [ $ok = 1 ] || continue
      local lk="$HIS_LOGS/.lane-$lane$( [ "$lane" = ext ] && printf %s "$HIS_EXT" | cksum | cut -d" " -f1).lock"
      if mkdir "$lk" 2>/dev/null; then
        echo $$ > "$lk/pid"
        echo "held=$lane" >> "$HIS_TICKET"
        export HIS_LANE="$lane" HIS_LANE_LOCK="$lk" HIS_PROJECT="$(his_lane_dir "$lane")" HIS_HELD="$HIS_TICKET"
        return 0
      fi
    done
    if [ $((waited % 60)) = 0 ]; then
      echo "[queue] ${1:-?} (lanes: $mine): $(ls "$HIS_QUEUE" | sort | awk -v me="${HIS_TICKET##*/}" '$0==me{print NR-1; exit}') ahead, running: $(for t in $(ls "$HIS_QUEUE" | sort); do [ "$HIS_QUEUE/$t" = "$HIS_TICKET" ] && break; head -1 "$HIS_QUEUE/$t" 2>/dev/null; done | head -2 | tr '\n' ' ')"
    fi
    sleep 2; waited=$((waited + 2))
  done
}
his_unlock() { rm -f "${HIS_TICKET:-/nonexistent}"; if [ -n "${HIS_LANE_LOCK:-}" ]; then rm -rf "$HIS_LANE_LOCK"; HIS_LANE_LOCK=""; fi; }
# Lane 2 sees the code as it is now, and its outputs land where everyone reads them.
his_lane_sync_in() {
  local f
  for f in Assets Packages ProjectSettings; do rsync -a --delete "$HIS_MAIN/$f/" "$HIS_PROJECT/$f/"; done
  [ -f "$HIS_MAIN/CREDITS.md" ] && cp -p "$HIS_MAIN/CREDITS.md" "$HIS_PROJECT/CREDITS.md"
  mkdir -p "$HIS_PROJECT/Logs/poses" "$HIS_PROJECT/Logs/security"
}
his_lane_sync_out() {
  local f
  rsync -a "$HIS_PROJECT/Logs/poses/" "$HIS_MAIN/Logs/poses/" 2>/dev/null
  rsync -a "$HIS_PROJECT/Logs/security/" "$HIS_MAIN/Logs/security/" 2>/dev/null
  for f in doorblood.png clip-trace.txt clip-metrics.txt; do [ -f "$HIS_PROJECT/Logs/$f" ] && cp -p "$HIS_PROJECT/Logs/$f" "$HIS_MAIN/Logs/$f"; done
  return 0
}
# Genuinely unhealthy = the same limits as scripts/machealth.sh (never load). Prints the reason, or nothing when fine.
# The die: a new run waits above 93 C (Brett 2026-10-05: a 95 C cap; was 90 C).
his_health_bad() {
  local mt cpu bat lim warn
  mt=$("$HIS_PROJECT/scripts/bin/mactemps" 2>/dev/null) || mt=""   # never abort a `set -e` caller (build.sh)
  cpu=$(echo "$mt" | sed -n 's/.*cpu=\([0-9.]*\).*/\1/p')
  # Battery = the pack sensor machealth.sh reads (ioreg). mactemps' "battery" is the hottest battery/gas-gauge sensor and
  # reads ~3.5 C higher, which made this gate pause ~3.5 C before machealth's own 40 C limit.
  bat=$(ioreg -rn AppleSmartBattery 2>/dev/null | awk -F'= ' '/"Temperature" =/{printf "%.1f", $2/100; exit}') || true  # awk exit → SIGPIPE
  [ -n "$bat" ] || bat=$(echo "$mt" | sed -n 's/.*battery=\([0-9.]*\).*/\1/p')
  lim=$(pmset -g therm 2>/dev/null | awk -F= '/CPU_Speed_Limit/{gsub(/ /,"",$2); print $2}') || true
  warn=$(pmset -g therm 2>/dev/null | grep -c -E "thermal warning level.*[1-9]|Thermal.*level *= *[1-9]") || true
  awk -v c="${cpu:-0}" -v b="${bat:-0}" -v l="${lim:-100}" -v w="${warn:-0}" 'BEGIN{
    if (l < 100) print "CPU speed-limited to " l "%"; else if (w > 0) print "macOS thermal warning"; else if (c > 93) print "CPU/GPU die " c " C"; else if (b > 40) print "battery " b " C" }'
}
his_wait_healthy() { local r; while r=$(his_health_bad); [ -n "$r" ]; do echo "[health] $r — pausing, re-check in 60 s"; sleep 60; done; }
