#!/usr/bin/env bash
# Run one editor method headless (compile + execute + quit). Usage: scripts/unity-batch.sh Class.Method [extra args]
# Call it directly: it queues itself (one Unity per lane, first come first served — env.sh his_lock; Play Mode suites
# ScenarioTests.*Headless and JudgeSelfTest.Run may also run in test lane 2, a copy of the project: LANE=main|2 forces one)
# and pauses only while the Mac is genuinely hot (his_wait_healthy). Don't wrap it in your own wait/lock script.
# Each run logs to its own file (printed at the end); Logs/batch.log links to the latest run — with other runs queued,
# read the printed path, not Logs/batch.log.
# Refuses to run while the GUI Editor owns the project (only one Editor may open it).
set -uo pipefail
export HIS_NO_ADB=1
source "$(dirname "$0")/env.sh"
# Anchored (argv[0] = the Unity binary): the pgrep's own command line, or a shell mentioning the text, must not match.
if pgrep -f "^[^ ]*/Unity\.app/Contents/MacOS/Unity -project[Pp]ath" >/dev/null; then echo "[batch] GUI Editor is open — stop it first (scripts/editor.sh stop)"; exit 2; fi
METHOD="$1"; shift
# Lane 2 only for runs that write nothing into Assets (env.sh "Two test lanes"); everything else stays in main.
case "$METHOD" in ScenarioTests.*Headless|JudgeSelfTest.Run) export HIS_LANES="main 2";; *) export HIS_LANES="main";; esac
case "${LANE:-}" in main) export HIS_LANES="main";; 2) export HIS_LANES="2";; esac
[ -n "${PROJECT:-}" ] && export HIS_LANES="ext"      # PROJECT=<another checkout with a Library>: run there (env.sh lane "ext")
his_lock "$METHOD"
[ "${HIS_LANE:-main}" = 2 ] && his_lane_sync_in     # lane 2 tests main's code as it is now (ext tests its own)
his_wait_healthy
his_cooldown   # gentle cycling: a 30 s gap between back-to-back heavy runs
mkdir -p "$HIS_LOGS/runs"
NAME="$(date +%m%d-%H%M%S)-$METHOD.log"
# Lane 2 logs inside its own folder (linked from main's Logs/runs): its Unity command line must not name main's path, or
# a queue waiter still running the old env.sh (it matches any batch Unity mentioning main) would wait for lane 2 too.
if [ "${HIS_LANE:-main}" = main ]; then LOG="$HIS_LOGS/runs/$NAME"
else mkdir -p "$HIS_PROJECT/Logs/runs"; LOG="$HIS_PROJECT/Logs/runs/$NAME"; ln -sfn "$LOG" "$HIS_LOGS/runs/$NAME"; fi
ln -sfn "runs/$NAME" "$HIS_LOGS/batch.log"
echo "[batch] $METHOD running in lane ${HIS_LANE:-main} (log: $HIS_LOGS/runs/$NAME)"
# Play Mode test entries (*Headless: ScenarioTests/PoseTests/ThermalTests) quit themselves (EditorApplication.Exit + a
# watchdog); -quit would end them before Play Mode — a wasted slot with no summary line. So they skip -quit unless NOQUIT=0.
QUIT=-quit
case "${NOQUIT:-auto}" in 1) QUIT="";; auto) case "$METHOD" in *Headless) QUIT="";; esac;; esac
# GRAPHICS=1 keeps the GPU (offscreen rendering, e.g. PoseSnapshots.Render); default stays -nographics.
GFX=-nographics; [ "${GRAPHICS:-0}" = 1 ] && GFX=""
$HIS_NICE "$UNITY_EDITOR" -batchmode $GFX $QUIT -projectPath "$HIS_PROJECT" -executeMethod "$METHOD" "$@" -logFile "$LOG"
RC=$?
[ "${HIS_LANE:-main}" = 2 ] && his_lane_sync_out    # its renders/metrics land in main's Logs (ext keeps its own Logs)
his_mark_end
his_unlock     # the next queued run can start its cool-down now
ls -t "$HIS_LOGS/runs" | tail -n +41 | while read -r f; do rm -f "$HIS_LOGS/runs/$f"; done
[ -d "$HIS_LANE2/Logs/runs" ] && ls -t "$HIS_LANE2/Logs/runs" | tail -n +41 | while read -r f; do rm -f "$HIS_LANE2/Logs/runs/$f"; done
grep -aE "error CS" "$LOG" | sort -u | head -30      # -a: a stray NUL in a Unity log otherwise prints "Binary file … matches"
grep -aE "^\[HIS\]" "$LOG" | tail -30
grep -aE "Exception" "$LOG" | grep -av "^  at\|UnityEngine.Debug" | head -10
echo "[batch] $METHOD exit $RC (lane ${HIS_LANE:-main}, log: $HIS_LOGS/runs/$NAME)"
exit $RC
