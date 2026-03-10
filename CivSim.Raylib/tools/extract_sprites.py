"""
Sprite extraction tool for CivSim.
Crops individual sprites from Vectoraith tileset grids and saves as individual PNGs.

Usage:
    python extract_sprites.py                  # Run all extractions
    python extract_sprites.py --preview        # Save bordered preview images instead of final crops
"""

import sys
import os
from pathlib import Path
from PIL import Image

DOWNLOADS = Path(r"C:\Users\angel\Downloads\pixelart")
ASSETS = Path(r"C:\Users\angel\CivSim\CivSim.Raylib\Assets")

def crop_grid(src_path: Path, cell_w: int, cell_h: int, col: int, row: int) -> Image.Image:
    """Crop a single grid cell from a tileset sheet."""
    img = Image.open(src_path)
    x = col * cell_w
    y = row * cell_h
    return img.crop((x, y, x + cell_w, y + cell_h))

def crop_rect(src_path: Path, x: int, y: int, w: int, h: int) -> Image.Image:
    """Crop an arbitrary rectangle from an image."""
    img = Image.open(src_path)
    return img.crop((x, y, x + w, y + h))

def compose_4x4(src_path: Path, cell_size: int, cells: list[tuple[int, int]]) -> Image.Image:
    """Compose a 4x4 grid of cells into a 64x64 tile."""
    src = Image.open(src_path)
    out = Image.new("RGBA", (64, 64))
    for i, (col, row) in enumerate(cells):
        tx = (i % 4) * cell_size
        ty = (i // 4) * cell_size
        cell = src.crop((col * cell_size, row * cell_size,
                         col * cell_size + cell_size, row * cell_size + cell_size))
        if cell_size != 16:
            cell = cell.resize((16, 16), Image.NEAREST)
        out.paste(cell, (tx, ty))
    return out

def save_sprite(img: Image.Image, dest_path: Path, preview: bool = False):
    """Save sprite, creating directories as needed."""
    dest_path.parent.mkdir(parents=True, exist_ok=True)
    if preview:
        # Add red border for preview mode
        from PIL import ImageDraw
        preview_img = img.copy().resize((img.width * 4, img.height * 4), Image.NEAREST)
        draw = ImageDraw.Draw(preview_img)
        draw.rectangle([0, 0, preview_img.width - 1, preview_img.height - 1], outline="red", width=2)
        preview_path = dest_path.with_suffix(".preview.png")
        preview_img.save(preview_path)
        print(f"  PREVIEW: {preview_path}")
    else:
        img.save(dest_path)
        print(f"  Saved: {dest_path} ({img.width}x{img.height})")

def extract_activity_icons(preview: bool):
    """Extract 8 activity icons from Vectoraith icon sheets.

    Uses a mix of 16x16 direct crops and 32x32 icons downscaled to 16x16
    for best visual results at CivSim's icon display size.
    """
    print("\n=== Extracting Activity Icons (16x16) ===")

    iconset_16 = DOWNLOADS / "vectoraith_16x16_iconset" / "vectoraith_16x16_iconset.png"
    farming_16 = DOWNLOADS / "vectoraith_iconset_16x16_farming" / "vectoraith_16x16_iconset_farming.png"
    iconset_32 = DOWNLOADS / "vectoraith_iconset" / "vectoraith_32x32_iconset.png"
    farming_32 = DOWNLOADS / "vectoraith_iconset_farming" / "vectoraith_32x32_iconset_farming.png"

    # --- Direct 16x16 extractions (good matches) ---
    extractions_16 = [
        # cook: torch/campfire from main iconset row 13 col 5
        (iconset_16, 5, 13, "cook", "Torch/campfire icon"),
        # tend_farm: hoe from farming iconset row 5 col 3
        (farming_16, 3, 5, "tend_farm", "Hoe tool"),
        # deposit: chest from main iconset row 13 col 2
        (iconset_16, 2, 13, "deposit", "Chest (deposit)"),
    ]

    for src, col, row, name, desc in extractions_16:
        print(f"  {name}: 16x16 col={col}, row={row} ({desc})")
        sprite = crop_grid(src, 16, 16, col, row)
        save_sprite(sprite, ASSETS / "indicators" / f"{name}.png", preview)

    # --- 32x32 extractions downscaled to 16x16 (better detail) ---
    extractions_32 = [
        # preserve: jar/bottle from farming 32x32 row 13 col 0 (white jar)
        (farming_32, 0, 13, "preserve", "Jar/bottle (preserve)"),
        # withdraw: bag from main 32x32 row 13 col 0 (open bag)
        (iconset_32, 0, 13, "withdraw", "Bag (withdraw)"),
        # explore: magnifying glass from main 32x32 row 13 col 15
        (iconset_32, 15, 13, "explore", "Magnifying glass (explore)"),
        # socialize: drum/music from main 32x32 row 12 col 15 (gathering/social)
        (iconset_32, 14, 12, "socialize", "Flute/music (social)"),
        # return_home: lantern from main 32x32 row 14 col 11 (guiding light home)
        (iconset_32, 11, 14, "return_home", "Lantern (return home)"),
    ]

    for src, col, row, name, desc in extractions_32:
        print(f"  {name}: 32x32->16x16 col={col}, row={row} ({desc})")
        sprite = crop_grid(src, 32, 32, col, row)
        sprite = sprite.resize((16, 16), Image.NEAREST)
        save_sprite(sprite, ASSETS / "indicators" / f"{name}.png", preview)

def extract_biome_tiles(preview: bool):
    """Extract 64x64 biome ground tiles from 32x32 A5/A1 tilesheets.

    Uses the solid fill tile from each biome's A5 tilesheet (top-left 32x32 cell)
    and upscales 2x to 64x64 with nearest-neighbor interpolation.
    Water uses the A1 tilesheet which has solid water fill tiles.
    """
    print("\n=== Extracting Biome Tiles (32x32 -> 64x64) ===")

    # Biome sources: (name, tileset path, crop_x, crop_y)
    # A5 sheets: cell (1,2) at pixel (32,64) is a solid, opaque center-fill ground tile
    # A1 water sheet: cells (6,7) at pixel (192,224) is solid teal water
    biome_sources = [
        ("forest",
         DOWNLOADS / "Biome Tileset Pack A" / "32x32" / "temperate_forest" / "tilesets - RPG Maker ready" / "vectoraith_tileset_terrain_A5_temperate_forest_32x32.png",
         32, 64),
        ("plains",
         DOWNLOADS / "Biome Tileset Pack B" / "32x32" / "grassland" / "tilesets - RPG Maker ready" / "vectoraith_tileset_terrain_A5_grassland_32x32.png",
         32, 64),
        ("mountain",
         DOWNLOADS / "Biome Tileset Pack D" / "32x32" / "alpine" / "tilesets - RPG Maker ready" / "vectoraith_tileset_terrain_A5_alpine_A_32x32.png",
         32, 64),
        ("desert",
         DOWNLOADS / "Biome Tileset Pack D" / "32x32" / "desert" / "tilesets - RPG Maker ready" / "vectoraith_tileset_terrain_A5_desert_32x32.png",
         32, 64),
        ("water",
         DOWNLOADS / "Biome Tileset Pack A" / "32x32" / "temperate_forest" / "tilesets - RPG Maker ready" / "vectoraith_tileset_terrain_A5_temperate_forest_32x32.png",
         192, 96),
    ]

    for name, src_path, cx, cy in biome_sources:
        if not src_path.exists():
            print(f"  SKIP {name}: {src_path} not found")
            continue
        tile = crop_rect(src_path, cx, cy, 32, 32)
        tile = tile.resize((64, 64), Image.NEAREST)
        save_sprite(tile, ASSETS / "biomes" / f"{name}.png", preview)

def extract_npc_sprites(preview: bool):
    """Extract 12 static NPC character sprites from RPG NPC sprite sheets.

    RPG Maker sprite sheet layout (32x32 version, 384x512):
    - 12 cols x 16 rows of 32x32 cells
    - 4 characters per row-group (each char = 3 walk frames wide)
    - 4 rows per character (directions: down, left, right, up)
    - 4 row-groups stacked vertically = 16 characters total per sheet
    - Front-facing idle = middle frame (col offset 1) of down-facing row (first row of group)

    Character positions (col, row) for front-facing idle:
      Group 0: (1,0), (4,0), (7,0), (10,0)
      Group 1: (1,4), (4,4), (7,4), (10,4)
      Group 2: (1,8), (4,8), (7,8), (10,8)
      Group 3: (1,12), (4,12), (7,12), (10,12)
    """
    print("\n=== Extracting NPC Agent Sprites (32x32) ===")

    people_src = DOWNLOADS / "Top Down RPG NPC Sprite Pack" / "32x32" / "_default_tint" / "generic_people_32x32.png"
    jobs_src = DOWNLOADS / "Top Down RPG NPC Sprite Pack" / "32x32" / "_default_tint" / "generic_jobs_32x32.png"
    people_b_src = DOWNLOADS / "Top Down RPG NPC Sprite Pack" / "32x32" / "_default_tint" / "medieval_people_32x32.png"

    # Front-facing idle positions for all 16 characters per sheet
    idle_positions = []
    for group_row in range(0, 16, 4):
        for char_col in range(0, 12, 3):
            idle_positions.append((char_col + 1, group_row))  # col+1 = middle walk frame

    npc_index = 1
    sources = [
        (people_src, "generic_people"),
        (jobs_src, "generic_jobs"),
        (people_b_src, "medieval_people"),
    ]

    for src_path, src_name in sources:
        if npc_index > 12:
            break
        if not src_path.exists():
            print(f"  SKIP {src_name}: {src_path} not found")
            continue

        img = Image.open(src_path)
        max_rows = img.height // 32

        for col, row in idle_positions:
            if npc_index > 12:
                break
            if row >= max_rows or col >= img.width // 32:
                continue
            sprite = img.crop((col * 32, row * 32, col * 32 + 32, row * 32 + 32))
            # Skip empty sprites
            if sprite.getbbox() is None:
                continue
            # Skip sprites that are too small (just a hat fragment)
            bbox = sprite.getbbox()
            content_h = bbox[3] - bbox[1]
            if content_h < 16:  # Less than half the cell height = probably partial
                continue
            save_sprite(sprite, ASSETS / "agents" / f"npc_{npc_index:02d}.png", preview)
            npc_index += 1

    print(f"  Total NPC sprites extracted: {npc_index - 1}")

def extract_resource_sprites(preview: bool):
    """Extract resource detail sprites (trees, rocks, etc.) from biome detail sheets."""
    print("\n=== Extracting Resource Sprites (32x32) ===")

    # Temperate forest details - has trees, rocks, bushes at various sizes
    forest_details = DOWNLOADS / "Biome Tileset Pack A" / "16x16" / "temperate_forest" / "tilesets" / "vectoraith_tileset_details_temperate_forest.png"

    # Alpine details - rocks, stones
    alpine_details = DOWNLOADS / "Biome Tileset Pack D" / "16x16" / "alpine" / "tilesets" / "vectoraith_tileset_details_alpine.png"

    # Farming iconset 32x32 - grain, fish, produce
    farming_32 = DOWNLOADS / "vectoraith_iconset_farming" / "vectoraith_32x32_iconset_farming.png"

    # Desert details - cacti, sand rocks
    desert_details = DOWNLOADS / "Biome Tileset Pack D" / "16x16" / "desert" / "tilesets" / "vectoraith_tileset_details_desert.png"

    # The detail sheets at 16x16 have objects at mixed sizes (some span 2x2 = 32x32)
    # Trees are typically 2 tiles wide × 2-3 tiles tall starting from row 2

    if forest_details.exists():
        # Tree: crop a 32x32 tree from rows 2-3 (large deciduous trees)
        # The trees in the detail sheet are at ~col 4-5, row 2-3 area (32x32 each)
        tree = crop_rect(forest_details, 4 * 16, 2 * 16, 32, 32)
        save_sprite(tree, ASSETS / "resources" / "tree.png", preview)

        # Tree2: different tree variant
        tree2 = crop_rect(forest_details, 6 * 16, 2 * 16, 32, 32)
        save_sprite(tree2, ASSETS / "resources" / "tree2.png", preview)

        # Berry bush: small bush from row 0 area
        bush = crop_rect(forest_details, 0, 4 * 16, 32, 32)
        save_sprite(bush, ASSETS / "resources" / "berry_bush.png", preview)

        # Rock: from the detail sheet
        rock = crop_rect(forest_details, 4 * 16, 1 * 16, 32, 32)
        save_sprite(rock, ASSETS / "resources" / "rock.png", preview)
    else:
        print(f"  SKIP forest details: {forest_details} not found")

    if alpine_details.exists():
        # Stone: from alpine rocks
        stone = crop_rect(alpine_details, 4 * 16, 1 * 16, 32, 32)
        save_sprite(stone, ASSETS / "resources" / "stone.png", preview)

        # Ore: darker rock from alpine
        ore = crop_rect(alpine_details, 6 * 16, 1 * 16, 32, 32)
        save_sprite(ore, ASSETS / "resources" / "ore.png", preview)
    else:
        print(f"  SKIP alpine details: {alpine_details} not found")

    if farming_32.exists():
        # Grain: wheat sheaf from farming iconset (rows 4-6 area)
        grain = crop_grid(farming_32, 32, 32, 0, 4)
        save_sprite(grain, ASSETS / "resources" / "grain.png", preview)

        # Fish: from bottom rows of farming iconset
        fish = crop_grid(farming_32, 32, 32, 0, 15)
        save_sprite(fish, ASSETS / "resources" / "fish.png", preview)
    else:
        print(f"  SKIP farming iconset: {farming_32} not found")


def main():
    preview = "--preview" in sys.argv
    mode = "PREVIEW" if preview else "EXTRACT"
    print(f"=== CivSim Sprite Extraction ({mode} mode) ===")

    extract_activity_icons(preview)
    extract_biome_tiles(preview)
    extract_npc_sprites(preview)
    extract_resource_sprites(preview)

    print(f"\n=== Done! ===")
    if preview:
        print("Preview images saved with .preview.png suffix. Check them, then run without --preview to extract.")
    else:
        print("Individual PNGs saved. Run rebuild_atlas.py to regenerate spritesheet + atlas.json.")


if __name__ == "__main__":
    main()
