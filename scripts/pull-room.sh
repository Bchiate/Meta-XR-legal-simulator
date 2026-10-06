#!/usr/bin/env bash
# Pull the real room a development build saved on the Quest (persistentDataPath/room.json) into the project, so the Editor
# can load Brett's actual room/door (MainSceneBuilder picks it up; HIS > Editor Room > Brett's Room forces it). Usage: scripts/pull-room.sh
set -euo pipefail
source "$(dirname "$0")/env.sh"
DEST="$HIS_PROJECT/Assets/_Game/Rooms/BrettRoom.json"
mkdir -p "$(dirname "$DEST")"
$ADB pull "/sdcard/Android/data/$HIS_PACKAGE/files/room.json" "$DEST"
echo "[pull-room] saved $(wc -c < "$DEST") bytes to $DEST — run Assets > Refresh, then HIS > Build Main Scene"
