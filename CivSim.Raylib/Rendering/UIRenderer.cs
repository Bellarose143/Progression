using Raylib_cs;
using CivSim.Core;
using Rl = Raylib_cs.Raylib;

namespace CivSim.Raylib.Rendering;

public enum EventFilterLevel { High, Medium, All }

/// <summary>
/// Renders the stats panel, selection detail, event log, and controls legend.
/// All in screen-space (after EndMode2D).
/// </summary>
public class UIRenderer
{
    public const int PanelWidth = 280;
    private static readonly Color Cyan = new(0, 255, 255, 255);

    private int previousPopulation;
    private int populationTrend; // +1 up, -1 down, 0 stable
    private int trendUpdateTick;
    private float cachedFoodHealth;
    private int foodCacheTick;

    // PERF-04: Cached filtered event list — invalidated when event count or filter changes
    private List<SimulationEvent>? _cachedFilteredEvents;
    private int _cachedEventCount;
    private EventFilterLevel _cachedFilterLevel;

    public void RenderStatsPanel(int screenWidth, int screenHeight, SimulationStats stats,
                                  float ticksPerSecond, int peakPopulation, World world,
                                  EventFilterLevel filterLevel)
    {
        int panelX = screenWidth - PanelWidth;

        // Panel background
        Rl.DrawRectangle(panelX, 0, PanelWidth, screenHeight, new Color(20, 20, 30, 230));
        Rl.DrawLineEx(new System.Numerics.Vector2(panelX, 0),
                      new System.Numerics.Vector2(panelX, screenHeight), 2, new Color(60, 60, 80, 255));

        int x = panelX + 12;
        int y = 12;
        int spacing = 22;

        Rl.DrawText("CivSim", x, y, 20, Color.White);
        y += 30;

        // Human-readable time (v1.8: ticks are invisible, show sim-days/seasons/years)
        string timeLabel = Agent.FormatTicks(stats.CurrentTick);
        // Systemic 4: Day/night indicator
        int hourOfDay = stats.CurrentTick % SimConfig.TicksPerSimDay;
        string timeOfDay;
        if (hourOfDay < 120) timeOfDay = "Dawn";           // 0-25%
        else if (hourOfDay < 240) timeOfDay = "Day";       // 25-50%
        else if (hourOfDay < 360) timeOfDay = "Afternoon"; // 50-75%
        else timeOfDay = "Night";                          // 75-100%
        Color timeColor = timeOfDay == "Night" ? new Color(130, 130, 200, 255) : Color.LightGray;
        Rl.DrawText($"Time: {timeLabel} - {timeOfDay}", x, y, 14, timeColor);
        y += spacing;

        // Population with trend arrow
        UpdatePopulationTrend(stats.AliveAgents, stats.CurrentTick);
        string trendArrow = populationTrend > 0 ? " ^" : populationTrend < 0 ? " v" : " -";
        Color popColor = stats.AliveAgents > 0 ? Color.Green : Color.Red;
        Rl.DrawText($"Population: {stats.AliveAgents}{trendArrow}", x, y, 14, popColor);
        y += spacing;

        // Peak population
        Rl.DrawText($"Peak: {peakPopulation}", x, y, 14, Color.LightGray);
        y += spacing;

        // Deaths
        Rl.DrawText($"Deaths: {stats.DeadAgents}", x, y, 14, new Color(180, 80, 80, 255));
        y += spacing;

        // Oldest
        Rl.DrawText($"Oldest: {Agent.FormatTicks(stats.OldestAgent)}", x, y, 14, Cyan);
        y += spacing;

        // Food health — PERF-03: use running counter instead of scanning all tiles
        if (stats.CurrentTick - foodCacheTick >= 10 || foodCacheTick == 0)
        {
            cachedFoodHealth = stats.AliveAgents > 0
                ? world.TotalWorldFood / (stats.AliveAgents * SimConfig.HungerDrainPerTick)
                : 0;
            foodCacheTick = stats.CurrentTick;
        }
        float foodHealth = cachedFoodHealth;
        string foodLabel;
        Color foodColor;
        if (foodHealth > 40) { foodLabel = "Abundant"; foodColor = Color.Green; }
        else if (foodHealth > 20) { foodLabel = "Stable"; foodColor = Color.Yellow; }
        else { foodLabel = "Declining"; foodColor = Color.Red; }
        Rl.DrawText($"Food: {foodLabel} ({foodHealth:F0})", x, y, 14, foodColor);
        y += spacing;

        // Discoveries
        int totalRecipes = RecipeRegistry.AllRecipes.Count(r => r.BaseChance > 0); // exclude oral_tradition
        Rl.DrawText($"Discoveries: {stats.TotalDiscoveries}/{totalRecipes}", x, y, 14, Cyan);
        y += spacing;

        // GDD v1.7.1: Settlements
        if (stats.SettlementCount > 0)
        {
            Rl.DrawText($"Settlements: {stats.SettlementCount}", x, y, 14, Color.Yellow);
            y += spacing;
            foreach (var name in stats.SettlementNames)
            {
                Rl.DrawText($"  {name}", x, y, 12, Color.LightGray);
                y += 14;
            }
            y += 2;
        }

        // Seed
        Rl.DrawText($"Seed: {stats.WorldSeed}", x, y, 14, Color.Gray);
        y += spacing + 8;

        // Separator
        Rl.DrawLineEx(new System.Numerics.Vector2(x, y),
                      new System.Numerics.Vector2(panelX + PanelWidth - 12, y), 1, new Color(60, 60, 80, 255));
        y += 8;

        // Speed + FPS (v1.8 Corrections: float-based speed tiers)
        int tierIndex = -1;
        for (int ti = 0; ti < SimConfig.SpeedTierTicksPerSecond.Length; ti++)
        {
            if (Math.Abs(SimConfig.SpeedTierTicksPerSecond[ti] - ticksPerSecond) < 0.01f)
            { tierIndex = ti; break; }
        }
        string speedLabel = tierIndex >= 0 ? SimConfig.SpeedTierLabels[tierIndex] : $"{ticksPerSecond:F1} t/s";
        Rl.DrawText($"Speed: {speedLabel}  FPS: {Rl.GetFPS()}", x, y, 14, new Color(150, 255, 150, 255));
        y += spacing;

        // Filter level
        string filterLabel = filterLevel switch
        {
            EventFilterLevel.High => "Log: High",
            EventFilterLevel.Medium => "Log: Med",
            EventFilterLevel.All => "Log: All",
            _ => "Log: All"
        };
        Rl.DrawText(filterLabel, x, y, 12, Color.Gray);
    }

    /// <summary>
    /// Renders the agent roster showing all alive agents with compact stats.
    /// Clicking a row expands it to show full details.
    /// </summary>
    public void RenderAgentRoster(int screenWidth, int screenHeight,
                                   List<Agent> agents, World world, int? expandedAgentId)
    {
        int panelLeft = screenWidth - PanelWidth;
        int panelX = panelLeft + 12;
        int y = screenHeight / 2;
        int barWidth = PanelWidth - 24;
        int halfBar = (barWidth - 4) / 2;

        var aliveAgents = agents.Where(a => a.IsAlive).OrderBy(a => a.Id).ToList();
        if (aliveAgents.Count == 0) return;

        // Background for roster area
        Rl.DrawRectangle(panelLeft, y - 5, PanelWidth, screenHeight - y + 5,
            new Color(15, 15, 25, 240));

        // Header
        Rl.DrawText("Agents", panelX, y, 14, Color.White);
        y += 20;

        RosterHitZones.Clear();

        foreach (var agent in aliveAgents)
        {
            if (y >= screenHeight - 10) break; // don't overflow

            int rowStartY = y;
            bool isExpanded = agent.Id == expandedAgentId;

            // Row separator
            Rl.DrawLineEx(new System.Numerics.Vector2(panelX, y),
                          new System.Numerics.Vector2(panelLeft + PanelWidth - 12, y), 1,
                          new Color(60, 60, 80, 255));
            y += 4;

            // Row 1: color dot + name (left), action (right)
            Color agentColor = ProceduralSprites.GetAgentColor(agent.Id);
            Rl.DrawCircle(panelX + 5, y + 6, 4, agentColor);

            string actionStr = GetDisplayAction(agent);
            if (actionStr.Length > 15) actionStr = actionStr[..15];
            int actionWidth = Rl.MeasureText(actionStr, 11);
            Rl.DrawText(agent.Name, panelX + 14, y, 13, isExpanded ? Color.White : Color.LightGray);
            Rl.DrawText(actionStr, panelLeft + PanelWidth - 12 - actionWidth, y, 11, Color.Gray);
            y += 16;

            // Row 2: hunger mini-bar + health mini-bar side by side
            DrawBar(panelX, y, halfBar, 8, agent.Hunger / 100f, $"H:{agent.Hunger:F0}", Color.Orange);
            DrawBar(panelX + halfBar + 4, y, halfBar, 8, agent.Health / 100f, $"HP:{agent.Health:F0}", Color.Green);
            y += 12;

            // Row 3: progress bar (only if busy, skip Move — sub-tick durations show 0/0)
            if (agent.IsBusy && agent.ActionDurationTicks > 0f
                && agent.PendingAction != ActionType.Move)
            {
                float progress = agent.ActionProgress / agent.ActionDurationTicks;
                string progressLabel = $"{agent.PendingAction} {(int)agent.ActionProgress}/{(int)agent.ActionDurationTicks}";
                DrawBar(panelX, y, barWidth, 8, progress, progressLabel, new Color(150, 150, 255, 255));
                y += 12;
            }

            y += 4; // padding after compact row

            // Expanded detail section
            if (isExpanded)
            {
                RenderAgentDetailContent(panelX, ref y, agent, world, agents);
            }

            RosterHitZones.Add((agent.Id, rowStartY, y));
        }
    }

    /// <summary>Hit zones from the last roster render, for click detection.</summary>
    public List<(int AgentId, int YStart, int YEnd)> RosterHitZones { get; } = new();

    /// <summary>
    /// Renders expanded agent detail content (inventory, traits, bonds, etc.).
    /// Used by the roster for expanded agents.
    /// </summary>
    private void RenderAgentDetailContent(int panelX, ref int y, Agent agent, World world, List<Agent>? allAgents)
    {
        // Separator before detail
        y += 2;

        Rl.DrawText($"Age: {agent.FormatAge()}", panelX, y, 12, Color.LightGray);
        y += 16;

        Rl.DrawText($"Position: ({agent.X}, {agent.Y})", panelX, y, 12, Color.LightGray);
        y += 16;

        // D19: Restlessness display
        var restColor = agent.Restlessness > 60 ? new Color(255, 100, 50, (int)255)
            : agent.Restlessness > 30 ? new Color(255, 200, 50, (int)255)
            : new Color(100, 200, 100, (int)255);
        Rl.DrawText($"Restless: {(int)agent.Restlessness}", panelX, y, 12, restColor);
        y += 16;

        // Inventory — only show items the agent actually has (hide zero-count entries)
        var nonZeroInventory = agent.Inventory.Where(item => item.Value > 0).ToList();
        if (nonZeroInventory.Count > 0)
        {
            Rl.DrawText("Inventory:", panelX, y, 12, Color.Yellow);
            y += 14;
            foreach (var item in nonZeroInventory)
            {
                Rl.DrawText($"  {item.Key}: {item.Value}", panelX, y, 11, Color.LightGray);
                y += 13;
            }
            y += 4;
        }

        // Personality Traits
        if (agent.Traits != null && agent.Traits.Length > 0)
        {
            Rl.DrawText("Traits:", panelX, y, 12, Cyan);
            y += 14;
            foreach (var trait in agent.Traits)
            {
                Color traitColor = trait switch
                {
                    PersonalityTrait.Builder => Color.Orange,
                    PersonalityTrait.Explorer => Color.SkyBlue,
                    PersonalityTrait.Social => Color.Pink,
                    PersonalityTrait.Cautious => Color.Green,
                    PersonalityTrait.Curious => Color.Purple,
                    _ => Color.LightGray
                };
                Rl.DrawText($"  {trait}", panelX, y, 11, traitColor);
                y += 13;
            }
            y += 4;
        }

        // Home Tile
        if (agent.HomeTile.HasValue)
        {
            Rl.DrawText($"Home: ({agent.HomeTile.Value.X}, {agent.HomeTile.Value.Y})", panelX, y, 12, Color.Yellow);
            y += 16;

            var homeTile = world.GetTile(agent.HomeTile.Value.X, agent.HomeTile.Value.Y);
            if (homeTile.HasHomeStorage)
            {
                int stored = homeTile.HomeTotalFood;
                int capacity = homeTile.HomeStorageCapacity;
                Rl.DrawText($"Home Storage: {stored}/{capacity}", panelX, y, 12, new Color(200, 180, 100, 255));
                y += 14;
                foreach (var kvp in homeTile.HomeFoodStorage)
                {
                    if (kvp.Value > 0)
                    {
                        Rl.DrawText($"  {kvp.Key}: {kvp.Value}", panelX, y, 11, Color.LightGray);
                        y += 13;
                    }
                }
                y += 4;
            }
        }

        // Social Bonds (top 5) with names and relationship types
        if (agent.SocialBonds.Count > 0)
        {
            Rl.DrawText("Bonds:", panelX, y, 12, Cyan);
            y += 14;
            var topBonds = agent.SocialBonds
                .OrderByDescending(b => b.Value)
                .Take(5);
            foreach (var bond in topBonds)
            {
                var bondAgent = allAgents?.FirstOrDefault(a => a.Id == bond.Key);
                string name = bondAgent?.Name ?? $"Agent #{bond.Key}";

                string relLabel = "";
                if (agent.Relationships.TryGetValue(bond.Key, out var relType) && relType != RelationshipType.None)
                {
                    relLabel = relType switch
                    {
                        RelationshipType.Spouse => " (spouse)",
                        RelationshipType.Parent => " (parent)",
                        RelationshipType.Child => " (child)",
                        RelationshipType.Sibling => " (sibling)",
                        _ => ""
                    };
                }
                else
                {
                    relLabel = bond.Value >= SimConfig.SocialBondFamilyStart ? " (family)" :
                               bond.Value >= SimConfig.SocialBondFriendThreshold ? " (friend)" : "";
                }

                Color bondColor = relType switch
                {
                    RelationshipType.Spouse => Color.Pink,
                    RelationshipType.Child => Color.SkyBlue,
                    RelationshipType.Parent => Color.Yellow,
                    RelationshipType.Sibling => Color.Green,
                    _ => Color.LightGray
                };
                Rl.DrawText($"  {name}: {bond.Value}{relLabel}", panelX, y, 11, bondColor);
                y += 13;
            }
            y += 4;
        }

        // Knowledge
        if (agent.Knowledge.Count > 0)
        {
            Rl.DrawText("Knowledge:", panelX, y, 12, Cyan);
            y += 14;
            foreach (var k in agent.Knowledge)
            {
                Rl.DrawText($"  {k}", panelX, y, 11, Color.LightGray);
                y += 13;
            }
            y += 4;
        }

        // Reproduction cooldown
        if (agent.ReproductionCooldownRemaining > 0)
        {
            string cdStr = Agent.FormatTicks(agent.ReproductionCooldownRemaining);
            Rl.DrawText($"Repro CD: {cdStr}", panelX, y, 12, Color.Gray);
            y += 16;
        }

        // Exposure status
        if (agent.IsExposed)
        {
            Rl.DrawText("EXPOSED (no shelter nearby)", panelX, y, 12, Color.Red);
            y += 16;
        }

        y += 4; // padding after detail section
    }

    /// <summary>
    /// Renders tile selection or death report detail (no longer handles alive agents — roster does that).
    /// </summary>
    public void RenderSelectionDetail(int screenWidth, int screenHeight,
                                       Agent? agent, (int X, int Y)? selectedTile,
                                       World world, List<Agent>? allAgents = null)
    {
        int panelX = screenWidth - PanelWidth + 12;
        int y = screenHeight / 2;

        if (agent != null && !agent.IsAlive)
        {
            // ── GDD v1.7: Death Report Panel ──────────────────────────
            Rl.DrawRectangle(screenWidth - PanelWidth, y - 5, PanelWidth, screenHeight - y + 5,
                new Color(25, 10, 10, 240));

            Rl.DrawText("DEATH REPORT", panelX, y, 16, Color.Red);
            y += 22;

            Rl.DrawText(agent.Name, panelX, y, 14, ProceduralSprites.GetAgentColor(agent.Id));
            y += 18;

            // Cause of death
            string cause = agent.DeathCause ?? "unknown";
            Color causeColor = cause switch
            {
                "starvation" => Color.Orange,
                "exposure" => Color.SkyBlue,
                "old age" => Color.Gray,
                _ => Color.White
            };
            Rl.DrawText($"Cause: {cause}", panelX, y, 12, causeColor);
            y += 16;

            // GDD v1.7.1: Traits in death report
            if (agent.Traits != null && agent.Traits.Length > 0)
            {
                Rl.DrawText($"Traits: {string.Join(", ", agent.Traits)}", panelX, y, 12, Cyan);
                y += 16;
            }

            // Final stats
            Rl.DrawText($"Age at death: {agent.FormatAge()}", panelX, y, 12, Color.LightGray);
            y += 14;
            Rl.DrawText($"Position: ({agent.X}, {agent.Y})", panelX, y, 12, Color.LightGray);
            y += 14;
            Rl.DrawText($"Final hunger: {agent.Hunger:F0}", panelX, y, 12, Color.Orange);
            y += 14;
            Rl.DrawText($"Final health: {agent.Health}", panelX, y, 12, Color.Green);
            y += 20;

            // Last actions from ring buffer
            var lastActions = agent.GetLastActions(8);
            if (lastActions.Count > 0)
            {
                Rl.DrawText("Last Actions:", panelX, y, 12, Color.Yellow);
                y += 14;
                foreach (var action in lastActions)
                {
                    string timeStr = Agent.FormatTicks(action.Tick);
                    Rl.DrawText($"  [{timeStr}] {action.Action}: {action.Detail}", panelX, y, 10, Color.LightGray);
                    y += 12;
                }
                y += 4;
            }

            // Knowledge at death
            if (agent.Knowledge.Count > 0)
            {
                Rl.DrawText("Knowledge:", panelX, y, 12, Cyan);
                y += 14;
                foreach (var k in agent.Knowledge)
                {
                    Rl.DrawText($"  {k}", panelX, y, 10, Color.LightGray);
                    y += 12;
                }
            }
        }
        else if (selectedTile.HasValue)
        {
            var tile = world.GetTile(selectedTile.Value.X, selectedTile.Value.Y);

            Rl.DrawRectangle(screenWidth - PanelWidth, y - 5, PanelWidth, screenHeight - y + 5,
                new Color(15, 15, 25, 240));

            Rl.DrawText($"Tile ({selectedTile.Value.X}, {selectedTile.Value.Y})", panelX, y, 16, Color.White);
            y += 22;

            Rl.DrawText($"Biome: {tile.Biome}", panelX, y, 12, Color.LightGray);
            y += 16;

            // Resources
            if (tile.Resources.Count > 0)
            {
                Rl.DrawText("Resources:", panelX, y, 12, Color.Yellow);
                y += 14;
                foreach (var res in tile.Resources)
                {
                    int cap = tile.GetCapacity(res.Key);
                    float ratio = cap > 0 ? (float)res.Value / cap : 0;
                    DrawBar(panelX, y, PanelWidth - 24, 10, ratio, $"{res.Key}: {res.Value}/{cap}", Color.SkyBlue);
                    y += 16;
                }
                y += 4;
            }

            // Structures
            if (tile.Structures.Count > 0)
            {
                Rl.DrawText("Structures:", panelX, y, 12, Color.Yellow);
                y += 14;
                foreach (var s in tile.Structures)
                {
                    Rl.DrawText($"  {s}", panelX, y, 11, Color.LightGray);
                    y += 13;
                }
            }

            // Build progress
            if (tile.BuildProgress.Count > 0)
            {
                Rl.DrawText("Building:", panelX, y, 12, Color.Orange);
                y += 14;
                foreach (var bp in tile.BuildProgress)
                {
                    string bpTime = Agent.FormatTicks(bp.Value);
                    Rl.DrawText($"  {bp.Key}: {bpTime} left", panelX, y, 11, Color.LightGray);
                    y += 13;
                }
            }

            // Animals on tile
            var animalsHere = world.GetAnimalsAt(selectedTile.Value.X, selectedTile.Value.Y);
            if (animalsHere.Count > 0)
            {
                y += 4;
                Rl.DrawText($"Animals: {animalsHere.Count}", panelX, y, 12, Color.Yellow);
                y += 14;
                var grouped = animalsHere.GroupBy(a => a.Species);
                foreach (var group in grouped)
                {
                    int domestic = group.Count(a => a.IsDomesticated);
                    string label = domestic > 0
                        ? $"  {group.Key} x{group.Count()} ({domestic} domestic)"
                        : $"  {group.Key} x{group.Count()}";
                    Rl.DrawText(label, panelX, y, 11, Color.LightGray);
                    y += 13;
                }
            }

            // Carcasses on tile
            var carcassesHere = world.Carcasses.Where(c => c.IsActive && c.X == selectedTile.Value.X && c.Y == selectedTile.Value.Y).ToList();
            if (carcassesHere.Count > 0)
            {
                Rl.DrawText($"Carcasses: {carcassesHere.Count}", panelX, y, 12, new Color(180, 100, 60, 255));
                y += 14;
            }

            // Agents on tile
            var agentsHere = world.GetAgentsAt(selectedTile.Value.X, selectedTile.Value.Y);
            if (agentsHere.Count > 0)
            {
                y += 4;
                Rl.DrawText($"Agents here: {agentsHere.Count}", panelX, y, 12, Color.Green);
                y += 14;
                foreach (var a in agentsHere.Take(5))
                {
                    Rl.DrawText($"  {a.Name}", panelX, y, 11, ProceduralSprites.GetAgentColor(a.Id));
                    y += 13;
                }
            }
        }
    }

    public void RenderEventLog(int screenHeight, List<SimulationEvent> recentEvents,
                                EventFilterLevel filterLevel)
    {
        // PERF-04: Reuse cached filtered list when event count and filter haven't changed
        if (_cachedFilteredEvents == null || recentEvents.Count != _cachedEventCount || filterLevel != _cachedFilterLevel)
        {
            _cachedFilteredEvents = recentEvents.Where(e => ShouldShowEvent(e.Type, filterLevel)).ToList();
            _cachedEventCount = recentEvents.Count;
            _cachedFilterLevel = filterLevel;
        }
        var filtered = _cachedFilteredEvents;
        int maxEvents = 10;
        int eventCount = Math.Min(maxEvents, filtered.Count);
        int startIndex = Math.Max(0, filtered.Count - eventCount);

        int x = 10;
        int logHeight = maxEvents * 18 + 10;
        int y = screenHeight - logHeight - 10;

        Rl.DrawRectangle(x - 5, y - 5, 500, logHeight + 10, new Color(0, 0, 0, 160));

        for (int i = 0; i < eventCount; i++)
        {
            var evt = filtered[startIndex + i];
            Color color = evt.Type switch
            {
                EventType.Birth => Color.Green,
                EventType.Death => Color.Red,
                EventType.Discovery => Cyan,
                EventType.Milestone => Color.Yellow,
                EventType.Action => Color.White,
                _ => Color.Gray
            };
            Rl.DrawText(evt.ToString(), x, y + i * 18, 11, color);
        }
    }

    public void RenderControls()
    {
        int x = 10, y = 10;
        Rl.DrawRectangle(x - 5, y - 5, 220, 164, new Color(0, 0, 0, 140));
        Rl.DrawText("Controls:", x, y, 14, Color.LightGray);
        y += 18;
        Rl.DrawText("WASD/MMB: Pan", x, y, 11, Color.Gray); y += 14;
        Rl.DrawText("Scroll: Zoom", x, y, 11, Color.Gray); y += 14;
        Rl.DrawText("Click: Select", x, y, 11, Color.Gray); y += 14;
        Rl.DrawText("Tab/Click: Select  F: Follow", x, y, 11, Color.Gray); y += 14;
        Rl.DrawText("P/Space: Pause  1-5: Speed", x, y, 11, Color.Gray); y += 14;
        Rl.DrawText("G: Grid  L: Log filter  R: Reset", x, y, 11, Color.Gray); y += 14;
        Rl.DrawText("K: Discoveries  Esc: Deselect", x, y, 11, Color.Gray);
    }

    /// <summary>
    /// Renders a toggleable overlay showing all recipes grouped by branch,
    /// with discovered vs undiscovered status. Triggered by K key.
    /// </summary>
    public void RenderDiscoveryPanel(int screenWidth, int screenHeight, HashSet<string> discovered)
    {
        int panelX = screenWidth - PanelWidth;

        // Full-height overlay background (covers stats panel when active)
        Rl.DrawRectangle(panelX, 0, PanelWidth, screenHeight, new Color(20, 20, 30, 240));
        Rl.DrawLineEx(new System.Numerics.Vector2(panelX, 0),
                      new System.Numerics.Vector2(panelX, screenHeight), 2, new Color(60, 60, 80, 255));

        int x = panelX + 12;
        int y = 12;

        // Title
        Rl.DrawText("Discoveries", x, y, 20, Cyan);
        y += 24;
        Rl.DrawText("(K to close)", x, y, 12, Color.Gray);
        y += 20;

        // Count summary
        int totalRecipes = RecipeRegistry.AllRecipes.Count(r => r.BaseChance > 0);
        Rl.DrawText($"{discovered.Count}/{totalRecipes} discovered", x, y, 14, Color.White);
        y += 24;

        // Group recipes by branch and render
        string[] branches = { "Tools", "Fire", "Food", "Shelter", "Knowledge" };
        Color[] branchColors =
        {
            new Color(200, 150, 50, 255),   // Tools: gold
            new Color(255, 100, 50, 255),   // Fire: orange-red
            new Color(100, 200, 50, 255),   // Food: green
            new Color(150, 150, 200, 255),  // Shelter: blue-gray
            Cyan                             // Knowledge: cyan
        };

        for (int b = 0; b < branches.Length; b++)
        {
            string branch = branches[b];
            var recipes = RecipeRegistry.AllRecipes
                .Where(r => r.Branch == branch)
                .OrderBy(r => r.Tier)
                .ToList();

            if (recipes.Count == 0) continue;

            // Branch header
            Rl.DrawText($"-- {branch} --", x, y, 13, branchColors[b]);
            y += 16;

            foreach (var recipe in recipes)
            {
                bool isDiscovered = discovered.Contains(recipe.Output);
                string marker = isDiscovered ? "[*]" : "[ ]";
                Color nameColor = isDiscovered ? Color.Green : new Color(100, 100, 100, 255);

                Rl.DrawText($"{marker} {recipe.Name} (T{recipe.Tier})", x + 4, y, 12, nameColor);
                y += 14;
            }
            y += 4; // Space between branches
        }
    }

    /// <summary>Renders a tile selection highlight in world-space (inside BeginMode2D).</summary>
    public void RenderTileSelection(int tileX, int tileY, int tileSize)
    {
        Rl.DrawRectangleLines(tileX * tileSize, tileY * tileSize, tileSize, tileSize,
            new Color(255, 255, 255, 200));
        Rl.DrawRectangleLines(tileX * tileSize + 1, tileY * tileSize + 1, tileSize - 2, tileSize - 2,
            new Color(255, 255, 255, 100));
    }

    /// <summary>Returns a descriptive label for the agent's current action.
    /// For Move actions, shows context-aware labels instead of "Move".</summary>
    private static string GetDisplayAction(Agent agent)
    {
        if (agent.CurrentAction != ActionType.Move)
            return agent.CurrentAction.ToString();

        // Move action — show what the agent is doing, not "Move 0/0"
        if (agent.CurrentGoal == GoalType.ReturnHome)
            return "Returning home";
        if (agent.CurrentGoal == GoalType.GatherFoodAt)
            return "Gathering food";
        if (agent.CurrentGoal == GoalType.GatherResourceAt)
        {
            if (agent.GoalResource.HasValue)
                return $"Gathering {agent.GoalResource.Value}";
            return "Gathering";
        }
        if (agent.CurrentGoal == GoalType.Explore)
            return "Exploring";
        if (agent.CurrentGoal == GoalType.BuildAtTile)
            return "Going to build";
        if (agent.CurrentGoal == GoalType.SeekFood)
            return "Seeking food";

        // No goal — use mode for context
        return agent.CurrentMode switch
        {
            BehaviorMode.Forage => "Foraging",
            BehaviorMode.Explore => "Exploring",
            BehaviorMode.Caretaker => "Heading home",
            _ => "Walking"
        };
    }

    private static void DrawBar(int x, int y, int width, int height, float value, string label, Color color)
    {
        value = Math.Clamp(value, 0f, 1f);
        Rl.DrawRectangle(x, y, width, height, new Color(40, 40, 40, 255));
        Rl.DrawRectangle(x, y, (int)(width * value), height, color);
        Rl.DrawText(label, x + 2, y, height - 1, Color.White);
    }

    private static bool ShouldShowEvent(EventType type, EventFilterLevel filter) => filter switch
    {
        EventFilterLevel.High => type is EventType.Birth or EventType.Death
                                     or EventType.Discovery or EventType.Milestone,
        EventFilterLevel.Medium => type is not EventType.Movement,
        EventFilterLevel.All => true,
        _ => true
    };

    private void UpdatePopulationTrend(int currentPop, int tick)
    {
        if (tick - trendUpdateTick >= 10)
        {
            if (currentPop > previousPopulation) populationTrend = 1;
            else if (currentPop < previousPopulation) populationTrend = -1;
            else populationTrend = 0;
            previousPopulation = currentPop;
            trendUpdateTick = tick;
        }
    }

}
