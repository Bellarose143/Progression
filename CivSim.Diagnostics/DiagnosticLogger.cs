using CivSim.Core;

namespace CivSim.Diagnostics;

public enum Verbosity { Summary, Full, Trace }

/// <summary>
/// Writes structured diagnostic output to a log file.
/// Supports three verbosity levels: Summary, Full, and Trace.
/// </summary>
public class DiagnosticLogger : IDisposable
{
    private readonly StreamWriter writer;
    private readonly Verbosity verbosity;

    // Tracking for final report
    private int peakPopulation;
    private int peakPopulationTick;
    private int totalBirths;
    private int totalDeaths;
    private int starvationDeaths;
    private int oldAgeDeaths;
    private int exposureDeaths; // GDD v1.7
    private readonly Dictionary<int, (int finalAge, float finalHunger, int finalHealth, bool survived)> agentOutcomes = new();

    // Analytics tracking
    private int minPopulation = int.MaxValue;
    private long sumPopulation;
    private int ticksTracked;
    private int minFood = int.MaxValue;
    private int maxFood;
    private readonly List<(int Tick, string Description)> discoveryTimeline = new();

    /// <summary>Public read-only access to the discovery timeline for HTML dashboard generation.</summary>
    public IReadOnlyList<(int Tick, string Description)> DiscoveryTimeline => discoveryTimeline;
    private readonly List<int> populationHistory = new();

    public DiagnosticLogger(string filePath, Verbosity verbosity)
    {
        this.verbosity = verbosity;

        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        writer = new StreamWriter(filePath, false) { AutoFlush = true };
    }

    public void WriteHeader(int worldSize, int startingAgents, int tickCount, int seed)
    {
        writer.WriteLine("╔══════════════════════════════════════════════════════════╗");
        writer.WriteLine("║           CivSim Diagnostic Run                        ║");
        writer.WriteLine("╚══════════════════════════════════════════════════════════╝");
        writer.WriteLine($"  World: {worldSize}x{worldSize} | Agents: {startingAgents} | Ticks: {tickCount} | Seed: {seed}");
        writer.WriteLine($"  Verbosity: {verbosity}");
        writer.WriteLine($"  Started: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        writer.WriteLine();
    }

    public void WriteTickSnapshot(TickSnapshot snapshot)
    {
        // Track stats for final report
        if (snapshot.AliveCount > peakPopulation)
        {
            peakPopulation = snapshot.AliveCount;
            peakPopulationTick = snapshot.Tick;
        }
        totalBirths += snapshot.BirthsThisTick;
        totalDeaths += snapshot.DeathsThisTick;
        starvationDeaths += snapshot.StarvationDeathsThisTick;
        oldAgeDeaths += snapshot.OldAgeDeathsThisTick;
        exposureDeaths += snapshot.ExposureDeathsThisTick;

        // Analytics tracking
        if (snapshot.AliveCount < minPopulation) minPopulation = snapshot.AliveCount;
        sumPopulation += snapshot.AliveCount;
        ticksTracked++;
        if (snapshot.TotalFoodOnMap < minFood) minFood = snapshot.TotalFoodOnMap;
        if (snapshot.TotalFoodOnMap > maxFood) maxFood = snapshot.TotalFoodOnMap;
        populationHistory.Add(snapshot.AliveCount);

        // Track discovery events
        foreach (var evt in snapshot.Events)
        {
            if (evt.Type == EventType.Discovery)
                discoveryTimeline.Add((snapshot.Tick, evt.Message));
        }

        // Track per-agent outcomes
        foreach (var agent in snapshot.AgentSnapshots)
        {
            agentOutcomes[agent.Id] = (agent.Age, agent.Hunger, agent.Health, agent.IsAlive);
        }

        // Summary level: one compact line per tick
        if (verbosity == Verbosity.Summary)
        {
            writer.Write($"T{snapshot.Tick,4} | Pop: {snapshot.AliveCount}/{snapshot.TotalCount}");
            if (snapshot.BirthsThisTick > 0) writer.Write($" +{snapshot.BirthsThisTick}B");
            if (snapshot.DeathsThisTick > 0) writer.Write($" -{snapshot.DeathsThisTick}D");
            writer.Write($" | AvgH:{snapshot.AvgHunger:F0} AvgHP:{snapshot.AvgHealth:F0}");
            writer.Write($" | Food:{snapshot.TotalFoodOnMap}");
            writer.WriteLine();
            return;
        }

        // Full and Trace: detailed per-tick block
        writer.WriteLine($"========== TICK {snapshot.Tick} ==========");
        writer.WriteLine();

        // Events
        if (snapshot.Events.Count > 0)
        {
            writer.WriteLine("--- EVENTS ---");
            foreach (var evt in snapshot.Events)
                writer.WriteLine($"  {evt.Message}");
            writer.WriteLine();
        }

        // Trace lines (only in Trace mode)
        if (verbosity == Verbosity.Trace && snapshot.TraceLines.Count > 0)
        {
            writer.WriteLine("--- AI TRACE ---");
            foreach (var line in snapshot.TraceLines)
                writer.WriteLine($"  {line}");
            writer.WriteLine();
        }

        // Agent snapshots
        writer.WriteLine("--- AGENTS ---");
        foreach (var agent in snapshot.AgentSnapshots)
        {
            string inv = agent.Inventory.Count > 0
                ? string.Join(", ", agent.Inventory.Select(kvp => $"{kvp.Key}:{kvp.Value}"))
                : "empty";

            string status = agent.IsAlive ? agent.Action.ToString() : "DEAD";

            writer.WriteLine($"  Agent {agent.Id,2} | Pos=({agent.X,2},{agent.Y,2}) | Hunger={agent.Hunger,5:F1} | Health={agent.Health,3} | Age={Agent.FormatTicks(agent.Age),8} | {status,-8} | Inv: {inv}");
        }
        writer.WriteLine();

        // World resources
        writer.WriteLine("--- WORLD ---");
        var res = snapshot.WorldResources;
        writer.Write("  Resources:");
        foreach (var kvp in res.OrderBy(r => r.Key.ToString()))
            writer.Write($" {kvp.Key}={kvp.Value}");
        writer.WriteLine();
        writer.WriteLine($"  Total food on map: {snapshot.TotalFoodOnMap}");
        writer.WriteLine();

        // Population summary
        writer.WriteLine("--- POPULATION ---");
        writer.WriteLine($"  Alive: {snapshot.AliveCount} | Dead: {snapshot.TotalCount - snapshot.AliveCount} | Births: {snapshot.BirthsThisTick} | Deaths: {snapshot.DeathsThisTick}");
        writer.WriteLine($"  Avg Hunger: {snapshot.AvgHunger:F1} | Avg Health: {snapshot.AvgHealth:F1} | Oldest: {Agent.FormatTicks(snapshot.OldestAge)}");
        writer.WriteLine();
    }

    public void WriteFinalReport(int totalTicks, List<AgentSnapshot> finalAgents)
    {
        writer.WriteLine();
        writer.WriteLine("╔══════════════════════════════════════════════════════════╗");
        writer.WriteLine("║                    FINAL REPORT                         ║");
        writer.WriteLine("╚══════════════════════════════════════════════════════════╝");
        writer.WriteLine($"  Total ticks: {totalTicks} ({Agent.FormatTicks(totalTicks)})");
        writer.WriteLine($"  Peak population: {peakPopulation} (tick {peakPopulationTick}, {Agent.FormatTicks(peakPopulationTick)})");
        writer.WriteLine($"  Final population: {finalAgents.Count(a => a.IsAlive)}");
        writer.WriteLine($"  Total births: {totalBirths}");
        writer.WriteLine($"  Total deaths: {totalDeaths}");
        writer.WriteLine($"  Death causes: starvation={starvationDeaths}, old_age={oldAgeDeaths}, exposure={exposureDeaths}");
        writer.WriteLine();

        // Longest-lived
        var longestLived = agentOutcomes.OrderByDescending(kvp => kvp.Value.finalAge).FirstOrDefault();
        if (longestLived.Value.finalAge > 0)
            writer.WriteLine($"  Longest-lived agent: Agent {longestLived.Key} (age {Agent.FormatTicks(longestLived.Value.finalAge)})");

        // ── Population Analytics ──
        writer.WriteLine();
        writer.WriteLine("--- POPULATION ANALYTICS ---");
        float avgPop = ticksTracked > 0 ? (float)sumPopulation / ticksTracked : 0;
        writer.WriteLine($"  Min population: {minPopulation}");
        writer.WriteLine($"  Max population: {peakPopulation}");
        writer.WriteLine($"  Avg population: {avgPop:F1}");
        writer.WriteLine();

        // ── Food Economy ──
        writer.WriteLine("--- FOOD ECONOMY ---");
        writer.WriteLine($"  Min food on map: {minFood}");
        writer.WriteLine($"  Max food on map: {maxFood}");
        writer.WriteLine();

        // ── Population Sparkline ──
        if (populationHistory.Count > 0)
        {
            writer.WriteLine("--- POPULATION TIMELINE ---");
            WriteSparkline(populationHistory, 60);
            writer.WriteLine();
        }

        // ── Discovery Timeline ──
        writer.WriteLine("--- DISCOVERY TIMELINE ---");
        if (discoveryTimeline.Count > 0)
        {
            foreach (var (tick, desc) in discoveryTimeline)
                writer.WriteLine($"  [{Agent.FormatTicks(tick),8}] {desc}");
        }
        else
        {
            writer.WriteLine("  No discoveries made.");
        }
        writer.WriteLine();

        // Per-agent final state
        writer.WriteLine("--- AGENT FINAL STATES ---");
        foreach (var agent in finalAgents.OrderBy(a => a.Id))
        {
            string status = agent.IsAlive
                ? $"ALIVE - Hunger={agent.Hunger:F1}, Health={agent.Health}, Age={Agent.FormatTicks(agent.Age)}"
                : $"DEAD at age {Agent.FormatTicks(agent.Age)}";
            writer.WriteLine($"  Agent {agent.Id}: {status}");
        }

        writer.WriteLine();
        writer.WriteLine($"  Completed: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    }

    /// <summary>
    /// Writes a text-based sparkline (population over time) using block characters.
    /// Compresses the data into the specified width by bucketing.
    /// </summary>
    private void WriteSparkline(List<int> data, int width)
    {
        if (data.Count == 0) return;

        // Block characters from empty to full (8 levels)
        char[] blocks = { ' ', '\u2581', '\u2582', '\u2583', '\u2584', '\u2585', '\u2586', '\u2587', '\u2588' };

        // Bucket the data into 'width' bins
        int bucketSize = Math.Max(1, data.Count / width);
        var buckets = new List<float>();
        for (int i = 0; i < data.Count; i += bucketSize)
        {
            int end = Math.Min(i + bucketSize, data.Count);
            float avg = 0;
            for (int j = i; j < end; j++)
                avg += data[j];
            avg /= (end - i);
            buckets.Add(avg);
        }

        // Find range
        float min = buckets.Min();
        float max = buckets.Max();
        float range = max - min;
        if (range < 1) range = 1;

        // Build sparkline string
        var sparkline = new char[buckets.Count];
        for (int i = 0; i < buckets.Count; i++)
        {
            int level = (int)((buckets[i] - min) / range * 8);
            level = Math.Clamp(level, 0, 8);
            sparkline[i] = blocks[level];
        }

        // Write with labels
        writer.WriteLine($"  Pop {(int)min,3} |{new string(sparkline)}| {(int)max,3}");
        writer.WriteLine($"       t=0 {new string(' ', Math.Max(0, buckets.Count - 8))} t={data.Count}");
    }

    public void Dispose()
    {
        writer.Dispose();
    }
}

/// <summary>Snapshot of a single agent's state at a point in time.</summary>
public class AgentSnapshot
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public float Hunger { get; set; }
    public int Health { get; set; }
    public int Age { get; set; }
    public bool IsAlive { get; set; }
    public ActionType Action { get; set; }
    public Dictionary<ResourceType, int> Inventory { get; set; } = new();
}

/// <summary>Complete snapshot of one simulation tick.</summary>
public class TickSnapshot
{
    public int Tick { get; set; }

    // Agent data
    public List<AgentSnapshot> AgentSnapshots { get; set; } = new();

    // Population stats
    public int AliveCount { get; set; }
    public int TotalCount { get; set; }
    public int BirthsThisTick { get; set; }
    public int DeathsThisTick { get; set; }
    public int StarvationDeathsThisTick { get; set; }
    public int OldAgeDeathsThisTick { get; set; }
    public int ExposureDeathsThisTick { get; set; } // GDD v1.7
    public float AvgHunger { get; set; }
    public float AvgHealth { get; set; }
    public int OldestAge { get; set; }

    // World resources
    public Dictionary<ResourceType, int> WorldResources { get; set; } = new();
    public int TotalFoodOnMap { get; set; }

    // GDD v1.7: Shelter & Granary metrics
    public float ShelterCoverage { get; set; } // 0.0–1.0: fraction of alive agents sheltered
    public int GranaryFood { get; set; } // Total food stored across all granaries

    // Knowledge
    public int DiscoveryCount { get; set; }

    // GDD v1.7.1: Settlements
    public int SettlementCount { get; set; }

    // Events and traces
    public List<SimulationEvent> Events { get; set; } = new();
    public List<string> TraceLines { get; set; } = new();
}
