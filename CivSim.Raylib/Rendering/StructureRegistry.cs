using Raylib_cs;
using System.Text.Json;
using Rl = Raylib_cs.Raylib;

namespace CivSim.Raylib.Rendering;

/// <summary>
/// Data-driven structure rendering registry. Loads structures.json to determine
/// size, anchor, and sprite for each structure type. Supports atlas sprites,
/// standalone texture files, and procedural placeholder fallbacks.
/// </summary>
public class StructureRegistry : IDisposable
{
    public record StructureRenderInfo(
        string Name,
        string? AtlasSpriteName,
        Texture2D? StandaloneTexture,
        int Width,
        int Height,
        string Anchor,
        bool HasSprite
    );

    private readonly Dictionary<string, StructureRenderInfo> entries = new();
    private readonly List<Texture2D> loadedTextures = new();

    public bool TryGetStructure(string name, out StructureRenderInfo info)
        => entries.TryGetValue(name, out info!);

    /// <summary>
    /// Loads the structure registry JSON from the Assets search paths.
    /// Resolves sprites via atlas first, then standalone PNG files.
    /// </summary>
    public void Load(SpriteAtlas? atlas)
    {
        string[] searchPaths =
        {
            Path.Combine(AppContext.BaseDirectory, "Assets"),
            "Assets",
            Path.Combine("CivSim.Raylib", "Assets"),
        };

        string? jsonPath = null;
        string? assetsBase = null;
        foreach (var basePath in searchPaths)
        {
            string candidate = Path.Combine(basePath, "Sprites", "Structures", "structures.json");
            if (File.Exists(candidate))
            {
                jsonPath = candidate;
                assetsBase = basePath;
                break;
            }
        }

        if (jsonPath == null)
        {
            Console.WriteLine("[StructureRegistry] structures.json not found — using defaults.");
            LoadDefaults();
            return;
        }

        try
        {
            var json = File.ReadAllText(jsonPath);
            using var doc = JsonDocument.Parse(json);

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                string name = prop.Name;
                var obj = prop.Value;

                int width = obj.GetProperty("width").GetInt32();
                int height = obj.GetProperty("height").GetInt32();
                string anchor = obj.GetProperty("anchor").GetString() ?? "center";
                string spriteFile = obj.GetProperty("sprite").GetString() ?? "";

                // Try standalone texture first (new Sprites/Structures/ overrides atlas)
                string atlasName = "structures_" + name;
                Texture2D? standaloneTex = null;
                if (assetsBase != null)
                {
                    // Check old path: Assets/structures/<file>
                    string oldPath = Path.Combine(assetsBase, "structures", spriteFile);
                    // Check new path: Assets/Sprites/Structures/<file>
                    string newPath = Path.Combine(assetsBase, "Sprites", "Structures", spriteFile);

                    string? texPath = null;
                    if (File.Exists(newPath)) texPath = newPath;
                    else if (File.Exists(oldPath)) texPath = oldPath;

                    if (texPath != null)
                    {
                        var tex = Rl.LoadTexture(texPath);
                        if (tex.Id > 0)
                        {
                            standaloneTex = tex;
                            loadedTextures.Add(tex);
                        }
                    }
                }

                // Only use atlas if no standalone texture was found
                bool hasAtlas = standaloneTex == null
                    && atlas is { IsLoaded: true } && atlas.TryGetRegion(atlasName, out _);
                bool hasSprite = hasAtlas || standaloneTex.HasValue;

                entries[name] = new StructureRenderInfo(
                    name,
                    hasAtlas ? atlasName : null,
                    standaloneTex,
                    width,
                    height,
                    anchor,
                    hasSprite
                );
            }

            Console.WriteLine($"[StructureRegistry] Loaded {entries.Count} structure definitions from {jsonPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[StructureRegistry] ERROR: Failed to parse structures.json: {ex.Message}");
            LoadDefaults();
        }
    }

    /// <summary>Fallback defaults when JSON is not found.</summary>
    private void LoadDefaults()
    {
        AddDefault("lean_to", 48, 48, "bottom_center");
        AddDefault("campfire", 24, 24, "center");
        AddDefault("farm", 56, 56, "center");
        AddDefault("animal_pen", 56, 48, "bottom_center");
        AddDefault("granary", 40, 44, "bottom_center");
        AddDefault("improved_shelter", 56, 56, "bottom_center");
        AddDefault("walls", 64, 32, "center");
        AddDefault("shelter", 48, 48, "bottom_center");
        AddDefault("reinforced_shelter", 56, 56, "bottom_center");
    }

    private void AddDefault(string name, int w, int h, string anchor)
    {
        entries[name] = new StructureRenderInfo(name, null, null, w, h, anchor, false);
    }

    public void Dispose()
    {
        foreach (var tex in loadedTextures)
            Rl.UnloadTexture(tex);
        loadedTextures.Clear();
    }
}
