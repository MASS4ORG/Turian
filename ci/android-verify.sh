#!/usr/bin/env bash
# Checks an exported APK: signature, identity, minimum SDK, and 16 KB alignment. Usage: android-verify.sh <apk> <application id>
set -euo pipefail

apk=$1
expected=$2
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=android-toolchain.env
. "$here/android-toolchain.env"
tools="$ANDROID_HOME/build-tools/$ANDROID_BUILD_TOOLS_VERSION"

"$tools/apksigner" verify "$apk"
"$tools/zipalign" -c -P 16 4 "$apk"

badging=$("$tools/aapt2" dump badging "$apk")
identity=$(echo "$badging" | grep -oE "^package: name='[^']+'" | cut -d"'" -f2)
minimum=$(echo "$badging" | grep -oE "^(sdkVersion|minSdkVersion):'[0-9]+'" | cut -d"'" -f2)
if [ "$identity" != "$expected" ]; then
  echo "Expected application id $expected, got $identity" >&2
  exit 1
fi
if [ "${minimum:-0}" -lt 33 ]; then
  echo "Expected a minimum SDK of at least 33, got ${minimum:-none}" >&2
  exit 1
fi
entries=$(unzip -l "$apk")
if ! grep -qE "assets/Content/.*\.oap$" <<<"$entries"; then
  echo "The APK holds no packaged content" >&2
  exit 1
fi
echo "Verified $apk: $identity, minimum SDK $minimum"
