#!/usr/bin/env bash
# Installs the pinned Android export toolchain: the .NET Android workload, a JDK when missing, and the Android SDK.
# ANDROID_HOME selects the SDK folder (created if absent); it is reused when already complete.
set -euo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=android-toolchain.env
. "$here/android-toolchain.env"

export ANDROID_HOME="${ANDROID_HOME:-$HOME/android-sdk}"
export ANDROID_SDK_ROOT="$ANDROID_HOME"

if ! command -v javac >/dev/null 2>&1; then
  apt-get update -yqq
  apt-get install -yqq --no-install-recommends "$ANDROID_JDK_PACKAGE"
fi

sdkmanager="$ANDROID_HOME/cmdline-tools/latest/bin/sdkmanager"
if [ ! -x "$sdkmanager" ]; then
  archive="$(mktemp -d)/cmdline-tools.zip"
  curl -fsSL -o "$archive" "https://dl.google.com/android/repository/commandlinetools-linux-${ANDROID_CMDLINE_TOOLS_BUILD}_latest.zip"
  echo "$ANDROID_CMDLINE_TOOLS_SHA1  $archive" | sha1sum -c -
  mkdir -p "$ANDROID_HOME/cmdline-tools"
  unzip -q "$archive" -d "$ANDROID_HOME/cmdline-tools"
  mv "$ANDROID_HOME/cmdline-tools/cmdline-tools" "$ANDROID_HOME/cmdline-tools/latest"
fi

# Running this script is the CI operator's acceptance of the Android SDK license.
yes | "$sdkmanager" --licenses >/dev/null || true
"$sdkmanager" --install "platform-tools" "$ANDROID_PLATFORM_PACKAGE" "build-tools;$ANDROID_BUILD_TOOLS_VERSION" >/dev/null

dotnet workload install android --version "$ANDROID_WORKLOAD_SET" --skip-manifest-update 2>/dev/null \
  || dotnet workload install android --version "$ANDROID_WORKLOAD_SET"
dotnet workload list
