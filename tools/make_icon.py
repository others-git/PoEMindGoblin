#!/usr/bin/env python3
"""Build assets/mindgoblin.ico from assets/mindgoblin.svg.

The SVG is the source; the .ico is generated but COMMITTED, because the app's
<ApplicationIcon> needs it at build time and a CI runner has no SVG rasteriser.

Every size is rasterised from the vector separately rather than downsampled from one
big bitmap. The icon is drawn for the 16px case, and downsampling 256 -> 16 turns the
gold band into a grey smear -- which is the one thing that has to survive.

Requires rsvg-convert (librsvg). Run after editing the SVG:

    python3 tools/make_icon.py
"""

import struct
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SVG = ROOT / "assets" / "mindgoblin.svg"
ICO = ROOT / "assets" / "mindgoblin.ico"

# Windows picks per context: 16 in the title bar, 32 on the desktop, 48 in Explorer,
# 256 for the extra-large view. The odd sizes in between stop it from scaling one of
# those and getting a soft result.
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]


def render(size: int) -> bytes:
    out = subprocess.run(
        ["rsvg-convert", "-w", str(size), "-h", str(size), str(SVG)],
        capture_output=True,
    )
    if out.returncode != 0:
        sys.exit(f"rsvg-convert failed at {size}px: {out.stderr.decode(errors='replace')}")
    return out.stdout


def main() -> None:
    if not SVG.exists():
        sys.exit(f"missing {SVG}")

    images = [(size, render(size)) for size in SIZES]

    # ICONDIR, then one 16-byte ICONDIRENTRY per image, then the PNG payloads. PNG-in-ICO
    # is understood by Vista and later, which is well below this app's floor of Windows 10.
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = len(header) + 16 * len(images)

    directory, payload = b"", b""
    for size, png in images:
        # 256 is stored as 0: the field is one byte and 256 does not fit in it.
        byte = 0 if size == 256 else size
        directory += struct.pack("<BBBBHHII", byte, byte, 0, 0, 1, 32, len(png), offset)
        payload += png
        offset += len(png)

    ICO.write_bytes(header + directory + payload)
    print(f"-> {ICO}  ({len(SIZES)} sizes, {ICO.stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
