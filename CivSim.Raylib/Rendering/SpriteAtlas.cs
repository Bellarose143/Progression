using Raylib_cs;
using System.Numerics;
using System.Text.Json;
using Rl = Raylib_cs.Raylib;

namespace CivSim.Raylib.Rendering;

/// <summary>
/// Loads the combined spritesheet + atlas.json and provides methods to draw sprites by name.
/// Falls back gracefully if assets are not found (renderers check IsLoaded).
/// </summary>
public class SpriteAtlas : IDisposable
{
    private Texture2D spritesheet;
    private readonly Dictionary<string, Rectangle> regions = new();

    public bool IsLoaded { get; private set; }

    /// <summary>PERF-01: Exposes the spritesheet texture for SpriteBatch.</summary>
    public Texture2D Spritesheet => spritesheet;

    /// <summary>PERF-01: Looks up a named sprite region for external batched draw calls.</summary>
    public bool TryGetRegion(string name, out Rectangle region) => regions.TryGetValue(name, out region);

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
            string sheetPath = Path.Combine(basePath, "spritesheet.png");
            string atlasPath = Path.Combine(basePath, "atlas.json");

            if (File.Exists(sheetPath) && File.Exists(atlasPath))
            {
                spritesheet = Rl.LoadTexture(sheetPath);
                ParseAtlas(atlasPath);
                IsLoaded = true;
                Console.WriteLine($"[SpriteAtlas] Loaded {regions.Count} sprites from {basePath}");
                return;
            }
        }

        Console.WriteLine("[SpriteAtlas] Assets not found — using procedural rendering.");
    }

    private void ParseAtlas(string atlasPath)
    {
        try
        {
            var json = File.ReadAllText(atlasPath);
            using var doc = JsonDocument.Parse(json);

            int loaded = 0, errors = 0;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                try
                {
                    var obj = prop.Value;
                    float x = obj.GetProperty("x").GetInt32();
                    float y = obj.GetProperty("y").GetInt32();
                    float w = obj.GetProperty("w").GetInt32();
                    float h = obj.GetProperty("h").GetInt32();
                    regions[prop.Name] = new Rectangle(x, y, w, h);
                    loaded++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[SpriteAtlas] WARNING: Malformed entry '{prop.Name}': {ex.Message}");
                    errors++;
                }
            }

            if (errors > 0)
                Console.WriteLine($"[SpriteAtlas] {loaded} sprites loaded, {errors} entries skipped due to errors.");
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"[SpriteAtlas] ERROR: Failed to parse atlas.json: {ex.Message}");
            regions.Clear();
        }
    }

    /// <summary>Draw sprite at world position (top-left corner), native size.</summary>
    public void Draw(string name, int x, int y)
    {
        if (!IsLoaded || !regions.TryGetValue(name, out var src)) return;
        Rl.DrawTextureRec(spritesheet, src, new Vector2(x, y), Color.White);
    }

    /// <summary>Draw sprite stretched to fill a destination rectangle.</summary>
    public void DrawStretched(string name, int x, int y, int w, int h, Color? tint = null)
    {
        if (!IsLoaded || !regions.TryGetValue(name, out var src)) return;
        var dest = new Rectangle(x, y, w, h);
        Rl.DrawTexturePro(spritesheet, src, dest, Vector2.Zero, 0, tint ?? Color.White);
    }

    /// <summary>Draw sprite centered at (cx, cy) with optional scale and tint.</summary>
    public void DrawCentered(string name, int cx, int cy, float scale = 1f, Color? tint = null)
    {
        if (!IsLoaded || !regions.TryGetValue(name, out var src)) return;
        float dw = src.Width * scale;
        float dh = src.Height * scale;
        var dest = new Rectangle(cx - dw / 2f, cy - dh / 2f, dw, dh);
        Rl.DrawTexturePro(spritesheet, src, dest, Vector2.Zero, 0, tint ?? Color.White);
    }

    /// <summary>Draw sprite centered with alpha transparency.</summary>
    public void DrawCenteredAlpha(string name, int cx, int cy, float scale, byte alpha)
    {
        DrawCentered(name, cx, cy, scale, new Color(255, 255, 255, (int)alpha));
    }

    /// <summary>GDD v1.7.2: Draw sprite with bottom edge at bottomY, centered horizontally at cx.
    /// Used for agent rendering so heads don't get clipped by adjacent tile backgrounds.</summary>
    public void DrawBottomCentered(string name, int cx, int bottomY, float scale = 1f, Color? tint = null)
    {
        if (!IsLoaded || !regions.TryGetValue(name, out var src)) return;
        float dw = src.Width * scale;
        float dh = src.Height * scale;
        var dest = new Rectangle(cx - dw / 2f, bottomY - dh, dw, dh);
        Rl.DrawTexturePro(spritesheet, src, dest, Vector2.Zero, 0, tint ?? Color.White);
    }

    // ── PERF-01: Batched draw methods ──────────────────────────────────

    /// <summary>Batched version of DrawStretched — appends quad to SpriteBatch.</summary>
    public void DrawStretchedBatched(SpriteBatch batch, string name, int x, int y, int w, int h, Color? tint = null)
    {
        if (!IsLoaded || !regions.TryGetValue(name, out var src)) return;
        batch.DrawQuad(src, new Rectangle(x, y, w, h), tint ?? Color.White);
    }

    /// <summary>Batched version of DrawCentered — appends quad to SpriteBatch.</summary>
    public void DrawCenteredBatched(SpriteBatch batch, string name, int cx, int cy, float scale = 1f, Color? tint = null)
    {
        if (!IsLoaded || !regions.TryGetValue(name, out var src)) return;
        float dw = src.Width * scale;
        float dh = src.Height * scale;
        batch.DrawQuad(src, new Rectangle(cx - dw / 2f, cy - dh / 2f, dw, dh), tint ?? Color.White);
    }

    /// <summary>Batched version of DrawBottomCentered — appends quad to SpriteBatch.</summary>
    public void DrawBottomCenteredBatched(SpriteBatch batch, string name, int cx, int bottomY, float scale = 1f, Color? tint = null)
    {
        if (!IsLoaded || !regions.TryGetValue(name, out var src)) return;
        float dw = src.Width * scale;
        float dh = src.Height * scale;
        batch.DrawQuad(src, new Rectangle(cx - dw / 2f, bottomY - dh, dw, dh), tint ?? Color.White);
    }

    public void Dispose()
    {
        if (IsLoaded)
        {
            Rl.UnloadTexture(spritesheet);
            spritesheet = default; // Null the handle to prevent use-after-dispose
            IsLoaded = false;
        }
    }
}
