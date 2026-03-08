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
    public int CurrentTick { get; internal set; }
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
    private readonly Random animalRng;
    private readonly Random trapRng;
    private readonly Random domesticRng; // D25d: Separate RNG for breeding/pup spawning to avoid cascading animal AI
    private int ticksSinceLastDiscovery;
    private (int X, int Y)? spawnCenter;

    /// <summary>Gender of the first spawned agent, for founding pair guarantee.</summary>
    private bool? _firstAgentMale;

    public (int X, int Y)? SpawnCenter => spawnCenter;
    public int PeakPopulation { get; private set; }

    /// <summary>Tick when peak population was reached.</summary>
    public int PeakPopulationTick { get; private set; }

    /// <summary>Records of all discoveries: (tick, recipeId, agentName).</summary>
    public List<(int Tick, string RecipeId, string AgentName)> DiscoveryRecords { get; } = new();

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
        animalRng = new Random(seed + 7919); // D25a: Separate RNG for animal AI to avoid cascading agent behavior
        trapRng = new Random(seed + 8527); // D25c: Separate RNG for trap processing to avoid cascading agent behavior
        domesticRng = new Random(seed + 9341); // D25d: Separate RNG for breeding/pup spawning
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

        // Subscribe to capture discovery records for run summary
        EventBus.Subscribe(events =>
        {
            foreach (var evt in events)
            {
                if (evt.Type != EventType.Discovery) continue;
                // Skip propagation/build events (communal knowledge spread, not original discovery)
                if (evt.Message.Contains("learned") || evt.Message.Contains("oral tradition")
                    || evt.Message.Contains("propagation") || evt.Message.Contains("built")
                    || evt.Message.Contains("constructed") || evt.Message.Contains("Settlement"))
                    continue;

                string recipeId = evt.RecipeId ?? evt.Message;
                string agentName = evt.AgentId >= 0
                    ? Agents.FirstOrDefault(a => a.Id == evt.AgentId)?.Name ?? "Unknown"
                    : "Unknown";
                DiscoveryRecords.Add((evt.Tick, recipeId, agentName));
            }
        });
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
            agent.BirthTick = CurrentTick;
            // D19: Founders start mildly motivated
            agent.Restlessness = SimConfig.RestlessnessFounderStart;
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
        fallback.BirthTick = CurrentTick;
        // D19: Founders start mildly motivated
        fallback.Restlessness = SimConfig.RestlessnessFounderStart;
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

                agent.Die(tile, cause, CurrentTick, World);
                World.RemoveAgentFromIndex(agent);
                EventBus.Emit(CurrentTick, $"{agent.Name} died of {cause} at age {Agent.FormatTicks(agent.Age)}", EventType.Death, agentId: agent.Id);

                // GDD v1.8: Notify knowledge system of death (explorer knowledge loss)
                SettlementKnowledgeSystem.OnAgentDeath(agent, Settlements, EventBus, CurrentTick);
            }
        }

        // Phase 1.25: Directive #9 Fix 1+5 — Maturation transition
        // Detect child→adult transition after Age increment (Phase 1) but before AI (Phase 2).
        // Resets stale state so newly matured agents start fresh at home.
        foreach (var agent in Agents.Where(a => a.IsAlive))
        {
            if (!agent.HasMatured && agent.Stage == DevelopmentStage.Adult && agent.Age > 0)
            {
                // Check if this agent was born as a child (has Parent relationships)
                bool wasChild = agent.Relationships.Values.Any(r => r == RelationshipType.Parent);
                if (wasChild)
                {
                    agent.HasMatured = true;

                    // D24 Fix 3: Start new adult bootstrap
                    agent.IsNewAdult = true;
                    agent.NewAdultTicksRemaining = SimConfig.NewAdultBootstrapDuration;

                    // Fix 1: Full state reset — clear stale goals/actions from shadow-following phase
                    agent.ClearGoal();
                    agent.ClearPendingAction();
                    agent.TransitionMode(BehaviorMode.Home, CurrentTick);
                    agent.StuckCounter = 0;
                    agent.LastDecisionTick = CurrentTick;

                    // Teleport to home tile — don't start life mid-stride on a phantom path
                    if (agent.HomeTile.HasValue)
                    {
                        World.RemoveAgentFromIndex(agent);
                        agent.X = agent.HomeTile.Value.X;
                        agent.Y = agent.HomeTile.Value.Y;
                        World.AddAgentToIndex(agent);
                    }

                    // Fix 5: Update parents' dependent tracking + Fix 3: transfer parent memories
                    foreach (var kvp in agent.Relationships)
                    {
                        if (kvp.Value != RelationshipType.Parent) continue;
                        var parent = Agents.FirstOrDefault(a => a.Id == kvp.Key && a.IsAlive);
                        if (parent != null)
                        {
                            // No cached DependentCount to update — HasYoungDependents recalculates each time.
                            // But emit event so it's visible in logs.
                            EventBus.Emit(CurrentTick, $"{agent.Name} matured — {parent.Name} no longer has a young dependent",
                                EventType.Action, agentId: parent.Id);

                            // Fix 3: Transfer parent's resource memories to newly matured agent.
                            // "Grandpa told us about the berry bushes east of camp."
                            // Without this, newly matured agents have no memory of resource locations
                            // and fall into scoring dead zones (Rest/Idle/Move loops).
                            foreach (var mem in parent.Memory)
                            {
                                if (mem.Type == MemoryType.Resource && mem.Resource.HasValue
                                    && CurrentTick - mem.TickObserved <= SimConfig.MemoryDecayTicks)
                                {
                                    // Check if agent already has this memory
                                    bool alreadyKnown = agent.Memory.Any(m =>
                                        m.X == mem.X && m.Y == mem.Y && m.Type == mem.Type
                                        && m.Resource == mem.Resource);
                                    if (!alreadyKnown && agent.Memory.Count < SimConfig.MemoryMaxEntries)
                                    {
                                        agent.Memory.Add(new MemoryEntry
                                        {
                                            X = mem.X, Y = mem.Y,
                                            Type = MemoryType.Resource,
                                            Resource = mem.Resource,
                                            Quantity = mem.Quantity,
                                            TickObserved = CurrentTick
                                        });
                                    }
                                }
                            }
                        }
                    }

                    // Fix 3: Force active perception so newly matured agent sees surroundings
                    agent.ForceActivePerception = true;

                    EventBus.Emit(CurrentTick, $"{agent.Name} matured to adult at age {Agent.FormatTicks(agent.Age)}",
                        EventType.Action, agentId: agent.Id);
                }
            }
        }

        // Phase 1.3: D24 Fix 3 — Decrement new adult bootstrap timer
        foreach (var agent in Agents.Where(a => a.IsAlive && a.IsNewAdult))
        {
            agent.NewAdultTicksRemaining--;
            if (agent.NewAdultTicksRemaining <= 0)
            {
                agent.IsNewAdult = false;
                agent.NewAdultTicksRemaining = 0;
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
            int preX = agent.X, preY = agent.Y;
            agentAI.DecideAction(agent, World, EventBus, CurrentTick, Agents, SettlementKnowledgeSystem, Settlements, TraceCallback);

            // D15 sanity check: detect single-tick position jumps > 2 tiles.
            // Legitimate movement is 1 tile/tick via MoveTo(). Jumps > 2 indicate
            // a teleport-home fallback fired (A* returned no path). Log for diagnostics.
            int jumpDist = Math.Max(Math.Abs(agent.X - preX), Math.Abs(agent.Y - preY));
            if (jumpDist > 2)
            {
                EventBus.Emit(CurrentTick,
                    $"TELEPORT-DETECT: {agent.Name} jumped {jumpDist} tiles ({preX},{preY})->({agent.X},{agent.Y})",
                    EventType.Info, agentId: agent.Id);
            }
        }

        // D19: Update restlessness and stats per tick
        foreach (var agent in Agents.Where(a => a.IsAlive))
        {
            AgentAI.UpdateRestlessness(agent, CurrentTick, World);
            agent.UpdateRestlessnessStats();
        }

        // Phase 3: Add children born this tick
        var newChildren = agentAI.FlushPendingChildren();
        foreach (var child in newChildren)
        {
            child.BirthTick = CurrentTick;
            Agents.Add(child);
            World.AddAgentToIndex(child);
        }

        // Track peak population
        int aliveNow = Agents.Count(a => a.IsAlive);
        if (aliveNow > PeakPopulation)
        {
            PeakPopulation = aliveNow;
            PeakPopulationTick = CurrentTick;
        }

        // Update per-agent summary counters (action, mode, stuck, distance)
        foreach (var agent in Agents.Where(a => a.IsAlive))
            agent.UpdateSummaryCounters();

        // Phase 4: Ecology — pressure map on cadence, resource regeneration every tick
        bool shouldRebuildPressure = (CurrentTick % SimConfig.PressureMapUpdateInterval == 0);
        if (World.MovesThisTick >= SimConfig.PressureMapMigrationTrigger)
            shouldRebuildPressure = true;
        if (shouldRebuildPressure)
            World.BuildPressureMap();

        World.RegenerateResources(CurrentTick);

        // Phase 4.5: Animal AI (D25a)
        foreach (var animal in World.Animals)
        {
            if (!animal.IsAlive) continue;
            AnimalAI.UpdateAnimal(animal, World, CurrentTick, Agents, animalRng);
        }

        // Carcass decay (D25a/D25b)
        for (int i = World.Carcasses.Count - 1; i >= 0; i--)
        {
            var c = World.Carcasses[i];
            if (!c.IsActive) { World.Carcasses.RemoveAt(i); continue; }
            c.DecayTicksRemaining--;
            // D25b: At half-life (CarcassDecayTicks remaining), reduce meat by 50%
            if (c.DecayTicksRemaining == SimConfig.CarcassDecayTicks)
                c.MeatYield = Math.Max(1, (c.MeatYield + 1) / 2);
            if (c.DecayTicksRemaining <= 0)
                World.Carcasses.RemoveAt(i);
        }

        // Animal replenishment (D25a — every AnimalReplenishInterval ticks)
        if (CurrentTick % SimConfig.AnimalReplenishInterval == 0 && CurrentTick > 0)
        {
            ReplenishAnimals();
        }

        // Phase 4.6: Trap processing (D25c)
        ProcessTraps(World, CurrentTick, trapRng);

        // Phase 4.7: D25d Pen feeding
        if (CurrentTick % SimConfig.PenFeedInterval == 0)
        {
            foreach (var pen in World.Pens)
            {
                if (!pen.IsActive || pen.AnimalCount == 0) continue;
                foreach (var animalId in pen.AnimalIds.ToList())
                {
                    var animal = World.Animals.FirstOrDefault(a => a.Id == animalId && a.IsAlive);
                    if (animal == null) { pen.AnimalIds.Remove(animalId); continue; }
                    if (pen.FoodStore > 0)
                    {
                        pen.FoodStore--;
                        pen.LastFeedTick = CurrentTick;
                    }
                    else
                    {
                        // Starving in pen
                        animal.Health -= SimConfig.PenStarvationDamage;
                        if (animal.Health <= 0)
                        {
                            animal.Die();
                            pen.AnimalIds.Remove(animalId);
                            // Create carcass at pen tile
                            var config = Animal.SpeciesConfig[animal.Species];
                            World.Carcasses.Add(new Carcass(pen.TileX, pen.TileY, animal.Species,
                                config.MeatYield, config.HideYield, config.BoneYield));
                            EventBus.Emit(CurrentTick, $"A penned {animal.Species} starved to death!", EventType.Death);
                        }
                    }
                }
            }
        }

        // Phase 4.8: D25d Breeding check
        foreach (var pen in World.Pens)
        {
            if (!pen.IsActive || pen.IsFull) continue;
            // Group penned animals by species
            var speciesCounts = new Dictionary<AnimalSpecies, int>();
            foreach (var animalId in pen.AnimalIds)
            {
                var animal = World.Animals.FirstOrDefault(a => a.Id == animalId && a.IsAlive);
                if (animal != null)
                    speciesCounts[animal.Species] = speciesCounts.GetValueOrDefault(animal.Species) + 1;
            }
            foreach (var kvp in speciesCounts)
            {
                if (kvp.Value < 2) continue; // Need 2+ for breeding
                if (kvp.Key == AnimalSpecies.Wolf) continue; // Wolves/Dogs don't breed in Phase 3
                int breedInterval = GetBreedInterval(kvp.Key);
                if (CurrentTick % breedInterval != 0) continue;
                float breedChance = GetBreedChance(kvp.Key);
                if (domesticRng.NextDouble() >= breedChance) continue;
                if (pen.IsFull) break;
                // Spawn offspring
                var offspring = new Animal(kvp.Key, pen.TileX, pen.TileY, -1, (pen.TileX, pen.TileY));
                offspring.IsDomesticated = true;
                offspring.OwnerAgentId = pen.BuilderAgentId;
                offspring.PenId = pen.Id;
                offspring.State = AnimalState.Domesticated;
                World.Animals.Add(offspring);
                pen.AnimalIds.Add(offspring.Id);
                EventBus.Emit(CurrentTick, $"A {kvp.Key} was born in the pen!", EventType.Action);
            }
        }

        // Phase 4.9: D25d Wolf pup spawning
        // Only spawn pups when at least one agent can interact with them (knows bow + animal_domestication)
        bool anyCanTamePup = Agents.Any(a => a.IsAlive && a.Knowledge.Contains("bow") && a.Knowledge.Contains("animal_domestication"));
        if (anyCanTamePup && CurrentTick > SimConfig.WolfPackEstablishedAge && CurrentTick % SimConfig.WolfPupSpawnInterval == 0)
        {
            // Group alive, non-domesticated wolves by HerdId
            var wolfPacks = World.Animals
                .Where(a => a.IsAlive && a.Species == AnimalSpecies.Wolf && !a.IsDomesticated)
                .GroupBy(a => a.HerdId)
                .ToList();

            foreach (var pack in wolfPacks)
            {
                int adults = pack.Count(a => !a.IsPup);
                if (adults < 2) continue;

                int pups = pack.Count(a => a.IsPup);
                if (pups >= SimConfig.WolfMaxPupsPerPack) continue;

                if (domesticRng.NextDouble() >= SimConfig.WolfPupSpawnChance) continue;

                var leader = pack.First();
                var pup = new Animal(AnimalSpecies.Wolf, leader.X, leader.Y, leader.HerdId, leader.TerritoryCenter);
                pup.IsPup = true;
                pup.Health = 10; // Pup health — lower than adult MaxHealth of 25
                World.Animals.Add(pup);
                World.AddAnimalToIndex(pup);
                EventBus.Emit(CurrentTick, $"A wolf pup was born in a pack near ({leader.X},{leader.Y})!", EventType.Action);
            }
        }

        // Phase 4.95: D25d Dog night guard alerts
        if (Agent.IsNightTime(CurrentTick))
        {
            foreach (var dog in World.Animals.Where(a => a.IsAlive && a.IsDog && a.OwnerAgentId.HasValue))
            {
                foreach (var danger in World.Animals)
                {
                    if (!danger.IsAlive || danger.IsDomesticated) continue;
                    if (danger.Species != AnimalSpecies.Boar && danger.Species != AnimalSpecies.Wolf) continue;
                    int dist = Math.Max(Math.Abs(danger.X - dog.X), Math.Abs(danger.Y - dog.Y));
                    if (dist <= SimConfig.DogNightGuardRange)
                    {
                        EventBus.Emit(CurrentTick, $"Dog barks — {danger.Species} nearby!", EventType.Info);
                        break; // One alert per dog per tick
                    }
                }
            }
        }

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
            {
                agent.SampleHistory(CurrentTick, World);
                // Directive #5 Fix 1: Reset daily socialize counter at dawn
                agent.SocializeCountToday = 0;
                agent.SocializePartnerCountToday.Clear();
                agent.LastSocializeDawnResetTick = CurrentTick;
            }
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
        {
            ticksSinceLastDiscovery = 0;
            // D12 Fix 5: Update all agents' last discovery tick for curiosity ramp
            foreach (var a in Agents)
                if (a.IsAlive) a.LastSettlementDiscoveryTick = CurrentTick;
        }
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

        // US-007: Settlements are now persistent — founded when first shelter is built (in AgentAI.CompleteBuild).
        // No longer recreated each detection interval. Track new settlements for event emission.
        foreach (var settlement in Settlements)
        {
            if (settlement.Id > 0 && !_knownSettlementNames.Contains(settlement.Name))
            {
                _knownSettlementNames.Add(settlement.Name);
                _persistentSettlementNames[(settlement.CenterX, settlement.CenterY)] = settlement.Name;
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

    /// <summary>D25a: Slow animal replenishment — replaces losses, prevents extinction.</summary>
    // ── D25d: Breeding helpers ──────────────────────────────────────
    private static int GetBreedInterval(AnimalSpecies species) => species switch
    {
        AnimalSpecies.Rabbit => SimConfig.BreedIntervalRabbit,
        AnimalSpecies.Cow => SimConfig.BreedIntervalCow,
        AnimalSpecies.Sheep => SimConfig.BreedIntervalSheep,
        AnimalSpecies.Deer => SimConfig.BreedIntervalDeer,
        AnimalSpecies.Boar => SimConfig.BreedIntervalBoar,
        _ => 9999
    };

    private static float GetBreedChance(AnimalSpecies species) => species switch
    {
        AnimalSpecies.Rabbit => SimConfig.BreedChanceRabbit,
        AnimalSpecies.Cow => SimConfig.BreedChanceCow,
        AnimalSpecies.Sheep => SimConfig.BreedChanceSheep,
        AnimalSpecies.Deer => SimConfig.BreedChanceDeer,
        AnimalSpecies.Boar => SimConfig.BreedChanceBoar,
        _ => 0f
    };

    private void ReplenishAnimals()
    {
        // Cap at 120% of ever-created count
        int maxAnimals = (int)(World.Animals.Count * 1.2);
        int aliveCount = World.Animals.Count(a => a.IsAlive);
        if (aliveCount >= maxAnimals) return;

        // Group by herd
        var herds = World.Animals.GroupBy(a => a.HerdId).ToList();
        foreach (var herd in herds)
        {
            int alive = herd.Count(a => a.IsAlive);
            int original = herd.Count();
            if (alive >= original) continue; // No losses

            // 30% chance to spawn 1 replacement near territory center
            if (animalRng.NextDouble() >= 0.30) continue;

            var template = herd.First();
            var center = template.TerritoryCenter;

            // Don't spawn if agents are nearby
            bool agentNearby = false;
            foreach (var agent in Agents)
            {
                if (!agent.IsAlive) continue;
                int dist = Math.Max(Math.Abs(agent.X - center.X), Math.Abs(agent.Y - center.Y));
                if (dist <= 5) { agentNearby = true; break; }
            }
            if (agentNearby) continue;

            // Find passable tile near territory center
            for (int attempt = 0; attempt < 10; attempt++)
            {
                int nx = center.X + animalRng.Next(-2, 3);
                int ny = center.Y + animalRng.Next(-2, 3);
                if (!World.IsInBounds(nx, ny)) continue;
                var tile = World.GetTile(nx, ny);
                if (tile.Biome == BiomeType.Water) continue;

                var newAnimal = new Animal(template.Species, nx, ny, template.HerdId, template.TerritoryCenter);
                World.Animals.Add(newAnimal);
                World.AddAnimalToIndex(newAnimal);
                break;
            }
        }
    }

    /// <summary>D25c: Process traps — decay, catch rabbits.</summary>
    private void ProcessTraps(World world, int currentTick, Random rng)
    {
        for (int i = world.Traps.Count - 1; i >= 0; i--)
        {
            var trap = world.Traps[i];
            if (!trap.IsActive) continue;

            // Decay check
            if (currentTick - trap.TickPlaced >= SimConfig.TrapDecayTicks)
            {
                trap.IsActive = false;
                continue;
            }

            // Already has a caught carcass — skip catching
            if (trap.CaughtCarcass != null) continue;

            // Check for nearby rabbits
            Animal? nearbyRabbit = null;
            for (int dx = -1; dx <= 1 && nearbyRabbit == null; dx++)
                for (int dy = -1; dy <= 1 && nearbyRabbit == null; dy++)
                {
                    int nx = trap.X + dx, ny = trap.Y + dy;
                    if (!world.IsInBounds(nx, ny)) continue;
                    var animals = world.GetAnimalsAt(nx, ny);
                    nearbyRabbit = animals.FirstOrDefault(a =>
                        a.Species == AnimalSpecies.Rabbit && a.IsAlive);
                }

            if (nearbyRabbit != null && rng.NextDouble() < SimConfig.TrapCatchChance)
            {
                // Catch! Kill rabbit, create carcass on trap
                nearbyRabbit.Die();
                world.RemoveAnimalFromIndex(nearbyRabbit);
                var carcass = new Carcass(trap.X, trap.Y, AnimalSpecies.Rabbit,
                    meatYield: 1, hideYield: 0, boneYield: 0);
                trap.CaughtCarcass = carcass;
                world.Carcasses.Add(carcass);
            }
        }

        // Clean up inactive traps periodically
        if (currentTick % 1000 == 0)
            world.Traps.RemoveAll(t => !t.IsActive);
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
