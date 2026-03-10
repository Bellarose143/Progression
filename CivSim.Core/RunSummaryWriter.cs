namespace CivSim.Core;

/// <summary>
/// Generates a human-readable run summary report from simulation data.
/// Called when the simulation ends (window close or diagnostics completion).
/// Writes to diagnostics/run_summary_seed_[SEED].txt.
/// </summary>
public static class RunSummaryWriter
{
    /// <summary>
    /// Writes the full run summary report to the specified file path.
    /// Creates the output directory if it does not exist.
    /// </summary>
    public static void Write(Simulation simulation, string outputPath)
    {
        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string report = Generate(simulation);
        File.WriteAllText(outputPath, report);
    }

    /// <summary>
    /// Generates the summary report as a string without writing to disk.
    /// Useful for testing.
    /// </summary>
    public static string Generate(Simulation simulation)
    {
        var sb = new System.Text.StringBuilder(4096);

        WriteHeader(sb, simulation);
        sb.AppendLine();
        WritePopulation(sb, simulation);
        sb.AppendLine();
        WriteDiscoveries(sb, simulation);
        sb.AppendLine();
        WritePerAgentSummary(sb, simulation);
        sb.AppendLine();
        WriteSettlements(sb, simulation);

        return sb.ToString();
    }

    // ── Header ──────────────────────────────────────────────────────────

    private static void WriteHeader(System.Text.StringBuilder sb, Simulation simulation)
    {
        int ticks = simulation.CurrentTick;
        int simDays = SimConfig.TicksToSimDays(ticks);
        float simYears = SimConfig.TicksToYears(ticks);

        sb.AppendLine($"CivSim Run Summary — Seed {simulation.World.Seed}");
        sb.AppendLine($"Duration: {ticks} ticks ({simDays} days, {simYears:F1} years)");
        sb.AppendLine($"World: {simulation.World.Width}x{simulation.World.Height}");
    }

    // ── Population ──────────────────────────────────────────────────────

    private static void WritePopulation(System.Text.StringBuilder sb, Simulation simulation)
    {
        sb.AppendLine("== Population ==");

        // Starting count: founding agents have no parents
        int startingCount = simulation.Agents.Count(a => a.Parent1Name == null);
        sb.AppendLine($"Starting: {startingCount}");

        int peakPop = simulation.PeakPopulation;
        int peakDay = SimConfig.TicksToSimDays(simulation.PeakPopulationTick);
        sb.AppendLine($"Peak: {peakPop} (day {peakDay})");

        int finalAlive = simulation.Agents.Count(a => a.IsAlive);
        sb.AppendLine($"Final: {finalAlive}");

        // Deaths
        var dead = simulation.Agents.Where(a => !a.IsAlive).ToList();
        sb.AppendLine($"Deaths: {dead.Count}");
        foreach (var agent in dead)
        {
            string cause = string.IsNullOrEmpty(agent.DeathCause) ? "unknown" : agent.DeathCause;
            int deathDay = agent.DeathTick >= 0 ? SimConfig.TicksToSimDays(agent.DeathTick) : 0;
            string ageStr = Agent.FormatTicks(agent.Age);
            sb.AppendLine($"  - {agent.Name}: {cause} at day {deathDay} (age {ageStr})");
        }

        // Births (agents born during the simulation, i.e. those with parents)
        var born = simulation.Agents.Where(a => a.Parent1Name != null).ToList();
        sb.AppendLine($"Births: {born.Count}");
        foreach (var agent in born)
        {
            int birthDay = SimConfig.TicksToSimDays(agent.BirthTick);
            sb.AppendLine($"  - {agent.Name}: born day {birthDay} to {agent.Parent1Name} and {agent.Parent2Name}");
        }
    }

    // ── Discoveries ─────────────────────────────────────────────────────

    private static void WriteDiscoveries(System.Text.StringBuilder sb, Simulation simulation)
    {
        // Accumulate cumulative discoveries
        simulation.GetStats(); // Side-effect: populates CumulativeDiscoveries

        var records = simulation.DiscoveryRecords;
        int totalDiscoveries = simulation.CumulativeDiscoveries.Count;

        sb.AppendLine($"== Discoveries ({totalDiscoveries} total) ==");

        if (records.Count > 0)
        {
            foreach (var (tick, recipeId, agentName) in records.OrderBy(r => r.Tick))
            {
                int day = SimConfig.TicksToSimDays(tick);
                sb.AppendLine($"Day {day}: {recipeId} (discovered by {agentName})");
            }

            // Longest gap without discovery
            int longestGapTicks = 0;

            // Check gap from start to first discovery
            var sorted = records.OrderBy(r => r.Tick).ToList();
            if (sorted.Count > 0 && sorted[0].Tick > longestGapTicks)
                longestGapTicks = sorted[0].Tick;

            for (int i = 1; i < sorted.Count; i++)
            {
                int gap = sorted[i].Tick - sorted[i - 1].Tick;
                if (gap > longestGapTicks)
                    longestGapTicks = gap;
            }

            // Check gap from last discovery to end
            int gapToEnd = simulation.CurrentTick - sorted[sorted.Count - 1].Tick;
            if (gapToEnd > longestGapTicks)
                longestGapTicks = gapToEnd;

            int longestGapDays = SimConfig.TicksToSimDays(longestGapTicks);
            sb.AppendLine($"Longest gap without discovery: {longestGapDays} sim-days");
        }
        else
        {
            sb.AppendLine("No recipe discoveries recorded.");
        }
    }

    // ── Per-Agent Summary ───────────────────────────────────────────────

    private static void WritePerAgentSummary(System.Text.StringBuilder sb, Simulation simulation)
    {
        sb.AppendLine("== Per-Agent Summary ==");

        foreach (var agent in simulation.Agents)
        {
            string status = agent.IsAlive ? "Alive" : "Dead";
            string ageStr = Agent.FormatTicks(agent.Age);
            sb.AppendLine($"{agent.Name} — {status}, Age {ageStr}");

            // Action Distribution
            sb.AppendLine("  Action Distribution:");
            WriteDistribution(sb, agent.ActionTickCounts, "    ");

            // Mode Distribution
            sb.AppendLine("  Mode Distribution:");
            WriteModeDistribution(sb, agent.ModeTickCounts, "    ");

            // Stuck episodes
            sb.AppendLine($"  Stuck episodes: {agent.StuckEpisodeCount} (total ticks stuck: {agent.TotalStuckTicks})");

            // Max distance from home
            sb.AppendLine($"  Max distance from home: {agent.MaxDistanceFromHome} tiles");

            // D19: Restlessness statistics
            float avgRestlessness = agent.RestlessnessSampleCount > 0
                ? agent.RestlessnessSum / agent.RestlessnessSampleCount : 0f;
            float pctAbove50 = agent.RestlessnessSampleCount > 0
                ? 100f * agent.RestlessnessAbove50Ticks / agent.RestlessnessSampleCount : 0f;
            sb.AppendLine($"  Avg Restlessness: {avgRestlessness:F1}");
            sb.AppendLine($"  Peak Restlessness: {agent.PeakRestlessness:F1}");
            sb.AppendLine($"  Time at Restlessness > 50: {pctAbove50:F1}%");
            sb.AppendLine();
        }
    }

    private static void WriteDistribution<T>(System.Text.StringBuilder sb,
        Dictionary<T, int> counts, string indent) where T : notnull
    {
        int total = 0;
        foreach (var kvp in counts)
            total += kvp.Value;

        if (total == 0)
        {
            sb.AppendLine($"{indent}(no data)");
            return;
        }

        // Group into lines of 3 entries each
        var sorted = counts.OrderByDescending(kv => kv.Value).ToList();
        for (int i = 0; i < sorted.Count; i += 3)
        {
            var parts = new List<string>();
            for (int j = i; j < Math.Min(i + 3, sorted.Count); j++)
            {
                float pct = 100f * sorted[j].Value / total;
                parts.Add($"{sorted[j].Key}: {pct:F0}%");
            }
            sb.AppendLine($"{indent}{string.Join("  |  ", parts)}");
        }
    }

    private static void WriteModeDistribution(System.Text.StringBuilder sb,
        Dictionary<BehaviorMode, int> counts, string indent)
    {
        WriteDistribution(sb, counts, indent);
    }

    // ── Settlements ─────────────────────────────────────────────────────

    private static void WriteSettlements(System.Text.StringBuilder sb, Simulation simulation)
    {
        sb.AppendLine("== Settlements ==");

        var settlements = simulation.Settlements;
        if (settlements.Count == 0)
        {
            sb.AppendLine("No settlements detected.");
            return;
        }

        foreach (var settlement in settlements)
        {
            int foundedDay = SimConfig.TicksToSimDays(settlement.FoundedTick);
            sb.AppendLine($"{settlement.Name}, founded day {foundedDay} at ({settlement.CenterX},{settlement.CenterY})");

            // List structures at settlement center tile
            if (simulation.World.IsInBounds(settlement.CenterX, settlement.CenterY))
            {
                var centerTile = simulation.World.GetTile(settlement.CenterX, settlement.CenterY);
                if (centerTile.Structures.Count > 0)
                    sb.AppendLine($"  Structures: {string.Join(", ", centerTile.Structures)}");

                // Home storage at settlement center
                int foodStored = centerTile.HomeTotalFood;
                int woodStored = centerTile.Resources.GetValueOrDefault(ResourceType.Wood, 0);
                int stoneStored = centerTile.Resources.GetValueOrDefault(ResourceType.Stone, 0);
                sb.AppendLine($"  Home storage: {foodStored} food, {woodStored} wood, {stoneStored} stone");
            }
        }

        // Also report any agent home tiles with storage
        var homeTiles = new HashSet<(int, int)>();
        foreach (var agent in simulation.Agents.Where(a => a.HomeTile.HasValue))
        {
            var home = agent.HomeTile!.Value;
            if (homeTiles.Add(home) && simulation.World.IsInBounds(home.X, home.Y))
            {
                var tile = simulation.World.GetTile(home.X, home.Y);
                if (tile.HasHomeStorage && tile.HomeTotalFood > 0)
                {
                    // Only report if not already covered by settlement center
                    bool coveredBySettlement = settlements.Any(s =>
                        s.CenterX == home.X && s.CenterY == home.Y);
                    if (!coveredBySettlement)
                    {
                        sb.AppendLine($"Home at ({home.X},{home.Y}): {tile.HomeTotalFood} food stored");
                    }
                }
            }
        }
    }
}
