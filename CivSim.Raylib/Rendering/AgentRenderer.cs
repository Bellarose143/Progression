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

    // ── Agent movement interpolation ────────────────────────────────
    // Tracks previous and current tile positions per agent for smooth visual lerp.
    // The simulation stays discrete; only the renderer smooths between positions.
    private readonly Dictionary<int, Vector2> _previousPos = new();  // agent.Id → prev tile pos
    private readonly Dictionary<int, Vector2> _targetPos = new();    // agent.Id → current tile pos
    // Tracks whether an agent moved THIS tick so we don't snap _previousPos on subsequent frames.
    // Set true when a new move is detected; cleared only after lerpAlpha reaches 1.0 (interpolation complete).
    private readonly HashSet<int> _interpolating = new();

    /// <summary>Returns the interpolated world-space center position for an agent.</summary>
    public Vector2 GetVisualPosition(int agentId, int tileSize)
    {
        if (_targetPos.TryGetValue(agentId, out var target))
        {
            // Return the target position (lerp happens during render with lerpAlpha)
            return target * tileSize;
        }
        return Vector2.Zero;
    }

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

        { "reproduce", "indicators_heart" },
        { "experiment", "indicators_experiment" },
    };

    // ── Display-only path cache for rendering full routes ─────────
    // When an agent uses greedy movement (no WaypointPath), we compute
    // an A* path purely for display and cache it until the agent moves
    // or changes goal, avoiding per-frame A* computation.
    private int _displayPathAgentId = -1;
    private (int X, int Y) _displayPathAgentPos;
    private (int X, int Y)? _displayPathTarget;
    private List<(int X, int Y)>? _displayPath;

    // PERF-02: Persistent agent-by-tile grouping to avoid per-frame dictionary allocation
    private readonly Dictionary<(int, int), List<Agent>> _agentsByTile = new();
    private readonly List<Agent> _listPool = new(); // temporary reuse pool

    public AgentRenderer(SpriteAtlas? atlas = null)
    {
        this.atlas = atlas;
    }

    /// <summary>Overflow badges deferred to screen-space rendering (BUG-04 fix).</summary>
    private readonly List<(string badge, int worldX, int worldY)> _deferredBadges = new();

    public void Render(List<Agent> agents, World world, Camera2D camera, int tileSize,
                       Agent? selectedAgent, float time, float lerpAlpha = 1.0f,
                       SpriteBatch? batch = null)
    {
        float zoom = camera.Zoom;
        // LOD thresholds — recalibrated for 350×350 world (0.08x–4.0x zoom range)
        // Below 0.15: agents invisible. 0.15–0.3: colored circles. Above 0.3: full humanoids.
        bool drawHumanoids = zoom >= 0.3f;
        bool drawActivityIcons = zoom >= 0.5f;
        bool useSprites = atlas is { IsLoaded: true };
        bool useBatch = useSprites && batch != null;

        _deferredBadges.Clear();

        // ── Update interpolation tracking (D26: predictive from sim fields) ──
        var aliveIds = new HashSet<int>();
        foreach (var agent in agents)
        {
            if (!agent.IsAlive) continue;
            aliveIds.Add(agent.Id);

            // D26 Predictive interpolation: use MoveOrigin/MoveDestination/ActionProgress
            // from the simulation instead of reactively detecting position changes.
            if (agent.IsMoving && agent.ActionDurationTicks > 0)
            {
                // Agent is in a Move action — compute visual position from progress
                float elapsed = agent.ActionProgress + lerpAlpha;
                float t = Math.Clamp(elapsed / agent.ActionDurationTicks, 0f, 1f);
                float smooth = t * t * (3f - 2f * t);
                var origin = new Vector2(agent.MoveOrigin.X, agent.MoveOrigin.Y);
                var dest = new Vector2(agent.MoveDestination.X, agent.MoveDestination.Y);
                var visualTile = Vector2.Lerp(origin, dest, smooth);

                _previousPos[agent.Id] = visualTile;
                _targetPos[agent.Id] = visualTile;
                _interpolating.Add(agent.Id);
            }
            else
            {
                // Not moving — snap to current tile position
                var pos = new Vector2(agent.X, agent.Y);
                _previousPos[agent.Id] = pos;
                _targetPos[agent.Id] = pos;
                _interpolating.Remove(agent.Id);
            }
        }

        // Clean up dead/removed agents
        foreach (var id in _previousPos.Keys.ToList())
            if (!aliveIds.Contains(id)) { _previousPos.Remove(id); _targetPos.Remove(id); _interpolating.Remove(id); }

        // PERF-02: Reuse persistent dictionary — clear and repopulate instead of allocating
        foreach (var list in _agentsByTile.Values) list.Clear();
        _agentsByTile.Clear();
        foreach (var agent in agents)
        {
            if (!agent.IsAlive) continue;
            var key = (agent.X, agent.Y);
            if (!_agentsByTile.ContainsKey(key))
                _agentsByTile[key] = new List<Agent>();
            _agentsByTile[key].Add(agent);
        }

        // PERF-01: Begin batch for agent sprites
        if (useBatch)
            batch!.Begin(atlas!.Spritesheet);

        foreach (var kvp in _agentsByTile)
        {
            int tileX = kvp.Key.Item1;
            int tileY = kvp.Key.Item2;
            var tileAgents = kvp.Value;

            int displayCount = Math.Min(tileAgents.Count, 4);
            var layout = TileLayouts[Math.Min(displayCount, TileLayouts.Length) - 1];

            for (int i = 0; i < displayCount; i++)
            {
                var agent = tileAgents[i];
                var color = ProceduralSprites.GetAgentColor(agent.Id);
                float scale = agent.Stage switch
                {
                    DevelopmentStage.Infant => 0.5f,
                    DevelopmentStage.Youth => 0.7f,
                    _ => 1.0f
                };

                // ── Interpolated position (D26: pre-computed in update loop) ──
                var visualTile = _targetPos.GetValueOrDefault(agent.Id, new Vector2(agent.X, agent.Y));

                float px = visualTile.X * tileSize;
                float py = visualTile.Y * tileSize;
                int cx = (int)(px + layout[i].X * tileSize);
                int cy = (int)(py + layout[i].Y * tileSize);

                if (drawHumanoids)
                {
                    bool isChild = agent.Stage == DevelopmentStage.Infant || agent.Stage == DevelopmentStage.Youth;
                    if (useBatch)
                    {
                        string spriteName = isChild
                            ? "agents_child"
                            : AgentTintNames[agent.Id % AgentTintNames.Length];
                        atlas!.DrawBottomCenteredBatched(batch!, spriteName, cx, cy, scale);
                    }
                    else if (useSprites)
                    {
                        string spriteName = isChild
                            ? "agents_child"
                            : AgentTintNames[agent.Id % AgentTintNames.Length];
                        atlas!.DrawBottomCentered(spriteName, cx, cy, scale);
                    }
                    else
                    {
                        ProceduralSprites.DrawHumanoid(cx, cy, color, scale);
                    }
                }
                else
                {
                    int r = Math.Max(3, (int)(8 * zoom));
                    Rl.DrawCircle(cx, cy - r, r, color);
                    Rl.DrawCircleLines(cx, cy - r, r, Color.White);
                }

                // Selection highlight — pulsing white ring (BUG-03 fix: unified offset)
                if (agent == selectedAgent)
                {
                    // Flush batch before non-batched draw calls
                    if (useBatch) batch!.Flush();

                    float pulse = (float)(0.5 + 0.5 * Math.Sin(time * 4.0));
                    byte alpha = (byte)(100 + (int)(155 * pulse));
                    int spriteHeight = useSprites ? 32 : ProceduralSprites.HumanoidHeight(1.0f);
                    int headOffset = drawHumanoids
                        ? (int)(spriteHeight * scale * 0.5f)
                        : Math.Max(3, (int)(8 * zoom));
                    int highlightR = drawHumanoids ? (int)(14 * scale) : Math.Max(5, (int)(12 * zoom));
                    Rl.DrawCircleLines(cx, cy - headOffset, highlightR, new Color(255, 255, 255, (int)alpha));

                    // Re-begin batch after non-batched draw
                    if (useBatch) batch!.Begin(atlas!.Spritesheet);
                }

                // Tool icon — bottom-right of agent (drawn before activity icon)
                if (drawActivityIcons)
                {
                    if (useBatch) batch!.Flush();
                    DrawToolIcon(agent, cx, cy, scale);
                    if (useBatch) batch!.Begin(atlas!.Spritesheet);
                }

                // Activity icon — unified Y-offset (BUG-06 fix)
                if (drawActivityIcons)
                {
                    int spriteHeight = useSprites ? 32 : ProceduralSprites.HumanoidHeight(1.0f);
                    int iconX = cx + (int)(12 * scale);
                    int iconY = cy - (int)(spriteHeight * scale) - 2;

                    if (useBatch)
                        DrawActivityIconBatched(batch!, agent, iconX, iconY);
                    else if (useSprites)
                        DrawActivityIconSprite(agent, iconX, iconY);
                    else
                        DrawActivityIconProcedural(agent, iconX, iconY, scale);
                }

                // Health bar — drawn when agent is in combat
                if (drawActivityIcons && agent.IsInCombat)
                {
                    if (useBatch) batch!.Flush();
                    int spriteH = useSprites ? 32 : ProceduralSprites.HumanoidHeight(1.0f);
                    int barY = cy - (int)(spriteH * scale) - 8;
                    DrawHealthBar(cx, barY, agent.Health, 100);
                    if (useBatch) batch!.Begin(atlas!.Spritesheet);
                }
            }

            // Overflow badge — defer to screen-space (BUG-04 fix)
            if (tileAgents.Count > 4 && drawHumanoids)
            {
                int badgeWorldX = tileX * tileSize + tileSize - 20;
                int badgeWorldY = tileY * tileSize + 2;
                _deferredBadges.Add(($"+{tileAgents.Count - 4}", badgeWorldX, badgeWorldY));
            }
        }

        // PERF-01: Flush remaining agent sprites
        if (useBatch)
            batch!.Flush();
    }

    /// <summary>Renders deferred overflow badges in screen-space (BUG-04 fix).</summary>
    public void RenderOverflowBadges(Camera2D camera)
    {
        foreach (var (badge, worldX, worldY) in _deferredBadges)
        {
            var screenPos = Rl.GetWorldToScreen2D(new Vector2(worldX, worldY), camera);
            Rl.DrawText(badge, (int)screenPos.X, (int)screenPos.Y, 12, Color.White);
        }
    }

    public void RenderLabels(List<Agent> agents, Camera2D camera, int tileSize,
                              Agent? selectedAgent)
    {
        if (selectedAgent == null || !selectedAgent.IsAlive) return;

        // Use interpolated visual position for label
        var visualPos = _targetPos.GetValueOrDefault(selectedAgent.Id, new Vector2(selectedAgent.X, selectedAgent.Y));
        int worldX = (int)(visualPos.X * tileSize + tileSize / 2);
        int worldY = (int)(visualPos.Y * tileSize);
        Vector2 screenPos = Rl.GetWorldToScreen2D(new Vector2(worldX, worldY), camera);

        string label = selectedAgent.Name;
        int textW = Rl.MeasureText(label, 14);
        int labelX = (int)screenPos.X - textW / 2;
        int labelY = (int)screenPos.Y - 20;

        Rl.DrawRectangle(labelX - 3, labelY - 2, textW + 6, 18, new Color(0, 0, 0, 180));
        Rl.DrawText(label, labelX, labelY, 14, Color.White);
    }

    // ── Fix 4: Tool icon on agent (highest-tier tool in knowledge) ──────
    private static readonly string[] ToolPriority = { "bow", "spear", "hafted_tools", "crude_axe", "stone_knife" };

    private static void DrawToolIcon(Agent agent, int cx, int cy, float scale)
    {
        string? bestTool = null;
        foreach (var tool in ToolPriority)
        {
            if (agent.Knowledge.Contains(tool))
            {
                bestTool = tool;
                break;
            }
        }
        if (bestTool == null) return;

        int ix = cx + (int)(10 * scale);
        int iy = cy - (int)(4 * scale);

        switch (bestTool)
        {
            case "stone_knife":
            {
                // Small filled triangle, gray
                var c = new Color(180, 180, 180, 255);
                Rl.DrawTriangle(
                    new Vector2(ix, iy - 6),
                    new Vector2(ix - 3, iy + 3),
                    new Vector2(ix + 3, iy + 3),
                    c);
                break;
            }
            case "crude_axe":
            {
                // Small L-shape (handle + head), brown
                var handle = new Color(139, 90, 43, 255);
                var head = new Color(160, 160, 160, 255);
                Rl.DrawRectangle(ix - 1, iy - 4, 2, 10, handle); // handle
                Rl.DrawRectangle(ix - 4, iy - 4, 6, 3, head);    // head
                break;
            }
            case "hafted_tools":
            {
                // Slightly larger L-shape, darker brown
                var handle = new Color(101, 67, 33, 255);
                var head = new Color(140, 140, 140, 255);
                Rl.DrawRectangle(ix - 1, iy - 5, 3, 12, handle); // handle
                Rl.DrawRectangle(ix - 5, iy - 5, 8, 4, head);    // head
                break;
            }
            case "spear":
            {
                // Thin vertical rectangle with small triangle tip
                var shaft = new Color(139, 90, 43, 255);
                var tip = new Color(180, 180, 180, 255);
                Rl.DrawRectangle(ix - 1, iy - 4, 2, 14, shaft); // shaft
                Rl.DrawTriangle(
                    new Vector2(ix, iy - 8),
                    new Vector2(ix - 2, iy - 4),
                    new Vector2(ix + 2, iy - 4),
                    tip); // tip
                break;
            }
            case "bow":
            {
                // Small curved arc, brown
                var bowColor = new Color(139, 90, 43, 255);
                for (int i = 0; i < 8; i++)
                {
                    float a0 = -MathF.PI * 0.4f + MathF.PI * 0.8f * i / 8f;
                    float a1 = -MathF.PI * 0.4f + MathF.PI * 0.8f * (i + 1) / 8f;
                    int x0 = ix + (int)(6 * MathF.Cos(a0));
                    int y0 = iy + (int)(6 * MathF.Sin(a0));
                    int x1 = ix + (int)(6 * MathF.Cos(a1));
                    int y1 = iy + (int)(6 * MathF.Sin(a1));
                    Rl.DrawLine(x0, y0, x1, y1, bowColor);
                }
                // Bowstring
                float startA = -MathF.PI * 0.4f;
                float endA = MathF.PI * 0.4f;
                Rl.DrawLine(
                    ix + (int)(6 * MathF.Cos(startA)), iy + (int)(6 * MathF.Sin(startA)),
                    ix + (int)(6 * MathF.Cos(endA)), iy + (int)(6 * MathF.Sin(endA)),
                    bowColor);
                break;
            }
        }
    }

    // ── Fix 5: Health bar for combat ──────────────────────────────────
    private static void DrawHealthBar(int cx, int cy, int health, int maxHealth)
    {
        int barW = 20;
        int barH = 3;
        int x = cx - barW / 2;

        // Background
        Rl.DrawRectangle(x, cy, barW, barH, new Color(40, 40, 40, 200));

        // Fill — green to yellow to red based on health %
        float pct = Math.Clamp((float)health / maxHealth, 0f, 1f);
        int fillW = (int)(barW * pct);
        Color fillColor;
        if (pct > 0.5f)
        {
            float t2 = (pct - 0.5f) * 2f;
            fillColor = new Color((int)(255 * (1f - t2)), 255, 0, 255);
        }
        else
        {
            float t2 = pct * 2f;
            fillColor = new Color(255, (int)(255 * t2), 0, 255);
        }
        if (fillW > 0)
            Rl.DrawRectangle(x, cy, fillW, barH, fillColor);
    }

    /// <summary>Fix 5: Renders a hunt/chase line from selected agent to target animal (dark red dashed line).</summary>
    public void RenderHuntLine(Agent? selectedAgent, World world, int tileSize, float lerpAlpha)
    {
        if (selectedAgent == null || !selectedAgent.IsAlive) return;
        if (!selectedAgent.HuntTargetAnimalId.HasValue) return;
        if (selectedAgent.PendingAction != ActionType.Hunt && selectedAgent.CurrentAction != ActionType.Hunt) return;

        Animal? target = null;
        foreach (var a in world.Animals)
        {
            if (a.Id == selectedAgent.HuntTargetAnimalId.Value && a.IsAlive)
            {
                target = a;
                break;
            }
        }
        if (target == null) return;

        // D26: Use pre-computed interpolated position
        var visualPos = _targetPos.GetValueOrDefault(selectedAgent.Id, new Vector2(selectedAgent.X, selectedAgent.Y));

        float startX = visualPos.X * tileSize + tileSize / 2f;
        float startY = visualPos.Y * tileSize + tileSize / 2f;
        float endX = target.X * tileSize + tileSize / 2f;
        float endY = target.Y * tileSize + tileSize / 2f;

        float dx = endX - startX;
        float dy = endY - startY;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist < 1f) return;

        float ux = dx / dist;
        float uy = dy / dist;

        var outlineColor = new Color(60, 10, 10, 180);
        var lineColor = new Color(140, 40, 30, 200);
        float dashLen = 6f;
        float gapLen = 4f;
        float segLen = dashLen + gapLen;

        for (float d = 0; d < dist; d += segLen)
        {
            float d0 = d;
            float d1 = MathF.Min(d + dashLen, dist);
            int x0 = (int)(startX + ux * d0);
            int y0 = (int)(startY + uy * d0);
            int x1 = (int)(startX + ux * d1);
            int y1 = (int)(startY + uy * d1);

            Rl.DrawLine(x0 - 1, y0, x1 - 1, y1, outlineColor);
            Rl.DrawLine(x0 + 1, y0, x1 + 1, y1, outlineColor);
            Rl.DrawLine(x0, y0, x1, y1, lineColor);
        }

        Rl.DrawCircleLines((int)endX, (int)endY, 5f, lineColor);
    }

    private void DrawActivityIconBatched(SpriteBatch batch, Agent agent, int cx, int cy)
    {
        string? iconName = GetActivityIconName(agent);
        if (iconName != null)
            atlas!.DrawCenteredBatched(batch, iconName, cx, cy);
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
            ActionType.Move => agent.CurrentMode switch
            {
                BehaviorMode.Forage => "indicators_basket",
                BehaviorMode.Explore => "indicators_explore",
                _ => agent.CurrentGoal switch
                {
                    GoalType.ReturnHome => "indicators_return_home",
                    GoalType.GatherFoodAt => "indicators_basket",
                    GoalType.GatherResourceAt => agent.GoalResource switch
                    {
                        ResourceType.Wood => "indicators_axe",
                        ResourceType.Stone or ResourceType.Ore => "indicators_pickaxe",
                        _ => "indicators_basket"
                    },
                    GoalType.SeekFood => "indicators_basket",
                    GoalType.BuildAtTile => "indicators_hammer",
                    _ => null
                }
            },
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
            case ActionType.Move:
                if (agent.CurrentMode == BehaviorMode.Forage)
                    ProceduralSprites.DrawBasketIcon(ix, iy);
                else if (agent.CurrentMode == BehaviorMode.Explore)
                    ProceduralSprites.DrawQuestionMark(ix, iy);
                // No icon for generic movement
                break;
        }
    }

    /// <summary>Draws a dotted path line from the selected agent through A* waypoints to their goal target.
    /// Shows the full remaining route as a continuous dotted polyline.</summary>
    public void RenderPathLine(Agent? selectedAgent, int tileSize, float lerpAlpha, World? world = null)
    {
        if (selectedAgent == null || !selectedAgent.IsAlive) return;

        // Determine final target: prefer GoalTarget (multi-step destination), fall back to ActionTarget
        (int X, int Y)? target = null;
        if (selectedAgent.CurrentGoal.HasValue && selectedAgent.GoalTarget.HasValue)
            target = selectedAgent.GoalTarget;
        else if (selectedAgent.ActionTarget.HasValue)
            target = selectedAgent.ActionTarget;

        if (!target.HasValue) return;

        // Get interpolated agent position (D26: pre-computed in update loop)
        var visualPos = _targetPos.GetValueOrDefault(selectedAgent.Id, new Vector2(selectedAgent.X, selectedAgent.Y));

        float startX = visualPos.X * tileSize + tileSize / 2f;
        float startY = visualPos.Y * tileSize + tileSize / 2f;
        float finalEndX = target.Value.X * tileSize + tileSize / 2f;
        float finalEndY = target.Value.Y * tileSize + tileSize / 2f;

        // Don't draw if agent is already at target
        float dx = finalEndX - startX;
        float dy = finalEndY - startY;
        float totalDist = MathF.Sqrt(dx * dx + dy * dy);
        if (totalDist < tileSize * 0.5f) return;

        // Build the list of points to draw through:
        // Start from interpolated agent position, then follow A* waypoints (from current index),
        // and end at the final target tile center.
        var points = new List<Vector2>();
        points.Add(new Vector2(startX, startY));

        // Resolve which waypoint list to use, in priority order:
        // 1. WaypointPath (A* path from simulation)
        // 2. RecoveryWaypoints (stuck recovery path)
        // 3. Display-only cached path (computed by renderer for greedy-movement goals)
        var waypointPath = selectedAgent.WaypointPath;
        int waypointIndex = selectedAgent.WaypointIndex;

        if (waypointPath == null || waypointPath.Count == 0)
        {
            waypointPath = selectedAgent.RecoveryWaypoints;
            waypointIndex = 0;
        }

        // If no simulation-side waypoints exist but we have a goal target and World access,
        // compute a display-only A* path so the user sees the actual route, not a straight line.
        if ((waypointPath == null || waypointPath.Count == 0) && world != null && target.HasValue)
        {
            var agentPos = (selectedAgent.X, selectedAgent.Y);
            // Invalidate cache if agent changed, moved, or target changed
            if (_displayPathAgentId != selectedAgent.Id
                || _displayPathAgentPos != agentPos
                || _displayPathTarget != target.Value)
            {
                _displayPathAgentId = selectedAgent.Id;
                _displayPathAgentPos = agentPos;
                _displayPathTarget = target.Value;
                _displayPath = SimplePathfinder.FindPath(
                    selectedAgent.X, selectedAgent.Y,
                    target.Value.X, target.Value.Y,
                    world, maxNodes: 1000); // Lower budget for rendering perf
            }

            if (_displayPath != null && _displayPath.Count > 0)
            {
                waypointPath = _displayPath;
                waypointIndex = 0;
            }
        }

        if (waypointPath != null && waypointPath.Count > 0)
        {
            // Clamp waypointIndex to valid range (defensive against off-by-one in child return paths)
            waypointIndex = Math.Clamp(waypointIndex, 0, waypointPath.Count);

            // Add ALL remaining waypoints from current index through the end of the path
            for (int i = waypointIndex; i < waypointPath.Count; i++)
            {
                var wp = waypointPath[i];
                float wpX = wp.X * tileSize + tileSize / 2f;
                float wpY = wp.Y * tileSize + tileSize / 2f;

                // Skip waypoints that are essentially at the same position as the last added point
                // (avoids zero-length segments from duplicate waypoints or agent standing on a waypoint)
                if (points.Count > 0)
                {
                    var last = points[^1];
                    if (MathF.Abs(last.X - wpX) < 1f && MathF.Abs(last.Y - wpY) < 1f)
                        continue;
                }

                points.Add(new Vector2(wpX, wpY));
            }
        }

        // Add the final destination if it differs from the last point
        var finalPoint = new Vector2(finalEndX, finalEndY);
        if (points.Count == 0 || Vector2.DistanceSquared(points[^1], finalPoint) > 1f)
            points.Add(finalPoint);

        // Need at least 2 points to draw a line
        if (points.Count < 2) return;

        // Colors: dark outline for contrast + slightly brighter inner line visible on all biomes
        var outlineColor = new Color(10, 10, 60, 200);
        var lineColor = new Color(40, 50, 140, 200);

        // Draw dotted line segments through each consecutive pair of points
        float dashLen = 6f;
        float gapLen = 4f;
        float segLen = dashLen + gapLen;

        for (int s = 0; s < points.Count - 1; s++)
        {
            float sx = points[s].X;
            float sy = points[s].Y;
            float ex = points[s + 1].X;
            float ey = points[s + 1].Y;

            float segDx = ex - sx;
            float segDy = ey - sy;
            float segLength = MathF.Sqrt(segDx * segDx + segDy * segDy);
            if (segLength < 0.5f) continue;

            float ux = segDx / segLength;
            float uy = segDy / segLength;

            for (float d = 0; d < segLength; d += segLen)
            {
                float d0 = d;
                float d1 = MathF.Min(d + dashLen, segLength);
                int x0 = (int)(sx + ux * d0);
                int y0 = (int)(sy + uy * d0);
                int x1 = (int)(sx + ux * d1);
                int y1 = (int)(sy + uy * d1);

                // Dark outline (1px offset in each direction for visibility on any biome)
                Rl.DrawLine(x0 - 1, y0, x1 - 1, y1, outlineColor);
                Rl.DrawLine(x0 + 1, y0, x1 + 1, y1, outlineColor);
                Rl.DrawLine(x0, y0 - 1, x1, y1 - 1, outlineColor);
                Rl.DrawLine(x0, y0 + 1, x1, y1 + 1, outlineColor);
                // Inner line
                Rl.DrawLine(x0, y0, x1, y1, lineColor);
            }
        }

        // Small circle at final target with outline for visibility
        Rl.DrawCircleLines((int)finalEndX, (int)finalEndY, 5f, outlineColor);
        Rl.DrawCircleLines((int)finalEndX, (int)finalEndY, 4f, lineColor);
    }
}
