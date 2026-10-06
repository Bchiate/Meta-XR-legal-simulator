#!/usr/bin/env bash
# Build the Quest APK headless. Usage: scripts/build.sh [-dev] | scripts/build.sh --finish
# Queues with the batch runs (env.sh his_lock: one Unity at a time, first come first served) and pauses only while the
# Mac is genuinely hot — call it directly.
set -euo pipefail
export HIS_NO_ADB=1
source "$(dirname "$0")/env.sh"
mkdir -p "$HIS_LOGS"
LOG="$HIS_LOGS/build.log"
GRADLE_DIR="$HIS_PROJECT/Library/Bee/Android/Prj/IL2CPP/Gradle"

# Unity 6000.6's Gradle step intermittently dies in :launcher:packageDebug (PackageAndroidArtifact$IncrementalSplitterRunnable)
# after a complete export; rerunning that one task directly succeeds. The export is already stamped (versionCode/name), so
# the APK it produces is the real build. A failed build also leaves Unity's XR build-time preloads in ProjectSettings, which a
# successful build removes itself: drop just those lines.
gradle_retry() {
  grep -q "IncrementalSplitterRunnable" "$LOG" || return 1
  echo "[build] Gradle packageDebug glitch — rerunning it directly"
  local jdk="$ANDROID_PLAYER/OpenJDK"
  (cd "$GRADLE_DIR" && JAVA_HOME="$jdk" $HIS_NICE "$jdk/bin/java" -classpath "$ANDROID_PLAYER/Tools/gradle/lib/gradle-launcher-9.1.0.jar" \
     org.gradle.launcher.GradleMain :launcher:packageDebug --offline -q) >> "$LOG" 2>&1 || return 1
  cp "$GRADLE_DIR/launcher/build/outputs/apk/debug/launcher-debug.apk" "$HIS_APK"
  (cd "$GRADLE_DIR" && JAVA_HOME="$jdk" "$jdk/bin/java" -classpath "$ANDROID_PLAYER/Tools/gradle/lib/gradle-launcher-9.1.0.jar" \
     org.gradle.launcher.GradleMain --stop -q) >/dev/null 2>&1 || true   # don't leave a 1 GB daemon idling
  python3 - "$HIS_PROJECT/ProjectSettings/ProjectSettings.asset" <<'EOF' || true
import re, subprocess, sys
p = sys.argv[1]
head = subprocess.run(["git", "-C", p.rsplit("/ProjectSettings/", 1)[0], "show", "HEAD:ProjectSettings/ProjectSettings.asset"],
                      capture_output=True, text=True).stdout
def preloads(s):
    m = re.search(r"  preloadedAssets:\n((?:  - .*\n)*)", s); return m.group(1) if m else None
cur = open(p).read(); keep = preloads(head)
if keep is not None and preloads(cur) is not None: open(p, "w").write(cur.replace(preloads(cur), keep, 1))
EOF
  echo "[HIS] Build Succeeded (Gradle rerun)"
}

# scripts/build.sh --finish: only redo the Gradle step of the last (glitched) build.
his_lock "build ${1:-}"
his_wait_healthy
if [ "${1:-}" = "--finish" ]; then his_cooldown; rc=0; { gradle_retry && ls -lh "$HIS_APK"; } || rc=$?; his_mark_end; exit $rc; fi

his_cooldown
echo "[build] Unity batch build -> $HIS_APK (log: $LOG)"
# The player build's IL2CPP/clang burst hit a 96.5 C die at full speed (2026-10-05) while tests peak ~66 C: builds run with
# softer peaks (utility QoS) unless a speed mode file says otherwise; tests stay at full speed.
${HIS_NICE:-taskpolicy -c utility} "$UNITY_EDITOR" -batchmode -nographics -quit -projectPath "$HIS_PROJECT" \
  -buildTarget Android -executeMethod BuildScript.BuildQuest -outputPath "$HIS_APK" "$@" \
  -logFile "$LOG" || gradle_retry || { his_mark_end; echo "[build] FAILED — errors:"; grep -E "error CS|\[HIS\].*Error|Exception|Build Failed" "$LOG" | head -40; exit 1; }
his_mark_end
his_unlock
grep -E "\[HIS\] Build" "$LOG" || true
ls -lh "$HIS_APK"
