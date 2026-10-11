#!/bin/bash
# Round-trip every NX archive in the reference corpus through the writer.
# Reads only from the source directory; all output goes to the scratch directory.
DOTNET="${DOTNET_PATH:-dotnet}"
VERIFY="${NXVERIFY_DLL:-tools/NxVerify/bin/Release/net10.0-windows/NxVerify.dll}"
SRC="${NX_FIXTURE_DIR:-}"
OUT='C:/nxtmp'
mkdir -p "$OUT"
for f in "$SRC"/*.nx; do
  name=$(basename "$f" .nx)
  dst="$OUT/$name.nx"
  rm -f "$dst"
  echo "===== $name ====="
  "$DOTNET" "$VERIFY" --nxwrite "$f" "$dst" 2>&1 | sed 's/^/  /'
done
echo "ALL_DONE"