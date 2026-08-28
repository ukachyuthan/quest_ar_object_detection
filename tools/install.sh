#!/usr/bin/env bash
# Build the debug APK and install it on the attached Quest.
set -euo pipefail
cd "$(dirname "$0")/.."
source tools/env.sh

APK=app/build/outputs/apk/debug/app-debug.apk

./gradlew :app:assembleDebug

if [ -z "$(adb devices | sed '1d' | grep -w device || true)" ]; then
  echo
  echo "No device in 'adb devices'."
  echo "  1. Quest: Settings > System > Developer > USB Debugging (needs a developer account)."
  echo "  2. Plug in over USB and accept the 'Allow USB debugging' prompt inside the headset."
  echo "  3. Re-run this script."
  exit 1
fi

echo "Installing $(du -h "$APK" | cut -f1) ..."
adb install -r "$APK"

echo
echo "Installed. On the Quest it appears under Library > Unknown Sources > Quest Object Detect."
echo "Follow the logs with: tools/logcat.sh"
