using CivSim.Core.Events;

namespace CivSim.Core;

/// <summary>
/// Main simulation controller. Manages the world, agents, and tick-based updates.
/// Uses SimConfig for all tuning values.
/// GDD v1.6.2: EventBus for typed event dispatch, incremental spatial index, pressure map cadence.
/// GDD v1.8: JSON-loaded curated names, gendered agents, RecipeId event tracking.
/// </summary>
public class Simulation
{
    public World World { get; }
    public EventLogger Logger { get; }
    public EventBus EventBus { get; }
    public List<Agent> Agents { get; }
    public int CurrentTick { get; private set; }
    public bool IsRunning { get; set; }

    /// <summary>Optional trace callback for diagnostic logging. When set, AI decisions emit trace strings.</summary>
    public Action<string>? TraceCallback { get; set; }

    /// <summary>GDD v1.8 Testing Infrastructure: Optional run logger.
    /// Set this before the first Tick() to capture all decisions and system events.</summary>
    public RunLogger? RunLogger
    {
        get => agentAI.Logger;
        set => agentAI.Logger = value;
    }

    private readonly AgentAI agentAI;
    private readonly Random random;
    private int ticksSinceLastDiscovery;
    private (int X, int Y)? spawnCenter;

    /// <summary>Gender of the first spawned agent, for founding pair guarantee.</summary>
    private bool? _firstAgentMale;

    public (int X, int Y)? SpawnCenter => spawnCenter;
    public int PeakPopulation { get; private set; }

    /// <summary>GDD v1.8 Section 2: Communal knowledge propagation system.</summary>
    public SettlementKnowledge SettlementKnowledgeSystem { get; }

    /// <summary>GDD v1.7.1: Currently detected settlements (updated every 100 ticks).</summary>
    public List<Settlement> Settlements { get; private set; } = new();
    private readonly HashSet<string> _knownSettlementNames = new();

    /// <summary>GDD v1.7.2: Persistent settlement names keyed by approximate center position.
    /// Prevents settlements from getting renamed every detection cycle.</summary>
    private readonly Dictionary<(int X, int Y), string> _persistentSettlementNames = new();

    /// <summary>Tracks all discoveries ever made across the civilization, surviving agent deaths.</summary>
    public HashSet<string> CumulativeDiscoveries { get; } = new();

    public Simulation(World world, int seed)
    {
        World = world;
        Logger = new EventLogger();
        EventBus = new EventBus();
        Agents = new List<Agent>();
        CurrentTick = 0;
        IsRunning = false;
        ticksSinceLastDiscovery = 0;

        random = new Random(seed);
        agentAI = new AgentAI(random);

        // GDD v1.8 Section 4: Load curated name lists from Data folder
        string dataFolder = Path.Combine(AppContext.BaseDirectory, "Data");
        if (!Directory.Exists(dataFolder))
            dataFolder = Path.Combine(Directory.GetCurrentDirectory(), "Data");
        if (!Directory.Exists(dataFolder))
            dataFolder = Path.Combine(Directory.GetCurrentDirectory(), "CivSim.Core", "Data");
        NameGenerator.LoadNames(dataFolder);

        // GDD v1.8: Initialize communal knowledge propagation
        SettlementKnowledgeSystem = new SettlementKnowledge();

        // GDD v1.8: Wire discovery callback — route discoveries to communal knowledge system
        agentAI.OnDiscoveryCallback = (agent, discoveryId) =>
        {
            SettlementKnowledgeSystem.OnDiscovery(agent, discoveryId, Settlements, Agents, EventBus, CurrentTick);
        };

        // Subscribe Logger as EventBus bridge (Raylib renderer reads from Logger)
        EventBus.Subscribe(events => Logger.IngestFromBus(events));
    }

    /// <summary>
    /// Spawns an agent near the spawn center (clustered).
    /// First call finds the best spawn center near map center (highest food + diversity).
    /// Subsequent calls place agents within SpawnClusterRadius of that center.
    /// GDD v1.6.2: Uses incremental spatial index instead of full rebuild.
    /// </summary>
    public Agent SpawnAgent()
    {
        if (!spawnCenter.HasValue)
            spawnCenter = FindSpawnCenter();

        int cx = spawnCenter.Value.X;
        int cy = spawnCenter.Value.Y;
        int radius = SimConfig.SpawnClusterRadius;

        // Try to place within cluster radius, avoiding water and already-occupied tiles
        for (int attempt = 0; attempt < 100; attempt++)
        {
            int x = cx + random.Next(-radius, radius + 1);
            int y = cy + random.Next(-radius, radius + 1);

            if (!World.IsInBounds(x, y)) continue;
            var tile = World.GetTile(x, y);
            if (tile.Biome == BiomeType.Water) continue;

            // Avoid stacking agents on the same tile at spawn
            if (World.GetAgentsAt(x, y).Count > 0) continue;

            var livingNames = Agents.Where(a => a.IsAlive).Select(a => a.Name);
            var agent = new Agent(x, y, startingAge: SimConfig.InitialAgentAge, rng: random, livingNames: livingNames);

            // Systemic: Guarantee M+F founding pair
            if (_firstAgentMale == null)
            {
                _firstAgentMale = agent.IsMale;
            }
            else if (Agents.Count == 1) // Second agent being spawned
            {
                // Force opposite gender and regenerate name to match
                if (agent.IsMale == _firstAgentMale.Value)
                {
                    agent.IsMale = !_firstAgentMale.Value;
                    var livingNamesNow = Agents.Where(a => a.IsAlive).Select(a => a.Name);
                    agent.Name = NameGenerator.Generate(random, agent.IsMale, livingNamesNow);
                }
            }

            // Founding pair shares spawn center as home so they settle together
            agent.HomeTile = Agents.Count < 2 ? (cx, cy) : (x, y);
            Agents.Add(agent);
            World.AddAgentToIndex(agent);
            EventBus.Emit(CurrentTick, $"{agent.Name} spawned at ({x},{y})", EventType.Birth, agentId: agent.Id);
            return agent;
        }

        // Fallback: place at spawn center directly
        var livingNamesFallback = Agents.Where(a => a.IsAlive).Select(a => a.Name);
        var fallback = new Agent(cx, cy, startingAge: SimConfig.InitialAgentAge, rng: random, livingNames: livingNamesFallback);

        // Systemic: Guarantee M+F founding pair
        if (_firstAgentMale == null)
        {
            _firstAgentMale = fallback.IsMale;
        }
        else if (Agents.Count == 1) // Second agent being spawned
        {
            // Force opposite gender and regenerate name to match
            if (fallback.IsMale == _firstAgentMale.Value)
            {
                fallback.IsMale = !_firstAgentMale.Value;
                var livingNamesNow = Agents.Where(a => a.IsAlive).Select(a => a.Name);
                fallback.Name = NameGenerator.Generate(random, fallback.IsMale, livingNamesNow);
            }
        }

        fallback.HomeTile = (cx, cy);
        Agents.Add(fallback);
        World.AddAgentToIndex(fallback);
        EventBus.Emit(CurrentTick, $"{fallback.Name} spawned at ({cx},{cy}) [fallback]", EventType.Birth, agentId: fallback.Id);
        return fallback;
    }

    /// <summary>
    /// Finds the best spawn center near the map center.
    /// Scores Plains/Forest tiles by food availability + resource diversity.
    /// </summary>
    private (int X, int Y) FindSpawnCenter()
    {
        int midX = World.Width / 2;
        int midY = World.Height / 2;
        int searchRadius = World.Width / 4;

        (int X, int Y) best = (midX, midY);
        int bestScore = -1;

        for (int x = midX - searchRadius; x <= midX + searchRadius; x++)
        {
            for (int y = midY - searchRadius; y <= midY + searchRadius; y++)
            {
                if (!World.IsInBounds(x, y)) continue;
                var tile = World.GetTile(x, y);

                if (tile.Biome != BiomeType.Plains && tile.Biome != BiomeType.Forest)
                    continue;

                // Score = total food + number of distinct resource types
                int foodScore = tile.TotalFood();
                int diversity = tile.Resources.Count;
                int score = foodScore + diversity * 3;

                // Bonus for neighbors with food
                var neighbors = World.GetNeighbors(x, y, 2);
                foreach (var n in neighbors)
                    score += n.TotalFood() > 0 ? 1 : 0;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = (x, y);
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Advances the simulation by one tick.
    /// GDD v1.6.2: Incremental spatial index, tiered perception, event bus dispatch, pressure map cadence.
    /// GDD v1.7: Exposure system (pre-UpdateNeeds), death cause tracking, granary support.
    /// Order: Reset counters -> Exposure calc -> Update needs -> Perception -> AI decisions -> Add children -> Ecology -> Events.
    /// </summary>
    public void Tick()
    {
        CurrentTick++;
        World.MovesThisTick = 0;

        // Phase 0 (GDD v1.7): Calculate exposure for each alive agent
        foreach (var agent in Agents.Where(a => a.IsAlive))
        {
            bool sheltered = false;
            int radius = SimConfig.ExposureShelterRadius;
            for (int dx = -radius; dx <= radius && !sheltered; dx++)
            {
                for (int dy = -radius; dy <= radius && !sheltered; dy++)
                {
                    int tx = agent.X + dx;
                    int ty = agent.Y + dy;
                    if (World.IsInBounds(tx, ty) && World.GetTile(tx, ty).HasShelter)
                        sheltered = true;
                }
            }
            agent.IsExposed = !sheltered;
        }

        // Phase 1: Update all agent needs (hunger, health, age, mortality, exposure)
        foreach (var agent in Agents.Where(a => a.IsAlive).ToList())
        {
            var tile = World.GetTile(agent.X, agent.Y);
            agent.UpdateNeeds(tile, CurrentTick);

            if (agent.Health <= 0)
            {
                // GDD v1.7: Determine death cause
                string cause;
                if (agent.Hunger <= 0f)
                    cause = "starvation";
                else if (agent.Age >= SimConfig.OldAgeThreshold)
                    cause = "old age";
                else
                    cause = "exposure";

                agent.Die(tile, cause, CurrentTick);
                World.RemoveAgentFromIndex(agent);
                EventBus.Emit(CurrentTick, $"{agent.Name} died of {cause} at age {Agent.FormatTicks(agent.Age)}", EventType.Death, agentId: agent.Id);

                // GDD v1.8: Notify knowledge system of death (explorer knowledge loss)
                SettlementKnowledgeSystem.OnAgentDeath(agent, Settlements, EventBus, CurrentTick);
            }
        }

        // Phase 1.5: Perception — each agent scans their surroundings (tiered)
        foreach (var agent in Agents.Where(a => a.IsAlive))
        {
            agent.DecayMemory(CurrentTick);
            agent.Perceive(World, CurrentTick);
        }

        // Phase 2: AI decisions for each living agent
        foreach (var agent in Agents.Where(a => a.IsAlive).ToList())
        {
            agentAI.DecideAction(agent, World, EventBus, CurrentTick, Agents, SettlementKnowledgeSystem, Settlements, TraceCallback);
        }

        // Phase 3: Add children born this tick
        var newChildren = agentAI.FlushPendingChildren();
        foreach (var child in newChildren)
        {
            Agents.Add(child);
            World.AddAgentToIndex(child);
        }

        // Track peak population
        int aliveNow = Agents.Count(a => a.IsAlive);
        if (aliveNow > PeakPopulation) PeakPopulation = aliveNow;

        // Phase 4: Ecology — pressure map on cadence, resource regeneration every tick
        bool shouldRebuildPressure = (CurrentTick % SimConfig.PressureMapUpdateInterval == 0);
        if (World.MovesThisTick >= SimConfig.PressureMapMigrationTrigger)
            shouldRebuildPressure = true;
        if (shouldRebuildPressure)
            World.BuildPressureMap();

        World.RegenerateResources(CurrentTick);

        // GDD v1.8 Section 7: Home storage food decay
        // Each shelter tile with stored food decays 1 unit per interval (lean-to faster than improved)
        foreach (var agent in Agents.Where(a => a.IsAlive && a.HomeTile.HasValue))
        {
            var homeTile = World.GetTile(agent.HomeTile!.Value.X, agent.HomeTile.Value.Y);
            if (homeTile.HasHomeStorage && homeTile.HomeTotalFood > 0)
            {
                int decayInterval = homeTile.HomeDecayInterval;
                if (CurrentTick - homeTile.LastHomeDecayTick >= decayInterval)
                {
                    homeTile.DecayOneHomeFood();
                    homeTile.LastHomeDecayTick = CurrentTick;
                }
            }
        }

        // GDD v1.8 Section 3: Sample stability history every sim-day
        if (CurrentTick % SimConfig.TicksPerSimDay == 0)
        {
            foreach (var agent in Agents.Where(a => a.IsAlive))
                agent.SampleHistory(CurrentTick, World);
        }

        // GDD v1.7.1: Social bond decay — every SocialBondDecayInterval ticks
        if (CurrentTick % SimConfig.SocialBondDecayInterval == 0)
        {
            int proximityRadius = SimConfig.SocialBondProximityRadius;
            foreach (var agent in Agents.Where(a => a.IsAlive))
            {
                var toRemove = new List<int>();
                foreach (var kvp in agent.SocialBonds)
                {
                    // Find the bonded agent
                    var other = Agents.FirstOrDefault(a => a.Id == kvp.Key && a.IsAlive);
                    if (other == null)
                    {
                        toRemove.Add(kvp.Key); // Dead agent — remove bond
                        continue;
                    }

                    // If not within proximity radius, decay by 1
                    int dist = Math.Max(Math.Abs(other.X - agent.X), Math.Abs(other.Y - agent.Y));
                    if (dist > proximityRadius)
                    {
                        agent.SocialBonds[kvp.Key] = kvp.Value - 1;
                        if (agent.SocialBonds[kvp.Key] <= 0)
                            toRemove.Add(kvp.Key);
                    }
                }
                foreach (var id in toRemove)
                    agent.SocialBonds.Remove(id);
            }
        }

        // Stagnation detection — check staged events before flush
        if (EventBus.HasStagedEventOfType(EventType.Discovery))
            ticksSinceLastDiscovery = 0;
        else
            ticksSinceLastDiscovery++;

        if (ticksSinceLastDiscovery >= SimConfig.StagnationWarningTicks && CurrentTick % SimConfig.StagnationWarningTicks == 0)
        {
            EventBus.Emit(CurrentTick,
                $"Stagnation warning: {ticksSinceLastDiscovery} ticks since last discovery",
                EventType.Info);
        }

        // GDD v1.8: Milestone logging every MilestoneLogInterval ticks (~1 year)
        if (CurrentTick % SimConfig.MilestoneLogInterval == 0)
        {
            int aliveCount = Agents.Count(a => a.IsAlive);
            EventBus.Emit(CurrentTick, $"Year {SimConfig.TicksToYears(CurrentTick):F0} - Population: {aliveCount}", EventType.Milestone);
        }

        // GDD v1.8: Settlement detection every SettlementDetectionInterval ticks (~1 season)
        if (CurrentTick % SimConfig.SettlementDetectionInterval == 0)
        {
            Settlements = SettlementDetector.Detect(World, Agents, CurrentTick, random);

            // Assign persistent names: if a settlement center is within 3 tiles of a known position, reuse the name
            foreach (var settlement in Settlements)
            {
                string? existingName = null;
                foreach (var kvp in _persistentSettlementNames)
                {
                    int dist = Math.Max(Math.Abs(kvp.Key.X - settlement.CenterX),
                                        Math.Abs(kvp.Key.Y - settlement.CenterY));
                    if (dist <= 3)
                    {
                        existingName = kvp.Value;
                        break;
                    }
                }

                if (existingName != null)
                {
                    settlement.Name = existingName;
                }
                else
                {
                    // New settlement — store its name for persistence
                    _persistentSettlementNames[(settlement.CenterX, settlement.CenterY)] = settlement.Name;
                }
            }

            // Emit events for newly discovered settlements
            foreach (var settlement in Settlements)
            {
                if (!_knownSettlementNames.Contains(settlement.Name))
                {
                    _knownSettlementNames.Add(settlement.Name);
                    EventBus.Emit(CurrentTick,
                        $"Settlement '{settlement.Name}' founded at ({settlement.CenterX},{settlement.CenterY}) with {settlement.ShelterCount} shelters!",
                        EventType.Discovery);
                }
            }
        }

        // GDD v1.8 Phase 5: Communal knowledge propagation
        // Always call UpdatePropagation — founding groups may have pending propagations
        // even before any formal settlement (with shelters) is detected.
        SettlementKnowledgeSystem.UpdatePropagation(Settlements, Agents, EventBus, CurrentTick);

        // GDD v1.8 Section 5: Geographic knowledge transfer — agents at home with pending discoveries
        // share them with settlement lore. This is the backup path; primary transfer happens in
        // AgentAI.CompleteMove() when agent arrives at HomeTile.
        if (Settlements.Count > 0)
        {
            foreach (var agent in Agents.Where(a => a.IsAlive
                && a.HomeTile.HasValue
                && a.PendingGeographicDiscoveries.Count > 0
                && a.X == a.HomeTile.Value.X && a.Y == a.HomeTile.Value.Y))
            {
                foreach (var discovery in agent.PendingGeographicDiscoveries)
                {
                    SettlementKnowledgeSystem.AddGeographicKnowledge(
                        agent, discovery.X, discovery.Y, discovery.FeatureType,
                        discovery.Resource, Settlements, EventBus, CurrentTick);
                }
                agent.ClearPendingGeographicDiscoveries();
            }
        }

        // Dispatch all events accumulated during this tick to subscribers
        EventBus.FlushTick();
    }

    /// <summary>Gets current simulation statistics.</summary>
    public SimulationStats GetStats()
    {
        // Accumulate all discoveries ever made (alive + dead agents) into cumulative tracker
        foreach (var a in Agents)
            foreach (var k in a.Knowledge)
                CumulativeDiscoveries.Add(k);

        return new SimulationStats
        {
            CurrentTick = CurrentTick,
            TotalAgents = Agents.Count,
            AliveAgents = Agents.Count(a => a.IsAlive),
            DeadAgents = Agents.Count(a => !a.IsAlive),
            OldestAgent = Agents.Where(a => a.IsAlive).MaxBy(a => a.Age)?.Age ?? 0,
            TotalEvents = Logger.TotalEvents,
            TotalDiscoveries = CumulativeDiscoveries.Count,
            DiscoveredKnowledge = new HashSet<string>(CumulativeDiscoveries),
            WorldSeed = World.Seed,
            SettlementCount = Settlements.Count(s => s.Id >= 0),
            SettlementNames = Settlements.Where(s => s.Id >= 0).Select(s => s.Name).ToList()
        };
    }
}

/// <summary>Statistics about the current simulation state.</summary>
public class SimulationStats
{
    public int CurrentTick { get; set; }
    public int TotalAgents { get; set; }
    public int AliveAgents { get; set; }
    public int DeadAgents { get; set; }
    public int OldestAgent { get; set; }
    public int TotalEvents { get; set; }
    public int TotalDiscoveries { get; set; }
    /// <summary>Set of all discovery IDs ever achieved (survives agent death). For UI display.</summary>
    public HashSet<string> DiscoveredKnowledge { get; set; } = new();
    public int WorldSeed { get; set; }

    /// <summary>GDD v1.7.1: Number of currently detected settlements.</summary>
    public int SettlementCount { get; set; }

    /// <summary>GDD v1.7.1: Names of currently detected settlements.</summary>
    public List<string> SettlementNames { get; set; } = new();

    public override string ToString()
    {
        return $"Tick: {CurrentTick} | Population: {AliveAgents}/{TotalAgents} | Oldest: {OldestAgent} | Events: {TotalEvents}";
    }
}
