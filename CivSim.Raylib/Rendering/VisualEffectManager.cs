using Raylib_cs;
using CivSim.Core;
using System.Numerics;
using System.Text.RegularExpressions;
using Rl = Raylib_cs.Raylib;

namespace CivSim.Raylib.Rendering;

public enum EffectType { Discovery, Birth, DeathStarvation, DeathOldAge, ShelterComplete, ExperimentFail }

/// <summary>
/// Manages in-world visual effects (sparkles, hearts, skulls).
/// Effects run on frame time, independent of simulation speed.
/// Uses SpriteAtlas icons when available, falls back to procedural shapes.
/// </summary>
public class VisualEffectManager
{
    private class VisualEffect
    {
        public EffectType Type;
        public int WorldX, WorldY;
        public float Duration;
        public float Elapsed;

        public float Progress => Math.Clamp(Elapsed / Duration, 0f, 1f);
    }

    private readonly List<VisualEffect> effects = new();
    private readonly SpriteAtlas? atlas;

    public VisualEffectManager(SpriteAtlas? atlas = null)
    {
        this.atlas = atlas;
    }

    public void ProcessTickEvents(List<SimulationEvent> tickEvents, List<Agent> agents, int tileSize)
    {
        foreach (var evt in tickEvents)
        {
            Agent? agent = FindAgentFromEvent(evt, agents);
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
            }
        }
    }

    public void AddEffect(EffectType type, int worldX, int worldY, float duration = 1.5f)
    {
        effects.Add(new VisualEffect
        {
            Type = type,
            WorldX = worldX,
            WorldY = worldY,
            Duration = duration,
            Elapsed = 0f
        });
    }

    public void Update(float deltaTime)
    {
        for (int i = effects.Count - 1; i >= 0; i--)
        {
            effects[i].Elapsed += deltaTime;
            if (effects[i].Elapsed >= effects[i].Duration)
                effects.RemoveAt(i);
        }
    }

    /// <summary>Render effects in world-space (inside BeginMode2D).</summary>
    public void Render()
    {
        bool useSprites = atlas is { IsLoaded: true };

        foreach (var fx in effects)
        {
            float t = fx.Progress;
            byte alpha = (byte)(255 * (1f - t));

            switch (fx.Type)
            {
                case EffectType.Discovery:
                    if (useSprites)
                    {
                        // Sparkle sprite floating up and fading
                        float scale = 0.8f + 0.6f * t;
                        atlas!.DrawCenteredAlpha("indicators_sparkle", fx.WorldX, fx.WorldY - (int)(15 * t), scale, alpha);
                        // Lightbulb floats above the sparkle (GDD visual overhaul)
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
                    // Gentle fade circle (keep procedural — no specific sprite needed)
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
            }
        }
    }

    private static Agent? FindAgentFromEvent(SimulationEvent evt, List<Agent> agents)
    {
        var match = Regex.Match(evt.Message, @"Agent (\d+)");
        if (match.Success && int.TryParse(match.Groups[1].Value, out int id))
            return agents.FirstOrDefault(a => a.Id == id);
        return null;
    }
}
