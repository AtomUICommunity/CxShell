#!/usr/bin/env bash
# Builds the native PTY bridge used by the Unix local terminal.
#
#   OUTPUT_DIR=/path/to/publish bash tools/build-pty-bridge.sh
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOURCE="$REPO_ROOT/native/CxPtyBridge/CxPtyBridge.c"
OUTPUT_DIR="${OUTPUT_DIR:-}"

if [ ! -f "$SOURCE" ]; then
  echo "CxPtyBridge source not found at $SOURCE" >&2
  exit 1
fi

if [ -z "$OUTPUT_DIR" ]; then
  echo "OUTPUT_DIR must point at the directory that holds libCxPtyBridge." >&2
  exit 1
fi

mkdir -p "$OUTPUT_DIR"

case "$(uname -s)" in
  Darwin)
    LIBRARY_NAME="libCxPtyBridge.dylib"
    cc -dynamiclib -O2 -fvisibility=hidden -o "$OUTPUT_DIR/$LIBRARY_NAME" "$SOURCE" -lutil
    ;;
  Linux)
    LIBRARY_NAME="libCxPtyBridge.so"
    cc -shared -fPIC -O2 -fvisibility=hidden -o "$OUTPUT_DIR/$LIBRARY_NAME" "$SOURCE" -lutil
    ;;
  *)
    echo "Unsupported OS for CxPtyBridge: $(uname -s)" >&2
    exit 1
    ;;
esac

echo "Built $OUTPUT_DIR/$LIBRARY_NAME"
