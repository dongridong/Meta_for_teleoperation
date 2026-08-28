#!/bin/bash
# Build both headset APKs from THIS single source tree, injecting each app's
# identity at build time so ProjectSettings.asset is never edited in the working
# tree. See Assets/Editor/BuildAndroid.cs.
#
#   UNITY=/path/to/Unity ./Tools/build_all.sh
#
# Requires Unity 6000.2.12f1 (this project's editor version) with Android Build
# Support. Set UNITY to the Unity executable, or export it in your shell.
set -euo pipefail

PROJECT="$(cd "$(dirname "$0")/.." && pwd)"
UNITY="${UNITY:?set UNITY to the Unity 6000.2.12f1 executable, e.g. UNITY=~/Unity/Hub/Editor/6000.2.12f1/Editor/Unity}"
OUT="$PROJECT/Build"

build() {
  local out="$1" product="$2" appid="$3" handviz="$4" videomode="$5"
  echo ">>> building $product ($appid, handVisualization=$handviz, videoMode=$videomode) -> $out"
  "$UNITY" -quit -batchmode -nographics \
    -projectPath "$PROJECT" \
    -buildTarget Android \
    -executeMethod BuildAndroid.Build \
    -output "$out" \
    -productName "$product" \
    -applicationIdentifier "$appid" \
    -handVisualization "$handviz" \
    -videoMode "$videomode" \
    -logFile -
}

# productName is what the headset shows under the app icon, so it carries spaces;
# the APK file names stay underscored to keep them shell-friendly. The hands
# identity matches committed ProjectSettings, so passing it explicitly is
# redundant but keeps both invocations symmetric and self-documenting — the same
# reason handVisualization=on is spelled out below even though it is the default.
#
# handVisualization is the one FUNCTIONAL difference between the first two APKs:
# the controller build does not draw the hand skeleton, so putting a controller
# down no longer makes hands appear.
#
# The third is the teleoperation build: instead of passthrough it shows the ZED X
# Mini's stereo stream from the robot, received over WebRTC/WHEP and drawn on a
# world-locked panel at 1:1 angular size. It needs the Jetson serving that stream
# (`./zedx_webrtc.sh --xr`) and the headset on the same LAN.
#
# ALL THREE publish identical data over TCP — HMD, hands, controllers and the
# IOBT body skeleton. The ZED build publishes it while the video is running; the
# video path and the publisher share nothing but the frame clock.
build "$OUT/Meta_with_hands.apk"       "Meta with hands"      com.dongridong.metahand       on  off
build "$OUT/Meta_with_controllers.apk" "Meta with controller" com.dongridong.metacontroller off off
build "$OUT/Meta_with_zed.apk"         "Meta with ZED"        com.dongridong.metazed        off zed

echo ">>> done. APKs in $OUT"
