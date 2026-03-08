using Raylib_cs;
using CivSim.Core;
using System.Text.Json;
using Rl = Raylib_cs.Raylib;

namespace CivSim.Raylib.Rendering;

/// <summary>
/// Data-driven resource rendering registry. Loads resources.json to determine
/// size, anchor, and sprite for each resource type. Supports atlas sprites,
/// standalone texture files, and procedural/atlas fallbacks in WorldRenderer.
/// </summary>
public class ResourceSpriteRegistry : IDisposable
{
    public record ResourceRenderInfo(
        string Name,
        string? AtlasSpriteName,
        Texture2D? StandaloneTexture,
        int Width,
        int Height,
        string Anchor,
        bool HasSprite
    );

    private readonly Dictionary<string, ResourceRenderInfo> _entries = new();
    private readonly List<Texture2D> _loadedTextures = new();

    /// <summary>
    /// Maps ResourceType enum values to registry keys.
    /// </summary>
    private static readonly Dictionary<ResourceType, string> ResourceKeyMap = new()
    {
        { ResourceType.Wood, "wood" },
        { ResourceType.Stone, "stone" },
        { ResourceType.Berries, "berries" },
        { ResourceType.Ore, "ore" },
        { ResourceType.Grain, "grain" },
        { ResourceType.Meat, "meat" },
        { ResourceType.Fish, "fish" },
        { ResourceType.Hide, "hide" },
        { ResourceType.Bone, "bone" },
        { ResourceType.PreservedFood, "preservedfood" },
    };

    /// <summary>
    /// Tries to get render info for a resource type.
    /// Returns false if no registry entry exists (caller should use existing atlas fallback).
    /// </summary>
    public bool TryGetInfo(ResourceType resourceType, out ResourceRenderInfo info)
    {
        info = default!;
        if (!ResourceKeyMap.TryGetValue(resourceType, out string? key))
            return false;
        return _entries.TryGetValue(key, out info!);
    }

    /// <summary>
    /// Tries to get render info by string key.
    /// </summary>
    public bool TryGetInfo(string resourceKey, out ResourceRenderInfo info)
        => _entries.TryGetValue(resourceKey, out info!);

    /// <summary>
    /// Loads the resource registry JSON from the Assets search paths.
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
            string candidate = Path.Combine(basePath, "Sprites", "Resources", "resources.json");
            if (File.Exists(candidate))
            {
                jsonPath = candidate;
                assetsBase = basePath;
                break;
            }
        }

        if (jsonPath == null)
        {
            Console.WriteLine("[ResourceSpriteRegistry] resources.json not found — using atlas fallback only.");
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

                // Try standalone texture first (new Sprites/Resources/ overrides atlas)
                string atlasName = "resources_" + name;
                Texture2D? standaloneTex = null;
                if (assetsBase != null)
                {
                    // Check new path: Assets/Sprites/Resources/<file>
                    string newPath = Path.Combine(assetsBase, "Sprites", "Resources", spriteFile);
                    // Check old path: Assets/resources/<file>
                    string oldPath = Path.Combine(assetsBase, "resources", spriteFile);

                    string? texPath = null;
                    if (File.Exists(newPath)) texPath = newPath;
                    else if (File.Exists(oldPath)) texPath = oldPath;

                    if (texPath != null)
                    {
                        var tex = Rl.LoadTexture(texPath);
                        if (tex.Id > 0)
                        {
                            standaloneTex = tex;
                            _loadedTextures.Add(tex);
                        }
                    }
                }

                // Only use atlas if no standalone texture was found
                bool hasAtlas = standaloneTex == null
                    && atlas is { IsLoaded: true } && atlas.TryGetRegion(atlasName, out _);
                bool hasSprite = hasAtlas || standaloneTex.HasValue;

                _entries[name] = new ResourceRenderInfo(
                    name,
                    hasAtlas ? atlasName : null,
                    standaloneTex,
                    width,
                    height,
                    anchor,
                    hasSprite
                );
            }

            Console.WriteLine($"[ResourceSpriteRegistry] Loaded {_entries.Count} resource definitions from {jsonPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ResourceSpriteRegistry] ERROR: Failed to parse resources.json: {ex.Message}");
            LoadDefaults();
        }
    }

    /// <summary>Fallback defaults when JSON is not found — empty entries so atlas fallback is used.</summary>
    private void LoadDefaults()
    {
        // No entries means WorldRenderer will fall through to its existing atlas-based rendering
    }

    public void Dispose()
    {
        foreach (var tex in _loadedTextures)
            Rl.UnloadTexture(tex);
        _loadedTextures.Clear();
    }
}
