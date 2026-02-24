"""
Spritesheet packer + atlas.json generator for CivSim.
Reads all individual PNGs from Assets/ subfolders, packs into spritesheet.png,
and generates atlas.json with coordinates.

Usage:
    python rebuild_atlas.py           # Rebuild spritesheet + atlas
    python rebuild_atlas.py --dry-run # Show what would be packed without writing
"""

import sys
import json
from pathlib import Path
from PIL import Image

ASSETS = Path(r"C:\Users\angel\CivSim\CivSim.Raylib\Assets")

# Subdirectories to scan (order determines packing priority)
CATEGORIES = ["indicators", "biomes", "resources", "structures", "agents"]

# Padding between sprites in the spritesheet
PADDING = 8


def collect_sprites() -> list[tuple[str, Path, Image.Image]]:
    """Collect all individual sprite PNGs from asset subfolders."""
    sprites = []
    for category in CATEGORIES:
        cat_dir = ASSETS / category
        if not cat_dir.is_dir():
            continue
        for png_file in sorted(cat_dir.glob("*.png")):
            # Skip preview files
            if ".preview." in png_file.name:
                continue
            atlas_key = f"{category}_{png_file.stem}"
            img = Image.open(png_file).convert("RGBA")
            sprites.append((atlas_key, png_file, img))
    return sprites


def shelf_pack(sprites: list[tuple[str, Path, Image.Image]], max_width: int = 512):
    """Pack sprites using shelf-packing algorithm, sorted by height descending."""
    # Sort by height (descending), then width (descending) for better packing
    sorted_sprites = sorted(sprites, key=lambda s: (-s[2].height, -s[2].width))

    placements = {}  # atlas_key -> (x, y, w, h, source_relpath)
    shelves = []  # list of (y_start, height, x_cursor)

    for atlas_key, src_path, img in sorted_sprites:
        w, h = img.size
        placed = False

        # Try to fit in an existing shelf
        for i, (shelf_y, shelf_h, shelf_x) in enumerate(shelves):
            if h <= shelf_h and shelf_x + w + PADDING <= max_width:
                # Fits in this shelf
                placements[atlas_key] = (shelf_x, shelf_y, w, h, src_path)
                shelves[i] = (shelf_y, shelf_h, shelf_x + w + PADDING)
                placed = True
                break

        if not placed:
            # Start a new shelf
            if shelves:
                new_y = max(sy + sh for sy, sh, _ in shelves) + PADDING
            else:
                new_y = PADDING

            shelves.append((new_y, h, PADDING + w + PADDING))
            placements[atlas_key] = (PADDING, new_y, w, h, src_path)

    # Calculate total sheet size
    if placements:
        max_x = max(x + w for x, y, w, h, _ in placements.values()) + PADDING
        max_y = max(y + h for x, y, w, h, _ in placements.values()) + PADDING
    else:
        max_x, max_y = 0, 0

    # Round up to power of 2 for GPU efficiency (optional but nice)
    def next_pow2(n):
        p = 1
        while p < n:
            p *= 2
        return p

    sheet_w = min(next_pow2(max_x), 1024)
    sheet_h = min(next_pow2(max_y), 1024)

    return placements, sheet_w, sheet_h


def build_spritesheet(sprites: list[tuple[str, Path, Image.Image]],
                      placements: dict, sheet_w: int, sheet_h: int) -> Image.Image:
    """Compose the final spritesheet from individual sprites."""
    sheet = Image.new("RGBA", (sheet_w, sheet_h), (0, 0, 0, 0))

    sprite_map = {key: img for key, _, img in sprites}

    for atlas_key, (x, y, w, h, _) in placements.items():
        if atlas_key in sprite_map:
            sheet.paste(sprite_map[atlas_key], (x, y))

    return sheet


def build_atlas_json(placements: dict) -> dict:
    """Generate atlas.json content."""
    atlas = {}
    # Sort by key for deterministic output
    for key in sorted(placements.keys()):
        x, y, w, h, src_path = placements[key]
        # Compute relative source path
        try:
            rel = src_path.relative_to(ASSETS)
            source = str(rel).replace("\\", "/")
        except ValueError:
            source = src_path.name
        atlas[key] = {
            "x": x,
            "y": y,
            "w": w,
            "h": h,
            "source": source
        }
    return atlas


def main():
    dry_run = "--dry-run" in sys.argv
    mode = "DRY RUN" if dry_run else "BUILD"
    print(f"=== CivSim Atlas Rebuild ({mode}) ===")

    # Collect all sprites
    sprites = collect_sprites()
    print(f"\nFound {len(sprites)} sprites:")
    for key, path, img in sprites:
        print(f"  {key}: {img.width}x{img.height} ({path.name})")

    # Pack
    placements, sheet_w, sheet_h = shelf_pack(sprites)
    print(f"\nSheet size: {sheet_w}x{sheet_h}")
    print(f"Sprites packed: {len(placements)}")

    if dry_run:
        print("\n[DRY RUN] Would write:")
        print(f"  {ASSETS / 'spritesheet.png'} ({sheet_w}x{sheet_h})")
        print(f"  {ASSETS / 'atlas.json'} ({len(placements)} entries)")
        return

    # Build and save spritesheet
    sheet = build_spritesheet(sprites, placements, sheet_w, sheet_h)
    sheet_path = ASSETS / "spritesheet.png"
    sheet.save(sheet_path)
    print(f"\nSaved spritesheet: {sheet_path} ({sheet_w}x{sheet_h})")

    # Build and save atlas.json
    atlas = build_atlas_json(placements)
    atlas_path = ASSETS / "atlas.json"
    with open(atlas_path, "w") as f:
        json.dump(atlas, f, indent=2)
    print(f"Saved atlas: {atlas_path} ({len(atlas)} entries)")

    print("\n=== Done! ===")


if __name__ == "__main__":
    main()
