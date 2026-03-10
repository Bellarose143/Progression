using Raylib_cs;
using CivSim.Core;
using System.Numerics;
using System.Text.Json;
using Rl = Raylib_cs.Raylib;

namespace CivSim.Raylib.Rendering;

/// <summary>
/// D26: Registry for animal spritesheets. Loads animals.json and resolves
/// per-species textures. Falls back gracefully when sprites are missing.
/// </summary>
public class AnimalSpriteRegistry : IDisposable
{
    private readonly Dictionary<string, AnimalSpriteEntry> _entries = new();
    private bool _loaded;

    public bool IsLoaded => _loaded;

    public void Load()
    {
        string[] searchPaths =
        {
            Path.Combine(AppContext.BaseDirectory, "Assets"),
            "Assets",
            Path.Combine("CivSim.Raylib", "Assets"),
        };

        foreach (var basePath in searchPaths)
        {
            string jsonPath = Path.Combine(basePath, "Sprites", "Animals", "animals.json");
            if (!File.Exists(jsonPath)) continue;

            try
            {
                var json = File.ReadAllText(jsonPath);
                using var doc = JsonDocument.Parse(json);
                string spriteDir = Path.GetDirectoryName(jsonPath)!;

                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    string speciesName = prop.Name;
                    var obj = prop.Value;
                    string sheetFile = obj.GetProperty("spritesheet").GetString()!;
                    int frameSize = obj.TryGetProperty("frame_size", out var fsVal) ? fsVal.GetInt32() : 0;

                    string sheetPath = Path.Combine(spriteDir, sheetFile);
                    if (!File.Exists(sheetPath))
                    {
                        Console.WriteLine($"[AnimalSpriteRegistry] Spritesheet not found: {sheetPath}");
                        continue;
                    }

                    var texture = Rl.LoadTexture(sheetPath);

                    // Auto-detect frame size from image dimensions if not specified
                    if (frameSize <= 0)
                    {
                        frameSize = texture.Width / 3; // 3 columns standard
                    }

                    int framesPerDir = texture.Width / frameSize;
                    int rows = texture.Height / frameSize; // Expect 4 rows: down, left, right, up

                    // Parse optional variants
                    List<AnimalVariant>? variants = null;
                    if (obj.TryGetProperty("variants", out var variantsArray) && variantsArray.ValueKind == JsonValueKind.Array)
                    {
                        variants = new List<AnimalVariant>();
                        float cumulativeWeight = 0f;

                        foreach (var variantObj in variantsArray.EnumerateArray())
                        {
                            string varSheetFile = variantObj.GetProperty("spritesheet").GetString()!;
                            float weight = (float)variantObj.GetProperty("weight").GetDouble();
                            int varFrameSize = variantObj.TryGetProperty("frame_size", out var vfs)
                                ? vfs.GetInt32()
                                : frameSize; // inherit base frame_size if not specified

                            string varSheetPath = Path.Combine(spriteDir, varSheetFile);
                            if (!File.Exists(varSheetPath))
                            {
                                Console.WriteLine($"[AnimalSpriteRegistry] Variant spritesheet not found, skipping: {varSheetPath}");
                                continue;
                            }

                            var varTexture = Rl.LoadTexture(varSheetPath);

                            // Auto-detect variant frame size if not specified
                            if (varFrameSize <= 0)
                            {
                                varFrameSize = varTexture.Width / 3;
                            }

                            int varFramesPerDir = varTexture.Width / varFrameSize;
                            int varRows = varTexture.Height / varFrameSize;

                            cumulativeWeight += weight;
                            variants.Add(new AnimalVariant
                            {
                                Texture = varTexture,
                                FrameSize = varFrameSize,
                                FramesPerDirection = varFramesPerDir,
                                Rows = varRows,
                                CumulativeWeight = cumulativeWeight,
                            });

                            Console.WriteLine($"[AnimalSpriteRegistry]   Variant {varSheetFile}: {varFramesPerDir} frames, weight={weight:F2} (cumulative={cumulativeWeight:F2})");
                        }

                        if (variants.Count == 0)
                            variants = null; // no valid variants loaded, fall back to base only
                    }

                    _entries[speciesName] = new AnimalSpriteEntry
                    {
                        Texture = texture,
                        FrameSize = frameSize,
                        FramesPerDirection = framesPerDir,
                        Rows = rows,
                        Variants = variants
                    };

                    Console.WriteLine($"[AnimalSpriteRegistry] Loaded {speciesName}: {framesPerDir} frames, {rows} rows from {sheetFile}{(variants != null ? $" + {variants.Count} variant(s)" : "")}");
                }

                _loaded = true;
                Console.WriteLine($"[AnimalSpriteRegistry] Loaded {_entries.Count} species from {jsonPath}");
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AnimalSpriteRegistry] Error loading {jsonPath}: {ex.Message}");
            }
        }

        Console.WriteLine("[AnimalSpriteRegistry] No animal sprites found — using colored dot fallback.");
    }

    /// <summary>Try to get sprite entry for a species (lowercase name lookup).</summary>
    public bool TryGetEntry(AnimalSpecies species, out AnimalSpriteEntry entry)
    {
        string key = species switch
        {
            AnimalSpecies.Deer => "deer",
            AnimalSpecies.Wolf => "wolf",
            AnimalSpecies.Rabbit => "rabbit",
            AnimalSpecies.Boar => "boar",
            AnimalSpecies.Cow => "cow",
            AnimalSpecies.Sheep => "sheep",
            _ => species.ToString().ToLowerInvariant()
        };
        return _entries.TryGetValue(key, out entry!);
    }

    /// <summary>
    /// Resolves the correct sprite entry (base or variant) for a specific animal.
    /// Uses deterministic hashing on animal ID so the same animal always gets the same variant.
    /// </summary>
    public bool TryGetEntryForAnimal(AnimalSpecies species, int animalId, out AnimalSpriteEntry entry)
    {
        if (!TryGetEntry(species, out var baseEntry))
        {
            entry = null!;
            return false;
        }

        if (baseEntry.Variants == null || baseEntry.Variants.Count == 0)
        {
            entry = baseEntry;
            return true;
        }

        // Deterministic variant selection via Knuth multiplicative hash
        unchecked
        {
            int hash = animalId * (int)2654435761u;
            float roll = (hash & 0x7FFFFFFF) / (float)0x7FFFFFFF;

            foreach (var variant in baseEntry.Variants)
            {
                if (roll < variant.CumulativeWeight)
                {
                    // Return a temporary entry with the variant's texture/dimensions
                    entry = new AnimalSpriteEntry
                    {
                        Texture = variant.Texture,
                        FrameSize = variant.FrameSize,
                        FramesPerDirection = variant.FramesPerDirection,
                        Rows = variant.Rows,
                        Variants = null // variants don't have sub-variants
                    };
                    return true;
                }
            }
        }

        // Roll fell in the base range (1.0 - sum(variant_weights))
        entry = baseEntry;
        return true;
    }

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            Rl.UnloadTexture(entry.Texture);
            if (entry.Variants != null)
            {
                foreach (var variant in entry.Variants)
                    Rl.UnloadTexture(variant.Texture);
            }
        }
        _entries.Clear();
    }
}

public class AnimalSpriteEntry
{
    public Texture2D Texture { get; set; }
    public int FrameSize { get; set; }
    public int FramesPerDirection { get; set; }
    public int Rows { get; set; } // Expected: 4 (down=0, left=1, right=2, up=3)
    public List<AnimalVariant>? Variants { get; set; } // null if no variants defined
}

/// <summary>
/// A visual variant for an animal species (e.g., doe vs buck for deer).
/// Each variant has its own spritesheet and a cumulative weight for deterministic selection.
/// </summary>
public class AnimalVariant
{
    public Texture2D Texture { get; set; }
    public int FrameSize { get; set; }
    public int FramesPerDirection { get; set; }
    public int Rows { get; set; }
    public float CumulativeWeight { get; set; } // pre-computed: sum of weights up to and including this variant
}

/// <summary>
/// D25a/D26: Renders animal entities as colored dots (low LOD) or sprites (high LOD).
/// D26: Adds smooth move interpolation and spritesheet support.
/// Called between WorldRenderer and AgentRenderer in the render pipeline.
/// </summary>
public class AnimalRenderer : IDisposable
{
    private readonly SpriteAtlas atlas;
    private readonly AnimalSpriteRegistry spriteRegistry;

    // Species -> dot color mapping
    private static readonly Dictionary<AnimalSpecies, Color> SpeciesColors = new()
    {
        [AnimalSpecies.Rabbit] = new Color(181, 137, 99, 255),    // light brown
        [AnimalSpecies.Deer] = new Color(210, 180, 140, 255),     // tan
        [AnimalSpecies.Cow] = new Color(180, 140, 100, 255),       // brown
        [AnimalSpecies.Sheep] = new Color(240, 240, 240, 255),     // white
        [AnimalSpecies.Boar] = new Color(101, 67, 33, 255),       // dark brown
        [AnimalSpecies.Wolf] = new Color(150, 150, 150, 255),     // gray
    };

    // Carcass color
    private static readonly Color CarcassColor = new Color(139, 69, 19, 180); // red-brown, semi-transparent

    public AnimalRenderer(SpriteAtlas atlas)
    {
        this.atlas = atlas;
        spriteRegistry = new AnimalSpriteRegistry();
        spriteRegistry.Load();
    }

    /// <summary>
    /// D26: Computes the smoothstep-interpolated visual position for an animal.
    /// </summary>
    private static (float px, float py) GetInterpolatedPosition(Animal animal, int tileSize, int currentTick, float lerpAlpha)
    {
        if (animal.IsMoving && currentTick < animal.MoveEndTick)
        {
            float totalDuration = animal.MoveEndTick - animal.MoveStartTick;
            if (totalDuration > 0)
            {
                float elapsed = (currentTick - animal.MoveStartTick) + lerpAlpha;
                float t = Math.Clamp(elapsed / totalDuration, 0f, 1f);
                float smooth = t * t * (3f - 2f * t);
                float interpX = animal.MoveOrigin.X + (animal.MoveDestination.X - animal.MoveOrigin.X) * smooth;
                float interpY = animal.MoveOrigin.Y + (animal.MoveDestination.Y - animal.MoveOrigin.Y) * smooth;
                return (interpX * tileSize, interpY * tileSize);
            }
        }
        return (animal.X * tileSize, animal.Y * tileSize);
    }

    /// <summary>
    /// D26: Maps FacingDirection to spritesheet row index.
    /// Row 0=down, 1=left, 2=right, 3=up.
    /// </summary>
    private static int GetDirectionRow((int Dx, int Dy) facing)
    {
        if (facing.Dy > 0) return 0;  // down
        if (facing.Dx < 0) return 1;  // left
        if (facing.Dx > 0) return 2;  // right
        if (facing.Dy < 0) return 3;  // up
        return 0; // default: facing down
    }

    public void Render(World world, Camera2D camera, int tileSize, int screenWidth, int screenHeight,
                       int currentTick, float lerpAlpha)
    {
        float zoom = camera.Zoom;

        // LOD < 0.35: nothing (too zoomed out)
        if (zoom < 0.35f) return;

        // Calculate visible tile range for frustum culling (same pattern as WorldRenderer)
        var topLeft = Rl.GetScreenToWorld2D(new Vector2(0, 0), camera);
        var bottomRight = Rl.GetScreenToWorld2D(new Vector2(screenWidth, screenHeight), camera);
        int minTX = Math.Max(0, (int)(topLeft.X / tileSize) - 1);
        int minTY = Math.Max(0, (int)(topLeft.Y / tileSize) - 1);
        int maxTX = Math.Min(world.Width - 1, (int)(bottomRight.X / tileSize) + 1);
        int maxTY = Math.Min(world.Height - 1, (int)(bottomRight.Y / tileSize) + 1);

        // Render animals
        foreach (var animal in world.Animals)
        {
            if (!animal.IsAlive) continue;
            if (animal.X < minTX || animal.X > maxTX || animal.Y < minTY || animal.Y > maxTY) continue;

            // D26: Interpolated position
            var (px, py) = GetInterpolatedPosition(animal, tileSize, currentTick, lerpAlpha);

            if (zoom < 1.0f)
            {
                // LOD 0.35-1.0: colored dots
                var color = SpeciesColors.GetValueOrDefault(animal.Species, Color.White);
                int dotSize = Math.Max(2, (int)(4 * zoom));
                int offset = tileSize / 2 - dotSize / 2;
                // Offset by animal ID to spread dots within a tile
                int spreadX = (animal.Id * 7) % (tileSize / 2) - tileSize / 4;
                int spreadY = (animal.Id * 13) % (tileSize / 2) - tileSize / 4;
                Rl.DrawRectangle((int)px + offset + spreadX, (int)py + offset + spreadY, dotSize, dotSize, color);
            }
            else
            {
                // LOD >= 1.0: try spritesheet first, fall back to colored circle
                bool drewSprite = false;

                if (spriteRegistry.IsLoaded && spriteRegistry.TryGetEntryForAnimal(animal.Species, animal.Id, out var spriteEntry))
                {
                    // Determine direction row
                    int row = GetDirectionRow(animal.FacingDirection);
                    if (row >= spriteEntry.Rows) row = 0; // clamp

                    // Determine animation frame
                    int frame = 0;
                    if (animal.IsMoving && currentTick < animal.MoveEndTick && animal.State != AnimalState.Sleeping)
                    {
                        float totalDuration = animal.MoveEndTick - animal.MoveStartTick;
                        if (totalDuration > 0)
                        {
                            float moveProgress = (currentTick - animal.MoveStartTick + lerpAlpha) / totalDuration;
                            frame = (int)(moveProgress * spriteEntry.FramesPerDirection) % spriteEntry.FramesPerDirection;
                        }
                    }
                    // Idle/sleeping: frame 0

                    // Source rectangle from spritesheet
                    int fs = spriteEntry.FrameSize;
                    var srcRect = new Rectangle(frame * fs, row * fs, fs, fs);

                    // Draw centered on interpolated tile position, scaled to fit tile
                    int drawSize = Math.Min(fs, tileSize - 8);
                    int drawX = (int)px + tileSize / 2 - drawSize / 2;
                    int drawY = (int)py + tileSize / 2 - drawSize / 2;
                    var destRect = new Rectangle(drawX, drawY, drawSize, drawSize);

                    // Sleeping animals: dimmed tint
                    Color tint = animal.State == AnimalState.Sleeping
                        ? new Color(180, 180, 180, 180)
                        : Color.White;

                    Rl.DrawTexturePro(spriteEntry.Texture, srcRect, destRect, Vector2.Zero, 0f, tint);
                    drewSprite = true;
                }

                if (!drewSprite)
                {
                    // Fallback: colored circle with direction indicator
                    var color = SpeciesColors.GetValueOrDefault(animal.Species, Color.White);

                    // Size: animals are smaller than a tile
                    int spriteSize = tileSize / 2;
                    int offsetX = tileSize / 4;
                    int offsetY = tileSize / 4;

                    // Spread animals within tile so they don't stack
                    int spreadX = (animal.Id * 7) % (tileSize / 3) - tileSize / 6;
                    int spreadY = (animal.Id * 13) % (tileSize / 3) - tileSize / 6;

                    // Sleeping animals are dimmed
                    if (animal.State == AnimalState.Sleeping)
                        color = new Color(color.R, color.G, color.B, (byte)128);

                    // Draw as a small colored circle with a darker outline
                    int cx = (int)px + offsetX + spreadX + spriteSize / 2;
                    int cy = (int)py + offsetY + spreadY + spriteSize / 2;
                    int radius = Math.Max(3, spriteSize / 4);

                    // Fleeing animals get a red tint
                    if (animal.State == AnimalState.Fleeing)
                        Rl.DrawCircle(cx, cy, radius + 1, Color.Red);

                    Rl.DrawCircle(cx, cy, radius, color);

                    // Direction indicator (small line showing facing)
                    if (animal.FacingDirection != (0, 0))
                    {
                        int lineLen = radius;
                        int endX = cx + animal.FacingDirection.Dx * lineLen;
                        int endY = cy + animal.FacingDirection.Dy * lineLen;
                        Rl.DrawLine(cx, cy, endX, endY, Color.DarkGray);
                    }
                }
            }
        }

        // Render carcasses
        foreach (var carcass in world.Carcasses)
        {
            if (!carcass.IsActive) continue;
            if (carcass.X < minTX || carcass.X > maxTX || carcass.Y < minTY || carcass.Y > maxTY) continue;

            float cpx = carcass.X * tileSize;
            float cpy = carcass.Y * tileSize;

            if (zoom < 1.0f)
            {
                int dotSize = Math.Max(2, (int)(3 * zoom));
                Rl.DrawRectangle((int)cpx + tileSize / 2, (int)cpy + tileSize / 2, dotSize, dotSize, CarcassColor);
            }
            else
            {
                // D26: If we have a sprite for the carcass species, draw darkened version
                // For now, use the X mark fallback (no carcass sprites yet)
                int cx = (int)cpx + tileSize / 2;
                int cy = (int)cpy + tileSize / 2;
                int sz = tileSize / 6;
                Rl.DrawLine(cx - sz, cy - sz, cx + sz, cy + sz, CarcassColor);
                Rl.DrawLine(cx + sz, cy - sz, cx - sz, cy + sz, CarcassColor);
            }
        }
    }

    /// <summary>Fix 5: Renders health bars above animals that are currently targeted in combat by any agent.</summary>
    public void RenderCombatHealthBars(World world, List<Agent> agents, int tileSize, Camera2D camera)
    {
        if (camera.Zoom < 1.2f) return;

        // Collect animal IDs currently targeted in combat
        var combatTargetIds = new HashSet<int>();
        foreach (var agent in agents)
        {
            if (agent.IsAlive && agent.CombatTargetAnimalId.HasValue)
                combatTargetIds.Add(agent.CombatTargetAnimalId.Value);
        }
        if (combatTargetIds.Count == 0) return;

        foreach (var animal in world.Animals)
        {
            if (!animal.IsAlive) continue;
            if (!combatTargetIds.Contains(animal.Id)) continue;

            int cx = animal.X * tileSize + tileSize / 2;
            int cy = animal.Y * tileSize + tileSize / 4; // above the animal

            int barW = 20;
            int barH = 3;
            int x = cx - barW / 2;
            int y = cy - 6;

            // Background
            Rl.DrawRectangle(x, y, barW, barH, new Color(40, 40, 40, 200));

            // Fill: green to yellow to red
            float pct = Math.Clamp((float)animal.Health / animal.MaxHealth, 0f, 1f);
            int fillW = (int)(barW * pct);
            Color fillColor;
            if (pct > 0.5f)
            {
                float t = (pct - 0.5f) * 2f;
                fillColor = new Color((int)(255 * (1f - t)), 255, 0, 255);
            }
            else
            {
                float t = pct * 2f;
                fillColor = new Color(255, (int)(255 * t), 0, 255);
            }
            if (fillW > 0)
                Rl.DrawRectangle(x, y, fillW, barH, fillColor);
        }
    }

    public void Dispose()
    {
        spriteRegistry.Dispose();
    }
}
