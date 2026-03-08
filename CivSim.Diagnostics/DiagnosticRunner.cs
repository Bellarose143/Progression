using CivSim.Core;
using CivSim.Core.Events;

namespace CivSim.Diagnostics;

/// <summary>
/// Runs the simulation headless for a fixed number of ticks,
/// capturing per-tick snapshots and writing them to the diagnostic logger.
/// GDD v1.6.2: Performance contract — full snapshots every DiagnosticSampleInterval ticks (24 = 1 day),
/// event-driven counters for births/deaths/discoveries between snapshots, buffered I/O every DiagnosticFlushInterval ticks.
/// ~24x reduction in I/O + world scans vs per-tick snapshots.
/// </summary>
public class DiagnosticRunner
{
    private readonly int worldSize;
    private readonly int startingAgents;
    private readonly int tickCount;
    private readonly int seed;
    private readonly Verbosity verbosity;
    private readonly string logFilePath;

    public DiagnosticRunner(int worldSize, int startingAgents, int tickCount, int seed, Verbosity verbosity, string logFilePath)
    {
        this.worldSize = worldSize;
        this.startingAgents = startingAgents;
        this.tickCount = tickCount;
        this.seed = seed;
        this.verbosity = verbosity;
        this.logFilePath = logFilePath;
    }

    // ── Event-driven counters (accumulated between snapshots) ────────
    private int _birthCounter;
    private int _deathCounter;
    private int _starvationDeathCounter;
    private int _oldAgeDeathCounter;
    private int _exposureDeathCounter; // GDD v1.7
    private int _discoveryCounter;
    // Discovery events captured between snapshots (timeline descriptions the logger would otherwise miss)
    private readonly List<SimulationEvent> _discoveryEvents = new();

    private void ResetCounters()
    {
        _birthCounter = 0;
        _deathCounter = 0;
        _starvationDeathCounter = 0;
        _oldAgeDeathCounter = 0;
        _exposureDeathCounter = 0;
        _discoveryCounter = 0;
        _discoveryEvents.Clear();
    }

    public void Run()
    {
        Console.WriteLine($"Initializing world ({worldSize}x{worldSize}, seed={seed})...");
        var world = new World(worldSize, worldSize, seed);
        var simulation = new Simulation(world, seed);

        using var logger = new DiagnosticLogger(logFilePath, verbosity);
        logger.WriteHeader(worldSize, startingAgents, tickCount, seed);

        // CSV export — companion file alongside the .log
        string csvPath = Path.ChangeExtension(logFilePath, ".csv");
        var csvDir = Path.GetDirectoryName(csvPath);
        if (!string.IsNullOrEmpty(csvDir))
            Directory.CreateDirectory(csvDir);
        // GDD v1.6.2: Buffered I/O — no AutoFlush, manual flush every DiagnosticFlushInterval ticks
        var csv = new StreamWriter(csvPath, false) { AutoFlush = false };
        csv.WriteLine("Tick,Population,Births,Deaths,StarvationDeaths,OldAgeDeaths,ExposureDeaths,AvgHunger,AvgHealth,OldestAge,TotalFoodOnMap,Discoveries,ShelterCoverage,GranaryFood,Settlements");

        // Subscribe to EventBus for event-driven counters
        simulation.EventBus.Subscribe(events =>
        {
            foreach (var evt in events)
            {
                switch (evt.Type)
                {
                    case EventType.Birth:
                        _birthCounter++;
                        break;
                    case EventType.Death:
                        _deathCounter++;
                        // GDD v1.7: Classify death by checking the message
                        if (evt.Message.Contains("starvation"))
                            _starvationDeathCounter++;
                        else if (evt.Message.Contains("exposure"))
                            _exposureDeathCounter++;
                        else
                            _oldAgeDeathCounter++;
                        break;
                    case EventType.Discovery:
                        _discoveryCounter++;
                        _discoveryEvents.Add(new SimulationEvent(evt.Tick, evt.Message, evt.Type));
                        break;
                }
            }
        });

        // Spawn agents
        Console.WriteLine($"Spawning {startingAgents} agents...");
        for (int i = 0; i < startingAgents; i++)
            simulation.SpawnAgent();

        // Trace lines collected per tick
        var traceLines = new List<string>();

        // Wire up trace callback if in trace mode
        if (verbosity == Verbosity.Trace)
        {
            simulation.TraceCallback = line => traceLines.Add(line);
        }

        Console.WriteLine($"Running {tickCount} ticks...");
        Console.WriteLine();

        int actualLastTick = tickCount;
        ResetCounters();

        // Track last snapshot values for progress display between samples
        int lastSnapshotAlive = startingAgents;
        int lastSnapshotDiscoveries = 0;

        for (int t = 1; t <= tickCount; t++)
        {
            traceLines.Clear();

            simulation.Tick();

            bool isSampleTick = (t % SimConfig.DiagnosticSampleInterval == 0) || t == 1;

            // GDD v1.6.2: Full snapshot only on sample ticks
            if (isSampleTick)
            {
                var snapshot = CaptureTickSnapshot(simulation, traceLines);

                // Override with accumulated event-driven counters (more accurate for inter-sample periods)
                snapshot.BirthsThisTick = _birthCounter;
                snapshot.DeathsThisTick = _deathCounter;
                snapshot.StarvationDeathsThisTick = _starvationDeathCounter;
                snapshot.OldAgeDeathsThisTick = _oldAgeDeathCounter;
                snapshot.ExposureDeathsThisTick = _exposureDeathCounter;

                logger.WriteTickSnapshot(snapshot);

                // Write CSV row (GDD v1.7: added ExposureDeaths, ShelterCoverage, GranaryFood)
                csv.WriteLine($"{snapshot.Tick},{snapshot.AliveCount},{snapshot.BirthsThisTick},{snapshot.DeathsThisTick},{snapshot.StarvationDeathsThisTick},{snapshot.OldAgeDeathsThisTick},{snapshot.ExposureDeathsThisTick},{snapshot.AvgHunger:F1},{snapshot.AvgHealth:F1},{snapshot.OldestAge},{snapshot.TotalFoodOnMap},{snapshot.DiscoveryCount},{snapshot.ShelterCoverage:F2},{snapshot.GranaryFood},{snapshot.SettlementCount}");

                // Reset counters after writing
                ResetCounters();

                lastSnapshotAlive = snapshot.AliveCount;
                lastSnapshotDiscoveries = snapshot.DiscoveryCount;

                // Early termination: stop if all agents have died
                if (snapshot.AliveCount == 0)
                {
                    actualLastTick = snapshot.Tick;
                    Console.Write($"\r  Tick {t}/{tickCount} | Pop: 0 | EXTINCT");
                    Console.WriteLine();
                    Console.WriteLine();
                    Console.WriteLine($"  ** All agents died at tick {snapshot.Tick} ({Agent.FormatTicks(snapshot.Tick)}). Ending early. **");
                    break;
                }
            }
            else
            {
                // Between samples: lightweight extinction check only
                int aliveNow = simulation.Agents.Count(a => a.IsAlive);
                if (aliveNow == 0)
                {
                    actualLastTick = t;
                    // Write final sample at extinction
                    var snapshot = CaptureTickSnapshot(simulation, traceLines);
                    snapshot.BirthsThisTick = _birthCounter;
                    snapshot.DeathsThisTick = _deathCounter;
                    snapshot.StarvationDeathsThisTick = _starvationDeathCounter;
                    snapshot.OldAgeDeathsThisTick = _oldAgeDeathCounter;
                    snapshot.ExposureDeathsThisTick = _exposureDeathCounter;
                    logger.WriteTickSnapshot(snapshot);
                    csv.WriteLine($"{snapshot.Tick},{snapshot.AliveCount},{snapshot.BirthsThisTick},{snapshot.DeathsThisTick},{snapshot.StarvationDeathsThisTick},{snapshot.OldAgeDeathsThisTick},{snapshot.ExposureDeathsThisTick},{snapshot.AvgHunger:F1},{snapshot.AvgHealth:F1},{snapshot.OldestAge},{snapshot.TotalFoodOnMap},{snapshot.DiscoveryCount},{snapshot.ShelterCoverage:F2},{snapshot.GranaryFood},{snapshot.SettlementCount}");

                    Console.Write($"\r  Tick {t}/{tickCount} | Pop: 0 | EXTINCT");
                    Console.WriteLine();
                    Console.WriteLine();
                    Console.WriteLine($"  ** All agents died at tick {t} ({Agent.FormatTicks(t)}). Ending early. **");
                    break;
                }
                lastSnapshotAlive = aliveNow;
            }

            // GDD v1.6.2: Buffered flush on cadence
            if (t % SimConfig.DiagnosticFlushInterval == 0)
                csv.Flush();

            // Progress indicator every 50 ticks
            if (t % 50 == 0 || t == tickCount)
            {
                Console.Write($"\r  Tick {t}/{tickCount} | Pop: {lastSnapshotAlive} alive | Discoveries: {lastSnapshotDiscoveries}");
            }
        }

        // Final flush
        csv.Flush();

        Console.WriteLine();
        Console.WriteLine();

        // Final report
        var finalAgents = simulation.Agents.Select(a => new AgentSnapshot
        {
            Id = a.Id,
            Name = a.Name,
            X = a.X,
            Y = a.Y,
            Hunger = a.Hunger,
            Health = a.Health,
            Age = a.Age,
            IsAlive = a.IsAlive,
            Action = a.CurrentAction,
            Inventory = new Dictionary<ResourceType, int>(a.Inventory),
            Restlessness = a.Restlessness,
            PeakRestlessness = a.PeakRestlessness,
            RestlessnessSum = a.RestlessnessSum,
            RestlessnessSampleCount = a.RestlessnessSampleCount,
            RestlessnessAbove50Ticks = a.RestlessnessAbove50Ticks
        }).ToList();

        logger.WriteFinalReport(actualLastTick, finalAgents);

        // Close CSV before HTML generator reads it
        csv.Dispose();

        // Generate HTML dashboard
        string htmlPath = Path.ChangeExtension(logFilePath, ".html");
        HtmlDashboardGenerator.GenerateSingleRun(csvPath, htmlPath,
            new RunMetadata
            {
                WorldSize = worldSize,
                StartingAgents = startingAgents,
                RequestedTicks = tickCount,
                ActualTicks = actualLastTick,
                Seed = seed,
                Verbosity = verbosity.ToString()
            },
            logger.DiscoveryTimeline);

        Console.WriteLine($"Log written to:       {Path.GetFullPath(logFilePath)}");
        Console.WriteLine($"CSV written to:       {Path.GetFullPath(csvPath)}");
        Console.WriteLine($"Dashboard written to: {Path.GetFullPath(htmlPath)}");
    }

    /// <summary>
    /// GDD v1.6.2: Simplified snapshot capture. Called only on sample ticks.
    /// No longer tracks prev alive set (event-driven counters handle births/deaths).
    /// </summary>
    private TickSnapshot CaptureTickSnapshot(Simulation simulation, List<string> traceLines)
    {
        var snapshot = new TickSnapshot
        {
            Tick = simulation.CurrentTick,
            TraceLines = new List<string>(traceLines)
        };

        // Agent snapshots
        foreach (var agent in simulation.Agents)
        {
            snapshot.AgentSnapshots.Add(new AgentSnapshot
            {
                Id = agent.Id,
                Name = agent.Name,
                X = agent.X,
                Y = agent.Y,
                Hunger = agent.Hunger,
                Health = agent.Health,
                Age = agent.Age,
                IsAlive = agent.IsAlive,
                Action = agent.CurrentAction,
                Inventory = new Dictionary<ResourceType, int>(agent.Inventory)
            });
        }

        // Population stats
        var alive = simulation.Agents.Where(a => a.IsAlive).ToList();

        snapshot.AliveCount = alive.Count;
        snapshot.TotalCount = simulation.Agents.Count;

        // Births/Deaths filled by caller from event-driven counters

        // Averages
        if (alive.Count > 0)
        {
            snapshot.AvgHunger = (float)alive.Average(a => a.Hunger);
            snapshot.AvgHealth = (float)alive.Average(a => a.Health);
            snapshot.OldestAge = alive.Max(a => a.Age);
        }

        // World resource totals
        var worldRes = new Dictionary<ResourceType, int>();
        int totalFood = 0;
        for (int x = 0; x < simulation.World.Width; x++)
        {
            for (int y = 0; y < simulation.World.Height; y++)
            {
                var tile = simulation.World.GetTile(x, y);
                foreach (var kvp in tile.Resources)
                {
                    if (!worldRes.ContainsKey(kvp.Key))
                        worldRes[kvp.Key] = 0;
                    worldRes[kvp.Key] += kvp.Value;
                }
                totalFood += tile.TotalFood();
            }
        }
        snapshot.WorldResources = worldRes;
        snapshot.TotalFoodOnMap = totalFood;

        // GDD v1.7: Shelter coverage — % of alive agents NOT exposed
        if (alive.Count > 0)
        {
            int sheltered = alive.Count(a => !a.IsExposed);
            snapshot.ShelterCoverage = (float)sheltered / alive.Count;
        }

        // GDD v1.7: Total granary food across all granaries
        int granaryFood = 0;
        for (int gx = 0; gx < simulation.World.Width; gx++)
            for (int gy = 0; gy < simulation.World.Height; gy++)
            {
                var gTile = simulation.World.GetTile(gx, gy);
                if (gTile.HasGranary)
                    granaryFood += gTile.GranaryTotalFood;
            }
        snapshot.GranaryFood = granaryFood;

        // Scan all agents' knowledge into cumulative tracker (same as Simulation.GetStats)
        foreach (var a in simulation.Agents)
            foreach (var k in a.Knowledge)
                simulation.CumulativeDiscoveries.Add(k);
        snapshot.DiscoveryCount = simulation.CumulativeDiscoveries.Count;

        // GDD v1.7.1: Settlement count
        snapshot.SettlementCount = simulation.Settlements.Count;

        // Events: merge current-tick events with any discovery events captured between snapshots
        var events = new List<SimulationEvent>(simulation.Logger.GetEventsForTick(simulation.CurrentTick));
        events.AddRange(_discoveryEvents);
        snapshot.Events = events;

        return snapshot;
    }
}
