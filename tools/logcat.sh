#!/usr/bin/env bash
# Tail just this app's logs, including the camera enumeration dump.
set -euo pipefail
cd "$(dirname "$0")/.."
source tools/env.sh
adb logcat -c || true
adb logcat QuestObjectDetect:V PassthroughCamera:V AndroidRuntime:E "*:S"
