#!/usr/bin/env bash
# Creates a project, targets it at Android and exports it, the way a game's own pipeline would. Usage: android-smoke.sh <work folder>
set -euo pipefail

work=$1
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
root=$(dirname "$here")
cli="${TURIAN_CLI:-dotnet $root/Turian/Editor/CLI/bin/Debug/net10.0/Turian.Editor.CLI.dll}"
application=org.mass4.turian.androidsmoke

mkdir -p "$work"
cd "$work"
$cli new sample

sed -i 's|"org.mass4.turian.vulkan": "builtin:org.mass4.turian.vulkan"|&,\n    "org.mass4.turian.android": "builtin:org.mass4.turian.android"|' sample/Bricks/manifest.json
profile=sample/Assets/Settings/Build/Client.dataasset
sed -i -e 's/"Name": "Client"/"Name": "Android"/' -e 's/"Platform": "desktop"/"Platform": "android"/' \
  -e 's/"RuntimeIdentifier": null/"RuntimeIdentifier": "android-arm64"/' -e 's/"Development": false/"Development": true/' "$profile"
sed -i "s/\"ApplicationIdentifier\": \"[^\"]*\"/\"ApplicationIdentifier\": \"$application\"/" sample/Assets/Settings/PlayerSettings.dataasset

$cli brick restore --project sample
$cli export sample --profile Android --configuration Debug

apk=$(find sample/Output -name '*-Signed.apk' | head -n1)
test -n "$apk"
bash "$here/android-verify.sh" "$apk" "$application"
