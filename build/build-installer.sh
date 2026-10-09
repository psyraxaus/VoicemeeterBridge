#!/usr/bin/env bash
# Builds the VoicemeeterBridge installer. Run from Git Bash anywhere in the repo:
#
#   build/build-installer.sh            # uses the version in Directory.Build.props
#   build/build-installer.sh --skip-tests
#
# Output: dist/VoicemeeterBridge-Setup-<version>.exe
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

SKIP_TESTS=0
for arg in "$@"; do
  case "$arg" in
    --skip-tests) SKIP_TESTS=1 ;;
    *) echo "unknown option: $arg" >&2; exit 1 ;;
  esac
done

VERSION="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' Directory.Build.props | head -1)"
if [[ ! "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Directory.Build.props must contain <Version>MAJOR.MINOR.PATCH</Version>, found '$VERSION'" >&2
  exit 1
fi

ISCC="${ISCC:-}"
if [[ -z "$ISCC" ]]; then
  for candidate in \
    "$LOCALAPPDATA/Programs/Inno Setup 6/ISCC.exe" \
    "/c/Program Files (x86)/Inno Setup 6/ISCC.exe" \
    "/c/Program Files/Inno Setup 6/ISCC.exe"; do
    if [[ -f "$candidate" ]]; then ISCC="$candidate"; break; fi
  done
fi
if [[ -z "$ISCC" || ! -f "$ISCC" ]]; then
  echo "ISCC.exe (Inno Setup 6) not found. Install Inno Setup or set ISCC=/path/to/ISCC.exe" >&2
  exit 1
fi

echo "== VoicemeeterBridge $VERSION"

if [[ "$SKIP_TESTS" -eq 0 ]]; then
  echo "== dotnet test"
  dotnet test -c Release --nologo
fi

echo "== dotnet publish"
rm -rf publish
dotnet publish src/VoicemeeterBridge -c Release -r win-x64 --self-contained -o publish --nologo

echo "== ISCC"
mkdir -p dist
# Git Bash rewrites arguments that start with "/" as paths; stop that for ISCC switches.
MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL="*" "$ISCC" "/DAppVersion=$VERSION" "/Qp" "$(cygpath -w "$ROOT/installer/VoicemeeterBridge.iss")"

echo "== done: dist/VoicemeeterBridge-Setup-$VERSION.exe"
