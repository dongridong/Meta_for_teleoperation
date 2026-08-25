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
  local out="$1" product="$2" appid="$3" handviz="$4"
  echo ">>> building $product ($appid, handVisualization=$handviz) -> $out"
  "$UNITY" -quit -batchmode -nographics \
    -projectPath "$PROJECT" \
    -buildTarget Android \
    -executeMethod BuildAndroid.Build \
    -output "$out" \
    -productName "$product" \
    -applicationIdentifier "$appid" \
    -handVisualization "$handviz" \
    -logFile -
}

# productName is what the headset shows under the app icon, so it carries spaces;
# the APK file names stay underscored to keep them shell-friendly. The hands
# identity matches committed ProjectSettings, so passing it explicitly is
# redundant but keeps both invocations symmetric and self-documenting — the same
# reason handVisualization=on is spelled out below even though it is the default.
#
# handVisualization is the one FUNCTIONAL difference between the two APKs: the
# controller build does not draw the hand skeleton, so putting a controller down
# no longer makes hands appear. Both APKs publish identical data over TCP.
build "$OUT/Meta_with_hands.apk"       "Meta with hands"      com.dongridong.metahand       on
build "$OUT/Meta_with_controllers.apk" "Meta with controller" com.dongridong.metacontroller off

echo ">>> done. APKs in $OUT"
