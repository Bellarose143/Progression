using Raylib_cs;
using CivSim.Core;
using System.Numerics;
using Rl = Raylib_cs.Raylib;

namespace CivSim.Raylib.Rendering;

/// <summary>
/// Renders agents as colored humanoid figures with activity icons.
/// Handles multi-agent tile layout and selection highlight.
/// LOD: at low zoom, agents render as simple colored circles.
/// When a SpriteAtlas is available, uses sprite tints; otherwise procedural shapes.
/// </summary>
public class AgentRenderer
{
    private readonly SpriteAtlas? atlas;

    // Pre-computed layout offsets within a tile (fraction of tileSize)
    private static readonly Vector2[][] TileLayouts =
    {
        new[] { new Vector2(0.5f, 0.65f) },
        new[] { new Vector2(0.3f, 0.65f), new Vector2(0.7f, 0.65f) },
        new[] { new Vector2(0.5f, 0.35f), new Vector2(0.3f, 0.7f), new Vector2(0.7f, 0.7f) },
        new[] { new Vector2(0.3f, 0.35f), new Vector2(0.7f, 0.35f), new Vector2(0.3f, 0.7f), new Vector2(0.7f, 0.7f) },
    };

    // 12 NPC sprite names from Vectoraith RPG NPC pack (GDD visual overhaul)
    private static readonly string[] AgentTintNames =
    {
        "agents_npc_01", "agents_npc_02", "agents_npc_03",
        "agents_npc_04", "agents_npc_05", "agents_npc_06",
        "agents_npc_07", "agents_npc_08", "agents_npc_09",
        "agents_npc_10", "agents_npc_11", "agents_npc_12"
    };

    // Activity icon sprite names from atlas
    private static readonly Dictionary<string, string> ActivityIconNames = new()
    {
        { "gather_wood", "indicators_axe" },
        { "gather_stone", "indicators_pickaxe" },
        { "gather_ore", "indicators_pickaxe" },
        { "gather_berries", "indicators_basket" },
        { "gather_grain", "indicators_basket" },
        { "gather_fish", "indicators_basket" },
        { "gather_default", "indicators_basket" },
        { "rest", "indicators_zzz" },
        { "build", "indicators_hammer" },
        { "teach", "indicators_teaching" },
        { "reproduce", "indicators_heart" },
        { "experiment", "indicators_experiment" },
    };

    public AgentRenderer(SpriteAtlas? atlas = null)
    {
        this.atlas = atlas;
    }

    public void Render(List<Agent> agents, World world, Camera2D camera, int tileSize,
                       Agent? selectedAgent, float time)
    {
        float zoom = camera.Zoom;
        bool drawHumanoids = zoom >= 0.7f;
        bool drawActivityIcons = zoom >= 1.2f;
        bool useSprites = atlas is { IsLoaded: true };

        // Group living agents by tile position
        var agentsByTile = new Dictionary<(int, int), List<Agent>>();
        foreach (var agent in agents)
        {
            if (!agent.IsAlive) continue;
            var key = (agent.X, agent.Y);
            if (!agentsByTile.ContainsKey(key))
                agentsByTile[key] = new List<Agent>();
            agentsByTile[key].Add(agent);
        }

        foreach (var kvp in agentsByTile)
        {
            int tileX = kvp.Key.Item1;
            int tileY = kvp.Key.Item2;
            int px = tileX * tileSize;
            int py = tileY * tileSize;
            var tileAgents = kvp.Value;

            int displayCount = Math.Min(tileAgents.Count, 4);
            var layout = TileLayouts[Math.Min(displayCount, TileLayouts.Length) - 1];

            for (int i = 0; i < displayCount; i++)
            {
                var agent = tileAgents[i];
                var color = ProceduralSprites.GetAgentColor(agent.Id);
                // GDD v1.7.2: Stage-based sizing (Infant=0.5, Youth=0.7, Adult=1.0)
                float scale = agent.Stage switch
                {
                    DevelopmentStage.Infant => 0.5f,
                    DevelopmentStage.Youth => 0.7f,
                    _ => 1.0f
                };

                int cx = px + (int)(layout[i].X * tileSize);
                int cy = py + (int)(layout[i].Y * tileSize);

                if (drawHumanoids)
                {
                    if (useSprites)
                    {
                        // GDD v1.7.2: Use bottom-centered anchor to prevent head clipping
                        string tintName = AgentTintNames[agent.Id % AgentTintNames.Length];
                        atlas!.DrawBottomCentered(tintName, cx, cy, scale);
                    }
                    else
                    {
                        ProceduralSprites.DrawHumanoid(cx, cy, color, scale);
                    }
                }
                else
                {
                    // Low-zoom LOD: simple colored circle
                    int r = Math.Max(3, (int)(8 * zoom));
                    Rl.DrawCircle(cx, cy - r, r, color);
                    Rl.DrawCircleLines(cx, cy - r, r, Color.White);
                }

                // Selection highlight — pulsing white ring
                if (agent == selectedAgent)
                {
                    float pulse = (float)(0.5 + 0.5 * Math.Sin(time * 4.0));
                    byte alpha = (byte)(100 + (int)(155 * pulse));
                    int highlightR = drawHumanoids ? (int)(14 * scale) : Math.Max(5, (int)(12 * zoom));
                    int headOffset = drawHumanoids
                        ? (useSprites ? (int)(16 * scale) : ProceduralSprites.HumanoidHeight(scale) / 2)
                        : Math.Max(3, (int)(8 * zoom));
                    Rl.DrawCircleLines(cx, cy - headOffset, highlightR, new Color(255, 255, 255, (int)alpha));
                }

                // Activity icon (only when zoomed in)
                if (drawActivityIcons)
                {
                    int iconX = cx + (int)(12 * scale);
                    int iconY = useSprites
                        ? cy - (int)(18 * scale)
                        : cy - ProceduralSprites.HumanoidHeight(scale) - 2;

                    if (useSprites)
                        DrawActivityIconSprite(agent, iconX, iconY);
                    else
                        DrawActivityIconProcedural(agent, iconX, iconY, scale);
                }
            }

            // Overflow badge
            if (tileAgents.Count > 4 && drawHumanoids)
            {
                string badge = $"+{tileAgents.Count - 4}";
                Rl.DrawText(badge, px + tileSize - 20, py + 2, 12, Color.White);
            }
        }
    }

    public void RenderLabels(List<Agent> agents, Camera2D camera, int tileSize,
                              Agent? selectedAgent)
    {
        if (selectedAgent == null || !selectedAgent.IsAlive) return;

        int worldX = selectedAgent.X * tileSize + tileSize / 2;
        int worldY = selectedAgent.Y * tileSize;
        Vector2 screenPos = Rl.GetWorldToScreen2D(new Vector2(worldX, worldY), camera);

        string label = selectedAgent.Name;
        int textW = Rl.MeasureText(label, 14);
        int labelX = (int)screenPos.X - textW / 2;
        int labelY = (int)screenPos.Y - 20;

        Rl.DrawRectangle(labelX - 3, labelY - 2, textW + 6, 18, new Color(0, 0, 0, 180));
        Rl.DrawText(label, labelX, labelY, 14, Color.White);
    }

    private void DrawActivityIconSprite(Agent agent, int cx, int cy)
    {
        string? iconName = GetActivityIconName(agent);
        if (iconName != null)
            atlas!.DrawCentered(iconName, cx, cy);
    }

    private static string? GetActivityIconName(Agent agent)
    {
        return agent.CurrentAction switch
        {
            ActionType.Gather => agent.LastGatheredResource switch
            {
                ResourceType.Wood => "indicators_axe",
                ResourceType.Stone or ResourceType.Ore => "indicators_pickaxe",
                _ => "indicators_basket"
            },
            ActionType.Rest => "indicators_zzz",
            ActionType.Idle => null, // No icon for idle
            ActionType.Build => "indicators_hammer",
            // GDD v1.8: Teach removed — no teaching indicator needed
            ActionType.Reproduce => "indicators_heart",
            ActionType.Experiment => "indicators_experiment",
            // GDD visual overhaul: wire all remaining action types
            ActionType.Cook => "indicators_cook",
            ActionType.Preserve => "indicators_preserve",
            ActionType.Deposit => "indicators_deposit",
            ActionType.Withdraw => "indicators_withdraw",
            ActionType.TendFarm => "indicators_tend_farm",
            ActionType.Socialize => "indicators_socialize",
            ActionType.Explore => "indicators_explore",
            ActionType.ReturnHome => "indicators_return_home",
            ActionType.Eat => "indicators_eating",
            ActionType.ShareFood => "indicators_heart",
            _ => null
        };
    }

    private static void DrawActivityIconProcedural(Agent agent, int cx, int cy, float scale)
    {
        // Adjust position for procedural icons (they have different anchor points)
        int ix = cx - (int)(2 * scale);
        int iy = cy;
        switch (agent.CurrentAction)
        {
            case ActionType.Gather:
                if (agent.LastGatheredResource == ResourceType.Wood)
                    ProceduralSprites.DrawAxeIcon(ix, iy);
                else if (agent.LastGatheredResource == ResourceType.Stone || agent.LastGatheredResource == ResourceType.Ore)
                    ProceduralSprites.DrawPickaxeIcon(ix, iy);
                else
                    ProceduralSprites.DrawBasketIcon(ix, iy);
                break;
            case ActionType.Rest:
                ProceduralSprites.DrawZzz(ix, iy);
                break;
            case ActionType.Idle:
                break; // No icon for idle
            case ActionType.Build:
                ProceduralSprites.DrawHammerIcon(ix, iy);
                break;
            // GDD v1.8: Teach removed — no teaching indicator
            case ActionType.Reproduce:
                ProceduralSprites.DrawHeartIcon(ix, iy);
                break;
            case ActionType.Experiment:
                ProceduralSprites.DrawQuestionMark(ix, iy);
                break;
            // GDD visual overhaul: procedural fallbacks for all remaining actions
            case ActionType.Cook:
                ProceduralSprites.DrawHeartIcon(ix, iy, new Color(255, 140, 0, 255));
                break;
            case ActionType.Preserve:
            case ActionType.Deposit:
            case ActionType.Withdraw:
            case ActionType.Eat:
                ProceduralSprites.DrawBasketIcon(ix, iy);
                break;
            case ActionType.TendFarm:
                ProceduralSprites.DrawHammerIcon(ix, iy);
                break;
            case ActionType.Socialize:
                ProceduralSprites.DrawSpeechBubbleIcon(ix, iy);
                break;
            case ActionType.Explore:
                ProceduralSprites.DrawQuestionMark(ix, iy);
                break;
            case ActionType.ReturnHome:
                ProceduralSprites.DrawZzz(ix, iy);
                break;
        }
    }
}
