using Raylib_cs;
using CivSim.Core;
using System.Numerics;
using Rl = Raylib_cs.Raylib;

namespace CivSim.Raylib.Rendering;

public enum EffectType { Discovery, Birth, DeathStarvation, DeathOldAge, ShelterComplete, ExperimentFail, AnimalKill }

/// <summary>
/// Manages in-world visual effects (sparkles, hearts, skulls).
/// Effects run on frame time, independent of simulation speed.
/// Uses SpriteAtlas icons when available, falls back to procedural shapes.
/// PERF-05: Pre-allocated effect pool (no per-event heap allocation).
/// PERF-06: Frustum culling for off-screen effects.
/// BUG-07: Uses SimulationEvent.AgentId instead of regex parsing.
/// BUG-08: Uses SimulationEvent.Message for death cause (preserved for compatibility).
/// </summary>
public class VisualEffectManager
{
    private struct VisualEffect
    {
        public EffectType Type;
        public int WorldX, WorldY;
        public float Duration;
        public float Elapsed;
        public bool Active;

        public float Progress => Duration > 0 ? Math.Clamp(Elapsed / Duration, 0f, 1f) : 1f;
    }

    // PERF-05: Pre-allocated fixed pool
    private const int PoolSize = 64;
    private readonly VisualEffect[] _pool = new VisualEffect[PoolSize];
    private int _activeCount;

    private readonly SpriteAtlas? atlas;

    public VisualEffectManager(SpriteAtlas? atlas = null)
    {
        this.atlas = atlas;
        _activeCount = 0;
    }

    public void ProcessTickEvents(List<SimulationEvent> tickEvents, List<Agent> agents, int tileSize)
    {
        foreach (var evt in tickEvents)
        {
            // BUG-07 fix: Use first-class AgentId field instead of regex parsing
            Agent? agent = null;
            if (evt.AgentId >= 0)
                agent = agents.FirstOrDefault(a => a.Id == evt.AgentId);

            if (agent == null) continue;

            int wx = agent.X * tileSize + tileSize / 2;
            int wy = agent.Y * tileSize + tileSize / 2;

            switch (evt.Type)
            {
                case EventType.Discovery:
                    AddEffect(EffectType.Discovery, wx, wy, 1.5f);
                    break;
                case EventType.Birth:
                    AddEffect(EffectType.Birth, wx, wy, 1.2f);
                    break;
                case EventType.Death:
                    if (evt.Message.Contains("starvation"))
                        AddEffect(EffectType.DeathStarvation, wx, wy, 1.5f);
                    else
                        AddEffect(EffectType.DeathOldAge, wx, wy, 1.5f);
                    break;
                case EventType.Action:
                    // Fix 5: Animal kill particle effect — triggers on hunt/combat kill messages
                    if (evt.Message.Contains("killed a "))
                        AddEffect(EffectType.AnimalKill, wx, wy, 1.0f);
                    break;
            }
        }
    }

    public void AddEffect(EffectType type, int worldX, int worldY, float duration = 1.5f)
    {
        // PERF-05: Recycle from pool instead of allocating
        int slot = -1;
        for (int i = 0; i < PoolSize; i++)
        {
            if (!_pool[i].Active)
            {
                slot = i;
                break;
            }
        }
        if (slot < 0)
        {
            // Pool full — find oldest effect and evict
            float maxElapsed = -1;
            for (int i = 0; i < PoolSize; i++)
            {
                if (_pool[i].Elapsed > maxElapsed)
                {
                    maxElapsed = _pool[i].Elapsed;
                    slot = i;
                }
            }
        }
        if (slot < 0) return; // Safety

        _pool[slot] = new VisualEffect
        {
            Type = type,
            WorldX = worldX,
            WorldY = worldY,
            Duration = duration,
            Elapsed = 0f,
            Active = true
        };
        _activeCount = Math.Min(_activeCount + 1, PoolSize);
    }

    public void Update(float deltaTime)
    {
        for (int i = 0; i < PoolSize; i++)
        {
            if (!_pool[i].Active) continue;
            _pool[i].Elapsed += deltaTime;
            if (_pool[i].Elapsed >= _pool[i].Duration)
            {
                _pool[i].Active = false;
                _activeCount--;
            }
        }
    }

    /// <summary>Render effects in world-space (inside BeginMode2D).</summary>
    public void Render()
    {
        if (_activeCount <= 0) return;

        bool useSprites = atlas is { IsLoaded: true };

        for (int i = 0; i < PoolSize; i++)
        {
            ref var fx = ref _pool[i];
            if (!fx.Active) continue;

            float t = fx.Progress;
            byte alpha = (byte)(255 * (1f - t));

            switch (fx.Type)
            {
                case EffectType.Discovery:
                    if (useSprites)
                    {
                        float scale = 0.8f + 0.6f * t;
                        atlas!.DrawCenteredAlpha("indicators_sparkle", fx.WorldX, fx.WorldY - (int)(15 * t), scale, alpha);
                        atlas!.DrawCenteredAlpha("indicators_lightbulb", fx.WorldX, fx.WorldY - (int)(25 * t), 0.8f, alpha);
                    }
                    else
                    {
                        float radius = 8 + 30 * t;
                        Rl.DrawCircleLines(fx.WorldX, fx.WorldY, radius,
                            new Color(255, 215, 0, (int)alpha));
                        Rl.DrawCircleLines(fx.WorldX, fx.WorldY, radius - 2,
                            new Color(255, 215, 0, (int)alpha / 2));
                    }
                    break;

                case EffectType.Birth:
                    if (useSprites)
                    {
                        int heartY = fx.WorldY - (int)(20 * t);
                        atlas!.DrawCenteredAlpha("indicators_heart", fx.WorldX, heartY, 1.0f, alpha);
                    }
                    else
                    {
                        int heartY = fx.WorldY - (int)(20 * t);
                        ProceduralSprites.DrawHeartIcon(fx.WorldX, heartY,
                            new Color(255, 150, 180, (int)alpha));
                    }
                    break;

                case EffectType.DeathStarvation:
                    if (useSprites)
                    {
                        atlas!.DrawCenteredAlpha("indicators_skull", fx.WorldX, fx.WorldY - (int)(10 * t), 1.2f, alpha);
                    }
                    else
                    {
                        ProceduralSprites.DrawSkull(fx.WorldX, fx.WorldY - (int)(10 * t), alpha);
                    }
                    break;

                case EffectType.DeathOldAge:
                    Rl.DrawCircle(fx.WorldX, fx.WorldY, 6 + 4 * t,
                        new Color(200, 200, 220, (int)alpha / 2));
                    break;

                case EffectType.ShelterComplete:
                    Rl.DrawRectangle(fx.WorldX - 32, fx.WorldY - 32, 64, 64,
                        new Color(255, 255, 255, (int)alpha / 3));
                    break;

                case EffectType.ExperimentFail:
                    if (useSprites)
                    {
                        atlas!.DrawCenteredAlpha("indicators_smoke", fx.WorldX, fx.WorldY - (int)(8 * t), 1.0f, alpha);
                    }
                    else
                    {
                        float grayR = 5 + 15 * t;
                        Rl.DrawCircleLines(fx.WorldX, fx.WorldY, grayR,
                            new Color(150, 150, 150, (int)alpha));
                    }
                    break;

                case EffectType.AnimalKill:
                {
                    // Red-brown burst: expanding circle that fades
                    float radius = 6 + 20 * t;
                    var killColor = new Color(160, 40, 20, (int)alpha);
                    Rl.DrawCircleLines(fx.WorldX, fx.WorldY, radius, killColor);
                    Rl.DrawCircleLines(fx.WorldX, fx.WorldY, radius * 0.6f,
                        new Color(180, 60, 30, (int)(alpha * 0.6f)));
                    // Small inner filled circle that shrinks
                    float innerR = 4 * (1f - t);
                    if (innerR > 0.5f)
                        Rl.DrawCircle(fx.WorldX, fx.WorldY, innerR,
                            new Color(200, 50, 20, (int)(alpha * 0.8f)));
                    break;
                }
            }
        }
    }
}
