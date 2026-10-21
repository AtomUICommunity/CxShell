#!/usr/bin/env bash
set -euo pipefail

APP_PATH="${APP_PATH:-}"
PACK_ID="${PACK_ID:-}"
PACK_VERSION="${PACK_VERSION:-}"
PACK_CHANNEL="${PACK_CHANNEL:-}"
PACK_RUNTIME="${PACK_RUNTIME:-}"
PACK_TITLE="${PACK_TITLE:-CxShell}"
OUTPUT_DIR="${OUTPUT_DIR:-}"
VELOPACK_VERSION="${VELOPACK_VERSION:-1.2.0}"

for value_name in APP_PATH PACK_ID PACK_VERSION PACK_CHANNEL PACK_RUNTIME OUTPUT_DIR; do
  if [ -z "${!value_name}" ]; then
    echo "$value_name is required." >&2
    exit 1
  fi
done

if [ ! -d "$APP_PATH" ] || [[ "$APP_PATH" != *.app ]]; then
  echo "APP_PATH must point to an existing .app bundle: $APP_PATH" >&2
  exit 1
fi

if [ ! -x "$APP_PATH/Contents/MacOS/CxShell" ]; then
  echo "CxShell executable was not found in $APP_PATH" >&2
  exit 1
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
VPK_TOOL_DIR="$REPO_ROOT/.tmp/vpk"
VPK_EXE="$VPK_TOOL_DIR/vpk"

mkdir -p "$OUTPUT_DIR"
if [ -x "$VPK_EXE" ]; then
  dotnet tool update vpk --tool-path "$VPK_TOOL_DIR" --version "$VELOPACK_VERSION"
else
  dotnet tool install vpk --tool-path "$VPK_TOOL_DIR" --version "$VELOPACK_VERSION"
fi

"$VPK_EXE" pack \
  --packId "$PACK_ID" \
  --packVersion "$PACK_VERSION" \
  --packDir "$APP_PATH" \
  --packAuthors "xiaochengzjc" \
  --packTitle "$PACK_TITLE" \
  --mainExe "CxShell" \
  --runtime "$PACK_RUNTIME" \
  --channel "$PACK_CHANNEL" \
  --noInst \
  --outputDir "$OUTPUT_DIR"

portable_package="$OUTPUT_DIR/$PACK_ID-$PACK_CHANNEL-Portable.zip"
required_files=(
  "$portable_package"
  "$OUTPUT_DIR/releases.$PACK_CHANNEL.json"
  "$OUTPUT_DIR/RELEASES-$PACK_CHANNEL"
  "$OUTPUT_DIR/$PACK_ID-$PACK_VERSION-$PACK_CHANNEL-full.nupkg"
)
for required_file in "${required_files[@]}"; do
  if [ ! -s "$required_file" ]; then
    echo "Expected Velopack output was not created: $required_file" >&2
    exit 1
  fi
done

unpack_dir="$(mktemp -d)"
app_parent="$(dirname "$APP_PATH")"
replacement_dir="$(mktemp -d "$app_parent/.cxshell-velopack.XXXXXX")"
replacement_app="$replacement_dir/$PACK_TITLE.app"
backup_app="$replacement_dir/original.app"
cleanup() {
  if [ ! -e "$APP_PATH" ] && [ -d "$backup_app" ]; then
    mv "$backup_app" "$APP_PATH"
  fi
  rm -rf "$unpack_dir" "$replacement_dir"
}
trap cleanup EXIT

ditto -x -k "$portable_package" "$unpack_dir"
packaged_app="$unpack_dir/$PACK_TITLE.app"
if [ ! -d "$packaged_app" ]; then
  echo "Velopack portable package does not contain $PACK_TITLE.app" >&2
  exit 1
fi

test -x "$packaged_app/Contents/MacOS/UpdateMac"
test -f "$packaged_app/Contents/Resources/sq.version"

ditto "$packaged_app" "$replacement_app"
mv "$APP_PATH" "$backup_app"
if ! mv "$replacement_app" "$APP_PATH"; then
  mv "$backup_app" "$APP_PATH"
  echo "Failed to replace the app bundle with the Velopack package." >&2
  exit 1
fi
rm -rf "$backup_app"

echo "Packaged $APP_PATH for Velopack updates using channel $PACK_CHANNEL."
