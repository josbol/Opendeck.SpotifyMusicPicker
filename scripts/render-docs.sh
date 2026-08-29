#!/usr/bin/env bash
# Renders the sample keys into docs/keys.png for the README.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BIN="$ROOT/plugin/com.josbol.spotifymusicpicker.sdPlugin/bin/linux-x64/opendeck-spotifymusicpicker"
TMP="$(mktemp -d)"
"$BIN" --render "$TMP" >/dev/null
mkdir -p "$ROOT/docs"
python3 - "$TMP" "$ROOT/docs/keys.png" <<'PY'
import sys, os
from PIL import Image
src, out = sys.argv[1], sys.argv[2]
order = ["mix-art", "album-art-playing", "album-art-paused", "album-tile", "liked-tile", "manual-tile",
         "nowplaying", "nowplaying-none", "empty", "connect", "previous", "next"]
files = [os.path.join(src, n + ".png") for n in order if os.path.exists(os.path.join(src, n + ".png"))]
cols = 6; cell = 150; rows = (len(files) + cols - 1) // cols
wide = os.path.join(src, "nowplaying-wide.png")
extra = 1 if os.path.exists(wide) else 0
sheet = Image.new("RGB", (cols * cell, (rows + extra) * cell), (30, 32, 38))
for i, f in enumerate(files):
    sheet.paste(Image.open(f).convert("RGB"), ((i % cols) * cell + 3, (i // cols) * cell + 3))
if extra:
    w = Image.open(wide).convert("RGB").resize((288, 144), Image.Resampling.LANCZOS)   # as the D200X shows it
    sheet.paste(w, (3, rows * cell + 3))
sheet.save(out, optimize=True); print("wrote", out, sheet.size)
PY
rm -rf "$TMP"
