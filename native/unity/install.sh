#!/usr/bin/env bash
#
# Copy the core and the Unity components into a Unity project.
#
# The core stays in native/MarkerOne.Core/ as the source of truth, because that
# is the copy the conformance suite proves. This syncs it into Assets/ so the
# app runs the same code, and re-running it after a change to the core is the
# whole point — a proof about a copy that has since diverged is not a proof.
#
#   ./native/unity/install.sh ~/MarkerOneApp
#
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
target="${1:-}"

if [ -z "$target" ]; then
  echo "usage: $0 <path-to-unity-project>" >&2
  echo "   eg: $0 ~/MarkerOneApp" >&2
  exit 1
fi

if [ ! -d "$target/Assets" ]; then
  echo "no Assets/ in $target — is that a Unity project?" >&2
  echo "create it in the Unity Hub first, then run this." >&2
  exit 1
fi

core="$target/Assets/MarkerOne/Core"
unity="$target/Assets/MarkerOne/Unity"
editor="$target/Assets/MarkerOne/Editor"
# Native plugins live where Unity looks for them by platform, not beside the
# managed code that calls into them.
plugins="$target/Assets/Plugins/iOS"
mkdir -p "$core" "$unity" "$editor" "$plugins"

# Delete first: a file removed from the core should disappear here too, or a
# stale copy keeps compiling long after it stopped being real.
rm -f "$core"/*.cs "$core"/*.asmdef "$unity"/*.cs "$unity"/*.asmdef \
      "$editor"/*.cs "$editor"/*.asmdef "$plugins"/MarkerOne*.m

cp "$here"/native/MarkerOne.Core/*.cs                 "$core/"
cp "$here"/native/unity/MarkerOne/MarkerOne.Core.asmdef "$core/"

cp "$here"/native/unity/MarkerOne/*.cs                "$unity/"
cp "$here"/native/unity/MarkerOne/MarkerOne.Unity.asmdef "$unity/"

cp "$here"/native/unity/MarkerOne/Editor/*.cs        "$editor/"
cp "$here"/native/unity/MarkerOne/Editor/*.asmdef    "$editor/"

cp "$here"/native/unity/MarkerOne/iOS/*.m            "$plugins/"

echo "core   -> $core"
ls -1 "$core" | sed 's/^/           /'
echo "unity  -> $unity"
ls -1 "$unity" | sed 's/^/           /'
echo "editor -> $editor"
ls -1 "$editor" | sed 's/^/           /'
echo "ios    -> $plugins"
ls -1 "$plugins" | sed 's/^/           /'
echo
echo "Unity will import these when it next has focus."
