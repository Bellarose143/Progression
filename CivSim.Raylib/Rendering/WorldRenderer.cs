using Raylib_cs;
using CivSim.Core;
using System.Numerics;
using Rl = Raylib_cs.Raylib;

namespace CivSim.Raylib.Rendering;

/// <summary>
/// Renders biome backgrounds, resource sprites, and structures.
/// Uses frustum culling and LOD (level-of-detail) based on zoom level.
/// PERF-01: Two-pass rendering with SpriteBatch for atlas sprites.
/// When a SpriteAtlas is available, uses texture sprites; otherwise falls back to procedural shapes.
/// </summary>
public class WorldRenderer
{
    /// <summary>GDD v1.7.2: MAX_SPRITES_PER_TILE = 5 per resource type.</summary>
    private static readonly int MaxSpritesPerResource = SimConfig.MaxSpritesPerTile;
    private readonly SpriteAtlas? atlas;
    private readonly StructureRegistry? structureRegistry;
    private readonly ResourceSpriteRegistry? resourceRegistry;

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
        { ResourceType.Meat, new Color(139, 90, 43, 255) },
    };

    public WorldRenderer(SpriteAtlas? atlas = null, StructureRegistry? structureRegistry = null,
                         ResourceSpriteRegistry? resourceRegistry = null)
    {
        this.atlas = atlas;
        this.structureRegistry = structureRegistry;
        this.resourceRegistry = resourceRegistry;
    }

    public void Render(World world, Camera2D camera, int tileSize, bool showGrid,
                       int screenWidth, int screenHeight, SpriteBatch? batch = null)
    {
        float zoom = camera.Zoom;
        bool useSprites = atlas is { IsLoaded: true };
        bool useBatch = useSprites && batch != null;

        // Frustum culling — calculate visible tile range
        Vector2 topLeft = Rl.GetScreenToWorld2D(new Vector2(0, 0), camera);
        Vector2 bottomRight = Rl.GetScreenToWorld2D(new Vector2(screenWidth, screenHeight), camera);

        // BUG-05 fix: use Math.Floor for negative coords (truncation skips tiles at world edge)
        int startX = Math.Max(0, (int)Math.Floor(topLeft.X / tileSize) - 1);
        int startY = Math.Max(0, (int)Math.Floor(topLeft.Y / tileSize) - 1);
        int endX = Math.Min(world.Width - 1, (int)Math.Floor(bottomRight.X / tileSize) + 1);
        int endY = Math.Min(world.Height - 1, (int)Math.Floor(bottomRight.Y / tileSize) + 1);

        // LOD thresholds — recalibrated for 350×350 world (0.08x–4.0x zoom range)
        // Below 0.1: flat rectangles only. 0.1–0.3: biome textures + resource dots.
        // Above 0.3: full sprites and structures.
        bool drawTexturedBiomes = zoom >= 0.1f;
        bool drawFullResourceSprites = zoom >= 0.3f;
        bool drawResourceDots = zoom >= 0.1f && !drawFullResourceSprites;
        bool drawStructures = zoom >= 0.15f;

        // ── Pass 1: Biome backgrounds ────────────────────────────────────
        if (useBatch && drawTexturedBiomes)
            batch!.Begin(atlas!.Spritesheet);

        for (int y = startY; y <= endY; y++)
        {
            for (int x = startX; x <= endX; x++)
            {
                var tile = world.Grid[x, y];
                int px = x * tileSize;
                int py = y * tileSize;

                if (drawTexturedBiomes)
                {
                    if (useBatch && BiomeSpriteNames.TryGetValue(tile.Biome, out string? spriteName))
                    {
                        atlas!.DrawStretchedBatched(batch!, spriteName, px, py, tileSize, tileSize);
                    }
                    else if (useSprites && BiomeSpriteNames.TryGetValue(tile.Biome, out string? spriteNameFallback))
                    {
                        atlas!.DrawStretched(spriteNameFallback, px, py, tileSize, tileSize);
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
            }
        }

        if (useBatch)
            batch!.Flush();

        // ── Pass 2: Resources + Structures ───────────────────────────────
        if (useBatch && (drawFullResourceSprites || drawStructures))
            batch!.Begin(atlas!.Spritesheet);

        for (int y = startY; y <= endY; y++)
        {
            for (int x = startX; x <= endX; x++)
            {
                var tile = world.Grid[x, y];
                int px = x * tileSize;
                int py = y * tileSize;

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

                        GetResourceSubRegion(resIndex, tileSize, out int regionOffX, out int regionOffY, out int regionW, out int regionH);

                        for (int i = 0; i < spriteCount; i++)
                        {
                            uint spriteSeed = ProceduralSprites.Hash(baseSeed, (uint)kvp.Key * 100 + (uint)i);
                            int margin = 4;
                            int rangeX = Math.Max(1, regionW - margin * 2);
                            int rangeY = Math.Max(1, regionH - margin * 2);
                            int sx = px + regionOffX + margin + (int)(spriteSeed % (uint)rangeX);
                            int sy = py + regionOffY + margin + (int)((spriteSeed >> 8) % (uint)rangeY);

                            // Try registry override first
                            if (TryDrawResourceFromRegistry(kvp.Key, sx, sy, useBatch, batch))
                                continue;

                            if (useBatch)
                            {
                                string resSprite = GetResourceSpriteName(kvp.Key, spriteSeed);
                                atlas!.DrawCenteredBatched(batch!, resSprite, sx, sy, 0.8f);
                            }
                            else if (useSprites)
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
                    int tileBottomY = py + tileSize;

                    // Build-in-progress: translucent placeholder at 50% alpha
                    foreach (var kvp in tile.BuildProgress)
                    {
                        RenderStructure(kvp.Key, px, py, tileSize, tileCX, tileBottomY,
                            useSprites, useBatch, batch, isProgress: true);
                    }

                    // Completed structures
                    foreach (var structure in tile.Structures)
                    {
                        // Skip non-visual markers
                        if (structure == "cleared") continue;

                        // Farm: use registry sprite if available, otherwise procedural
                        if (structure == "farm")
                        {
                            if (!RenderStructureFromRegistry("farm", px, py, tileSize, tileCX, tileBottomY,
                                    useSprites, useBatch, batch, isProgress: false))
                                RenderFarm(px, py, tileSize, tile);
                            continue;
                        }

                        RenderStructure(structure, px, py, tileSize, tileCX, tileBottomY,
                            useSprites, useBatch, batch, isProgress: false);
                    }
                }
            }
        }

        if (useBatch)
            batch!.Flush();
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
            ResourceType.Meat => "resources_animal",
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
        int pad = 6;
        switch (resIndex)
        {
            case 0:
                offX = pad; offY = pad; w = half - pad; h = half - pad; break;
            case 1:
                offX = half; offY = half; w = half - pad; h = half - pad; break;
            case 2:
                offX = half; offY = pad; w = half - pad; h = half - pad; break;
            default:
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
            case ResourceType.Meat:
                Rl.DrawCircle(cx, cy, (int)(6 * scale), new Color(139, 90, 43, 255));
                Rl.DrawCircle(cx, cy - (int)(3 * scale), (int)(3 * scale), new Color(180, 120, 60, 255));
                break;
        }
    }

    // ── Resource registry rendering ────────────────────────────────────

    /// <summary>
    /// Attempts to draw a resource using the ResourceSpriteRegistry override.
    /// Returns true if the registry handled the draw (standalone texture or atlas override);
    /// returns false to fall through to existing atlas/procedural rendering.
    /// </summary>
    private bool TryDrawResourceFromRegistry(ResourceType resourceType, int cx, int cy,
        bool useBatch, SpriteBatch? batch)
    {
        if (resourceRegistry == null)
            return false;

        if (!resourceRegistry.TryGetInfo(resourceType, out var info) || !info.HasSprite)
            return false;

        int drawX, drawY;
        if (info.Anchor == "bottom_center")
        {
            drawX = cx - info.Width / 2;
            drawY = cy - info.Height;
        }
        else // "center"
        {
            drawX = cx - info.Width / 2;
            drawY = cy - info.Height / 2;
        }

        // Try atlas sprite (scaled to registry size)
        if (info.AtlasSpriteName != null && atlas is { IsLoaded: true }
            && atlas.TryGetRegion(info.AtlasSpriteName, out var src))
        {
            var dest = new Rectangle(drawX, drawY, info.Width, info.Height);
            if (useBatch && batch != null)
                batch.DrawQuad(src, dest, Color.White);
            else
                Rl.DrawTexturePro(atlas.Spritesheet, src, dest, Vector2.Zero, 0, Color.White);
            return true;
        }

        // Try standalone texture
        if (info.StandaloneTexture is Texture2D tex && tex.Id > 0)
        {
            var src2 = new Rectangle(0, 0, tex.Width, tex.Height);
            var dest = new Rectangle(drawX, drawY, info.Width, info.Height);
            Rl.DrawTexturePro(tex, src2, dest, Vector2.Zero, 0, Color.White);
            return true;
        }

        return false;
    }

    // ── Structure rendering helpers ────────────────────────────────────

    /// <summary>
    /// Tries to render a structure from the registry (standalone texture or atlas).
    /// Returns true if a sprite was found and drawn, false to fall through to procedural.
    /// </summary>
    private bool RenderStructureFromRegistry(string structureName, int px, int py, int tileSize,
        int tileCX, int tileBottomY, bool useSprites, bool useBatch,
        SpriteBatch? batch, bool isProgress)
    {
        if (structureRegistry == null || !structureRegistry.TryGetStructure(structureName, out var regInfo))
            return false;
        if (!regInfo.HasSprite)
            return false;

        int drawX, drawY;
        if (regInfo.Anchor == "bottom_center")
        {
            drawX = tileCX - regInfo.Width / 2;
            drawY = tileBottomY - regInfo.Height;
        }
        else
        {
            int tileCY = py + tileSize / 2;
            drawX = tileCX - regInfo.Width / 2;
            drawY = tileCY - regInfo.Height / 2;
        }

        byte alpha = isProgress ? (byte)128 : (byte)255;
        Color tint = new Color(255, 255, 255, (int)alpha);

        // Try standalone texture first
        if (regInfo.StandaloneTexture is Texture2D standaloneTex && standaloneTex.Id > 0)
        {
            var src = new Rectangle(0, 0, standaloneTex.Width, standaloneTex.Height);
            var dest = new Rectangle(drawX, drawY, regInfo.Width, regInfo.Height);
            Rl.DrawTexturePro(standaloneTex, src, dest, Vector2.Zero, 0, tint);
            return true;
        }

        // Try atlas
        if (regInfo.AtlasSpriteName != null && useSprites && atlas is { IsLoaded: true }
            && atlas.TryGetRegion(regInfo.AtlasSpriteName, out _))
        {
            var src = GetAtlasRegion(regInfo.AtlasSpriteName);
            var dest = new Rectangle(drawX, drawY, regInfo.Width, regInfo.Height);
            if (useBatch && batch != null)
                batch.DrawQuad(src, dest, tint);
            else
                Rl.DrawTexturePro(atlas.Spritesheet, src, dest, Vector2.Zero, 0, tint);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Renders a single structure using registry lookup. Falls through to atlas sprite
    /// (scaled to registry size), standalone texture, or procedural placeholder.
    /// </summary>
    private void RenderStructure(string structureName, int px, int py, int tileSize,
        int tileCX, int tileBottomY, bool useSprites, bool useBatch,
        SpriteBatch? batch, bool isProgress)
    {
        // Resolve the render info from registry
        int width, height;
        string anchor;
        string? atlasSpriteName = null;
        bool hasSprite = false;
        StructureRegistry.StructureRenderInfo? info = null;

        if (structureRegistry != null && structureRegistry.TryGetStructure(structureName, out var regInfo))
        {
            info = regInfo;
            width = regInfo.Width;
            height = regInfo.Height;
            anchor = regInfo.Anchor;
            atlasSpriteName = regInfo.AtlasSpriteName;
            hasSprite = regInfo.HasSprite;
        }
        else
        {
            // Unknown structure — use default sizing
            width = 40;
            height = 40;
            anchor = "center";
            // Try atlas with standard naming convention
            atlasSpriteName = "structures_" + structureName;
            if (atlas is { IsLoaded: true } && atlas.TryGetRegion(atlasSpriteName, out _))
                hasSprite = true;
            else
                atlasSpriteName = null;
        }

        // Calculate draw position based on anchor
        int drawX, drawY;
        if (anchor == "bottom_center")
        {
            drawX = tileCX - width / 2;
            drawY = tileBottomY - height;
        }
        else // "center"
        {
            int tileCY = py + tileSize / 2;
            drawX = tileCX - width / 2;
            drawY = tileCY - height / 2;
        }

        byte alpha = isProgress ? (byte)128 : (byte)255;
        Color tint = new Color(255, 255, 255, (int)alpha);

        // For build-in-progress, try the dedicated progress sprite first
        if (isProgress && useSprites && atlas is { IsLoaded: true })
        {
            string progressName = "structures_" + structureName + "_progress";
            if (!atlas.TryGetRegion(progressName, out _))
                progressName = "structures_shelter_progress"; // Fallback to generic progress sprite

            if (atlas.TryGetRegion(progressName, out _))
            {
                var dest = new Rectangle(drawX, drawY, width, height);
                if (useBatch)
                    batch!.DrawQuad(GetAtlasRegion(progressName), dest, tint);
                else
                    Rl.DrawTexturePro(atlas.Spritesheet, GetAtlasRegion(progressName), dest, Vector2.Zero, 0, tint);
                return;
            }
        }

        // Try atlas sprite (scaled to registry size)
        if (hasSprite && atlasSpriteName != null && useSprites && atlas is { IsLoaded: true }
            && atlas.TryGetRegion(atlasSpriteName, out _))
        {
            var src = GetAtlasRegion(atlasSpriteName);
            var dest = new Rectangle(drawX, drawY, width, height);
            if (useBatch)
                batch!.DrawQuad(src, dest, tint);
            else
                Rl.DrawTexturePro(atlas.Spritesheet, src, dest, Vector2.Zero, 0, tint);
            return;
        }

        // Try standalone texture from registry
        if (info?.StandaloneTexture is Texture2D standaloneTex && standaloneTex.Id > 0)
        {
            var src = new Rectangle(0, 0, standaloneTex.Width, standaloneTex.Height);
            var dest = new Rectangle(drawX, drawY, width, height);
            Rl.DrawTexturePro(standaloneTex, src, dest, Vector2.Zero, 0, tint);
            return;
        }

        // Procedural placeholder
        DrawStructurePlaceholder(structureName, drawX, drawY, width, height, alpha);
    }

    private Rectangle GetAtlasRegion(string name)
    {
        atlas!.TryGetRegion(name, out var region);
        return region;
    }

    /// <summary>
    /// Draws a clean geometric placeholder for a structure when no sprite is available.
    /// </summary>
    private static void DrawStructurePlaceholder(string name, int x, int y, int w, int h, byte alpha)
    {
        switch (name)
        {
            case "lean_to":
                // Triangle outline (tent shape)
                DrawTriangleOutline(x, y, w, h, new Color(139, 105, 20, (int)alpha));
                break;

            case "campfire":
                // Filled circle with glow
                int cfCX = x + w / 2;
                int cfCY = y + h / 2;
                Rl.DrawCircle(cfCX, cfCY, w / 2, new Color(255, 102, 0, (int)(alpha * 0.3f)));
                Rl.DrawCircle(cfCX, cfCY, w / 3, new Color(255, 102, 0, (int)alpha));
                Rl.DrawCircle(cfCX, cfCY, w / 5, new Color(255, 200, 50, (int)alpha));
                break;

            case "farm":
                // Brown rectangles (tilled rows) — should not reach here (farm has special handler)
                DrawFarmPlaceholder(x, y, w, h, alpha);
                break;

            case "animal_pen":
                // Rectangle outline with corner posts
                Rl.DrawRectangleLines(x, y, w, h, new Color(160, 82, 45, (int)alpha));
                Rl.DrawRectangleLines(x + 1, y + 1, w - 2, h - 2, new Color(160, 82, 45, (int)alpha));
                int postR = 3;
                Color postColor = new Color(120, 60, 30, (int)alpha);
                Rl.DrawCircle(x, y, postR, postColor);
                Rl.DrawCircle(x + w, y, postR, postColor);
                Rl.DrawCircle(x, y + h, postR, postColor);
                Rl.DrawCircle(x + w, y + h, postR, postColor);
                break;

            case "granary":
                // Rounded rectangle (silo shape)
                Rl.DrawRectangleRounded(
                    new Rectangle(x, y + h / 4, w, h * 3 / 4),
                    0.3f, 4, new Color(210, 180, 140, (int)alpha));
                // Roof cap
                Rl.DrawRectangle(x + w / 6, y, w * 2 / 3, h / 4 + 2,
                    new Color(139, 90, 43, (int)alpha));
                break;

            case "improved_shelter":
            case "reinforced_shelter":
                // House outline (rect + triangle roof) — larger
                DrawHouseOutline(x, y, w, h, new Color(92, 51, 23, (int)alpha));
                break;

            case "walls":
                // Thick horizontal bar
                Rl.DrawRectangle(x, y + h / 4, w, h / 2, new Color(128, 128, 128, (int)alpha));
                Rl.DrawRectangleLines(x, y + h / 4, w, h / 2, new Color(100, 100, 100, (int)alpha));
                break;

            case "shelter":
                // House outline (smaller)
                DrawHouseOutline(x, y, w, h, new Color(139, 105, 20, (int)alpha));
                break;

            default:
                // Unknown structure — generic rectangle
                Rl.DrawRectangleLines(x, y, w, h, new Color(180, 180, 180, (int)alpha));
                break;
        }
    }

    /// <summary>Draws a triangle (tent) outline shape.</summary>
    private static void DrawTriangleOutline(int x, int y, int w, int h, Color color)
    {
        Vector2 top = new(x + w / 2, y);
        Vector2 bl = new(x, y + h);
        Vector2 br = new(x + w, y + h);
        Rl.DrawLineEx(top, bl, 2, color);
        Rl.DrawLineEx(top, br, 2, color);
        Rl.DrawLineEx(bl, br, 2, color);
        // Cross-bar at 2/3 height
        float barY = y + h * 2f / 3f;
        float barLeft = x + w / 6f;
        float barRight = x + w * 5f / 6f;
        Rl.DrawLineEx(new Vector2(barLeft, barY), new Vector2(barRight, barY), 1.5f, color);
    }

    /// <summary>Draws a house outline (rectangular body + triangular roof).</summary>
    private static void DrawHouseOutline(int x, int y, int w, int h, Color color)
    {
        int roofH = h * 2 / 5;
        int wallY = y + roofH;
        int wallH = h - roofH;

        // Walls
        Rl.DrawRectangle(x + 2, wallY, w - 4, wallH, color);
        Rl.DrawRectangleLines(x + 2, wallY, w - 4, wallH, color);

        // Roof triangle
        Vector2 roofTop = new(x + w / 2, y);
        Vector2 roofLeft = new(x, wallY);
        Vector2 roofRight = new(x + w, wallY);
        Rl.DrawTriangle(roofTop, roofRight, roofLeft, color);

        // Door
        int doorW = w / 5;
        int doorH = wallH * 2 / 3;
        Rl.DrawRectangle(x + w / 2 - doorW / 2, wallY + wallH - doorH, doorW, doorH,
            new Color((int)40, (int)25, (int)10, (int)color.A));
    }

    /// <summary>Draws a farm placeholder with tilled rows.</summary>
    private static void DrawFarmPlaceholder(int x, int y, int w, int h, byte alpha)
    {
        // Dark brown tilled area
        Rl.DrawRectangle(x, y, w, h, new Color(101, 67, 33, (int)(alpha * 0.6f)));
        // Furrow lines
        Color furrow = new Color(80, 50, 25, (int)alpha);
        int lineCount = 5;
        int spacing = h / (lineCount + 1);
        for (int i = 1; i <= lineCount; i++)
        {
            int ly = y + i * spacing;
            Rl.DrawLineEx(new Vector2(x + 4, ly), new Vector2(x + w - 4, ly), 2, furrow);
        }
    }

    /// <summary>
    /// Renders a farm tile with tilled earth pattern and grain visualization.
    /// </summary>
    private void RenderFarm(int px, int py, int tileSize, Tile tile)
    {
        int farmSize = 56;
        int farmX = px + (tileSize - farmSize) / 2;
        int farmY = py + (tileSize - farmSize) / 2;

        // Dark brown tilled area
        Rl.DrawRectangle(farmX, farmY, farmSize, farmSize, new Color(101, 67, 33, 150));

        // Furrow lines (horizontal)
        Color furrow = new Color(80, 50, 25, 200);
        int lineCount = 5;
        int spacing = farmSize / (lineCount + 1);
        for (int i = 1; i <= lineCount; i++)
        {
            int ly = farmY + i * spacing;
            Rl.DrawLineEx(new Vector2(farmX + 4, ly), new Vector2(farmX + farmSize - 4, ly), 2, furrow);
        }

        // Grain quantity shown via tile info panel — no overlay dots needed
    }
}
