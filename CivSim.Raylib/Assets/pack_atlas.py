#!/usr/bin/env python3
"""
Atlas packer for CivSim sprite assets.
Scans all PNGs in biomes/, resources/, structures/, agents/, indicators/
and packs them into spritesheet.png + atlas.json.

Naming convention: {folder}_{stem}  (e.g. structures/lean_to.png -> structures_lean_to)

Usage:
    python pack_atlas.py          # from CivSim.Raylib/Assets/
"""

import json
import os
from pathlib import Path
from PIL import Image

FOLDERS = ["biomes", "resources", "structures", "agents", "indicators"]
EXCLUDE = {"indicators/teaching.png"}  # Dead code in GDD v1.8
PADDING = 8  # px between sprites
CANVAS_SIZE = 512


def collect_sprites(base: Path):
    """Collect all sprite PNGs from subfolders."""
    sprites = []
    for folder in FOLDERS:
        folder_path = base / folder
        if not folder_path.exists():
            continue
        for png in sorted(folder_path.glob("*.png")):
            rel = f"{folder}/{png.name}"
            if rel in EXCLUDE:
                continue
            atlas_name = f"{folder}_{png.stem}"
            img = Image.open(png).convert("RGBA")
            sprites.append({
                "name": atlas_name,
                "image": img,
                "w": img.width,
                "h": img.height,
                "source": rel,
            })
    return sprites


def pack_strip(sprites, canvas_size, padding):
    """Simple row-based strip packing sorted by height desc, then width desc."""
    # Sort: tallest first, then widest
    sprites.sort(key=lambda s: (-s["h"], -s["w"]))

    canvas = Image.new("RGBA", (canvas_size, canvas_size), (0, 0, 0, 0))
    atlas = {}

    x, y = padding, padding
    row_height = 0

    for sp in sprites:
        w, h = sp["w"], sp["h"]

        # Wrap to next row if needed
        if x + w + padding > canvas_size:
            x = padding
            y += row_height + padding
            row_height = 0

        if y + h + padding > canvas_size:
            raise RuntimeError(
                f"Canvas {canvas_size}x{canvas_size} too small! "
                f"Failed at sprite '{sp['name']}' at y={y}"
            )

        canvas.paste(sp["image"], (x, y))
        atlas[sp["name"]] = {
            "x": x,
            "y": y,
            "w": w,
            "h": h,
            "source": sp["source"],
        }

        row_height = max(row_height, h)
        x += w + padding

    return canvas, atlas


def main():
    base = Path(__file__).parent
    sprites = collect_sprites(base)
    print(f"Found {len(sprites)} sprites to pack")

    canvas, atlas = pack_strip(sprites, CANVAS_SIZE, PADDING)

    # Write outputs
    sheet_path = base / "spritesheet.png"
    atlas_path = base / "atlas.json"

    canvas.save(sheet_path, "PNG")
    print(f"Wrote {sheet_path} ({canvas.width}x{canvas.height})")

    # Sort atlas keys for stable output
    sorted_atlas = dict(sorted(atlas.items()))
    with open(atlas_path, "w") as f:
        json.dump(sorted_atlas, f, indent=2)
    print(f"Wrote {atlas_path} ({len(sorted_atlas)} entries)")

    # Summary
    for name, info in sorted_atlas.items():
        print(f"  {name}: {info['w']}x{info['h']} @ ({info['x']},{info['y']})")


if __name__ == "__main__":
    main()
