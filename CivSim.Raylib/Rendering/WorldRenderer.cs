using Raylib_cs;
using CivSim.Core;
using System.Numerics;
using Rl = Raylib_cs.Raylib;

namespace CivSim.Raylib.Rendering;

/// <summary>
/// Renders biome backgrounds, resource sprites, and structures.
/// Uses frustum culling and LOD (level-of-detail) based on zoom level.
/// When a SpriteAtlas is available, uses texture sprites; otherwise falls back to procedural shapes.
/// </summary>
public class WorldRenderer
{
    /// <summary>GDD v1.7.2: MAX_SPRITES_PER_TILE = 5 per resource type.</summary>
    private static readonly int MaxSpritesPerResource = SimConfig.MaxSpritesPerTile;
    private readonly SpriteAtlas? atlas;

    // Flat biome colors for low-zoom LOD
    private static readonly Dictionary<BiomeType, Color> BiomeColors = new()
    {
        { BiomeType.Forest, new Color(34, 100, 34, 255) },
        { BiomeType.Plains, new Color(180, 170, 60, 255) },
        { BiomeType.Mountain, new Color(120, 115, 110, 255) },
        { BiomeType.Water, new Color(40, 80, 180, 255) },
        { BiomeType.Desert, new Color(210, 185, 140, 255) },
    };

    // Atlas sprite names per biome
    private static readonly Dictionary<BiomeType, string> BiomeSpriteNames = new()
    {
        { BiomeType.Forest, "biomes_forest" },
        { BiomeType.Plains, "biomes_plains" },
        { BiomeType.Mountain, "biomes_mountain" },
        { BiomeType.Water, "biomes_water" },
        { BiomeType.Desert, "biomes_desert" },
    };

    // Resource dot colors for medium-zoom LOD
    private static readonly Dictionary<ResourceType, Color> ResourceDotColors = new()
    {
        { ResourceType.Wood, new Color(101, 67, 33, 255) },
        { ResourceType.Berries, new Color(200, 30, 30, 255) },
        { ResourceType.Stone, new Color(150, 145, 140, 255) },
        { ResourceType.Ore, new Color(205, 127, 50, 255) },
        { ResourceType.Grain, new Color(218, 165, 32, 255) },
        { ResourceType.Fish, new Color(70, 130, 200, 255) },
        { ResourceType.Animals, new Color(139, 90, 43, 255) },
    };

    public WorldRenderer(SpriteAtlas? atlas = null)
    {
        this.atlas = atlas;
    }

    public void Render(World world, Camera2D camera, int tileSize, bool showGrid,
                       int screenWidth, int screenHeight)
    {
        float zoom = camera.Zoom;
        bool useSprites = atlas is { IsLoaded: true };

        // Frustum culling — calculate visible tile range
        Vector2 topLeft = Rl.GetScreenToWorld2D(new Vector2(0, 0), camera);
        Vector2 bottomRight = Rl.GetScreenToWorld2D(new Vector2(screenWidth, screenHeight), camera);

        int startX = Math.Max(0, (int)(topLeft.X / tileSize) - 1);
        int startY = Math.Max(0, (int)(topLeft.Y / tileSize) - 1);
        int endX = Math.Min(world.Width - 1, (int)(bottomRight.X / tileSize) + 1);
        int endY = Math.Min(world.Height - 1, (int)(bottomRight.Y / tileSize) + 1);

        // LOD thresholds — graduated for smooth zoom transitions
        // Tier 1 (zoom < 0.35): flat color rectangles only
        // Tier 2 (0.35–0.6): flat colors + resource dots + structure outlines
        // Tier 3 (0.6–1.0): biome textures + larger resource dots + structure shapes + agent dots
        // Tier 4 (1.0+): full biome textures + individual resource sprites + agents + structures
        bool drawTexturedBiomes = zoom >= 0.6f;
        bool drawFullResourceSprites = zoom >= 1.0f;
        bool drawResourceDots = zoom >= 0.35f && !drawFullResourceSprites;
        bool drawStructures = zoom >= 0.5f;

        // Single pass: biomes → resources → structures per tile
        for (int y = startY; y <= endY; y++)
        {
            for (int x = startX; x <= endX; x++)
            {
                var tile = world.Grid[x, y];
                int px = x * tileSize;
                int py = y * tileSize;

                // ── Biome background ────────────────────────────────
                if (drawTexturedBiomes)
                {
                    if (useSprites && BiomeSpriteNames.TryGetValue(tile.Biome, out string? spriteName))
                    {
                        // Sprite biome tile — one draw call, perfect fit at 64x64
                        atlas!.DrawStretched(spriteName, px, py, tileSize, tileSize);
                    }
                    else
                    {
                        // Procedural biome texture fallback
                        uint seed = ProceduralSprites.Hash((uint)x * 7919, (uint)y * 104729);
                        switch (tile.Biome)
                        {
                            case BiomeType.Forest: ProceduralSprites.DrawForestBackground(px, py, tileSize, seed); break;
                            case BiomeType.Plains: ProceduralSprites.DrawPlainsBackground(px, py, tileSize, seed); break;
                            case BiomeType.Mountain: ProceduralSprites.DrawMountainBackground(px, py, tileSize, seed); break;
                            case BiomeType.Water: ProceduralSprites.DrawWaterBackground(px, py, tileSize, seed); break;
                            case BiomeType.Desert: ProceduralSprites.DrawDesertBackground(px, py, tileSize, seed); break;
                        }
                    }
                }
                else
                {
                    // Flat color — single draw call per tile (low zoom)
                    Rl.DrawRectangle(px, py, tileSize, tileSize, BiomeColors[tile.Biome]);
                }

                // Grid lines
                if (showGrid)
                    Rl.DrawRectangleLines(px, py, tileSize, tileSize, new Color(0, 0, 0, 50));

                // ── Resources ───────────────────────────────────────
                if (drawFullResourceSprites)
                {
                    uint baseSeed = ProceduralSprites.Hash((uint)x * 7919, (uint)y * 104729);
                    int resIndex = 0;
                    foreach (var kvp in tile.Resources)
                    {
                        if (kvp.Value <= 0) continue;
                        int capacity = tile.GetCapacity(kvp.Key);
                        int spriteCount = Math.Max(1, (int)Math.Ceiling((float)kvp.Value / capacity * MaxSpritesPerResource));

                        // Sub-region staggering: each resource type gets a quadrant to reduce overlap
                        // resIndex 0 → upper-left, 1 → lower-right, 2 → upper-right, 3+ → center
                        GetResourceSubRegion(resIndex, tileSize, out int regionOffX, out int regionOffY, out int regionW, out int regionH);

                        for (int i = 0; i < spriteCount; i++)
                        {
                            uint spriteSeed = ProceduralSprites.Hash(baseSeed, (uint)kvp.Key * 100 + (uint)i);
                            int margin = 4; // small margin within sub-region
                            int rangeX = Math.Max(1, regionW - margin * 2);
                            int rangeY = Math.Max(1, regionH - margin * 2);
                            int sx = px + regionOffX + margin + (int)(spriteSeed % (uint)rangeX);
                            int sy = py + regionOffY + margin + (int)((spriteSeed >> 8) % (uint)rangeY);

                            if (useSprites)
                            {
                                string resSprite = GetResourceSpriteName(kvp.Key, spriteSeed);
                                atlas!.DrawCentered(resSprite, sx, sy, 0.8f);
                            }
                            else
                            {
                                DrawResourceSpriteProcedural(kvp.Key, sx, sy, 0.7f);
                            }
                        }
                        resIndex++;
                    }
                }
                else if (drawResourceDots)
                {
                    // Resource dots — size scales with zoom for smooth transition
                    int dotIndex = 0;
                    foreach (var kvp in tile.Resources)
                    {
                        if (kvp.Value <= 0) continue;
                        if (ResourceDotColors.TryGetValue(kvp.Key, out Color dotColor))
                        {
                            int dotX = px + tileSize / 3 + (dotIndex % 2) * tileSize / 3;
                            int dotY = py + tileSize / 3 + (dotIndex / 2) * tileSize / 3;
                            int dotR = Math.Max(2, (int)(6 * zoom));
                            Rl.DrawCircle(dotX, dotY, dotR, dotColor);
                            dotIndex++;
                        }
                    }
                }

                // ── Structures ──────────────────────────────────────
                if (drawStructures)
                {
                    int tileCX = px + tileSize / 2;
                    int tileCY = py + tileSize / 2;

                    foreach (var kvp in tile.BuildProgress)
                    {
                        if (kvp.Key == "lean_to" || kvp.Key == "shelter")
                        {
                            if (useSprites)
                                atlas!.DrawCentered("structures_shelter_wip", tileCX, tileCY);
                            else
                                ProceduralSprites.DrawShelter(px, py, tileSize, false);
                        }
                    }
                    foreach (var structure in tile.Structures)
                    {
                        if (structure == "lean_to" || structure == "shelter")
                        {
                            if (useSprites)
                                atlas!.DrawCentered("structures_shelter", tileCX, tileCY);
                            else
                                ProceduralSprites.DrawShelter(px, py, tileSize, true);
                        }
                        else if (structure == "farm")
                        {
                            ProceduralSprites.DrawFarm(px, py, tileSize);
                        }
                    }
                }
            }
        }
    }

    private static string GetResourceSpriteName(ResourceType resource, uint seed)
    {
        return resource switch
        {
            ResourceType.Wood => (seed % 2 == 0) ? "resources_tree" : "resources_tree2",
            ResourceType.Berries => "resources_berry_bush",
            ResourceType.Stone => (seed % 2 == 0) ? "resources_stone" : "resources_rock",
            ResourceType.Ore => "resources_ore",
            ResourceType.Grain => "resources_grain",
            ResourceType.Fish => "resources_fish",
            ResourceType.Animals => "resources_animal",
            _ => "resources_stone"
        };
    }

    /// <summary>
    /// Assigns each resource type to a sub-region of the tile to reduce sprite overlap.
    /// Index 0 → upper-left quadrant, 1 → lower-right, 2 → upper-right, 3+ → center.
    /// </summary>
    private static void GetResourceSubRegion(int resIndex, int tileSize, out int offX, out int offY, out int w, out int h)
    {
        int half = tileSize / 2;
        int pad = 6; // padding from tile edges to prevent bleeding into neighbors
        switch (resIndex)
        {
            case 0: // upper-left quadrant
                offX = pad; offY = pad; w = half - pad; h = half - pad; break;
            case 1: // lower-right quadrant
                offX = half; offY = half; w = half - pad; h = half - pad; break;
            case 2: // upper-right quadrant
                offX = half; offY = pad; w = half - pad; h = half - pad; break;
            default: // center (fallback for 4+ resource types)
                offX = tileSize / 4; offY = tileSize / 4; w = half; h = half; break;
        }
    }

    private static void DrawResourceSpriteProcedural(ResourceType resource, int cx, int cy, float scale)
    {
        switch (resource)
        {
            case ResourceType.Wood: ProceduralSprites.DrawTree(cx, cy, scale); break;
            case ResourceType.Berries: ProceduralSprites.DrawBush(cx, cy, scale); break;
            case ResourceType.Stone: ProceduralSprites.DrawRock(cx, cy, scale); break;
            case ResourceType.Ore: ProceduralSprites.DrawOreVein(cx, cy, scale); break;
            case ResourceType.Grain: ProceduralSprites.DrawGrainStalks(cx, cy, scale); break;
            case ResourceType.Fish: ProceduralSprites.DrawFish(cx, cy, scale); break;
            case ResourceType.Animals: ProceduralSprites.DrawTree(cx, cy, scale * 0.8f); break; // placeholder
        }
    }
}
