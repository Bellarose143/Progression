using CivSim.Core;

namespace CivSim.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  TestSimBuilder — fluent builder for controlled simulation scenarios.
//
//  IMPORTANT: All positions (AgentAt, ShelterAt, ResourceAt, AgentHome) are
//  OFFSETS from the simulation's spawn center, not absolute world coordinates.
//  The builder discovers the spawn center by calling SpawnAgent() first.
//
//  Usage:
//      var sim = new TestSimBuilder()
//          .GridSize(32, 32).Seed(1)
//          .AddAgent("Alice", isMale: false).AgentAt("Alice", 0, 0)  // at spawn
//          .AgentHome("Alice", 0, 0).ShelterAt(0, 0)
//          .ResourceAt(1, 0, ResourceType.Berries, 20)               // 1 tile east
//          .Build();
//
//      // Actual world coordinates: sim.SpawnX, sim.SpawnY
//      sim.Tick(300);
//      Assert.True(sim.GetAgent("Alice").Hunger > 50);
// ─────────────────────────────────────────────────────────────────────────────

public class TestSimBuilder
{
    // ── Grid ──────────────────────────────────────────────────────────────────
    private int _width = 32, _height = 32, _seed = 1;

    // ── Agent specs ───────────────────────────────────────────────────────────
    private sealed class AgentSpec
    {
        public string Name        { get; set; } = "";
        public bool   IsMale      { get; set; } = true;
        public float  Hunger      { get; set; } = 80f;
        public int    Health      { get; set; } = 100;
        public int    Age         { get; set; } = -1;     // -1 → use ReproductionMinAge
        public (int Dx, int Dy)?  PositionOffset { get; set; }  // offset from spawn center
        public (int Dx, int Dy)?  HomeOffset     { get; set; }  // offset from spawn center
        public List<string>                   Knowledge  { get; } = new();
        public Dictionary<ResourceType, int>  Inventory  { get; } = new();
        public int ReproductionCooldown       { get; set; } = 0;
        public BehaviorMode? InitialMode     { get; set; }
    }

    // ── Tile specs ────────────────────────────────────────────────────────────
    private sealed class TileSpec
    {
        public int  Dx { get; set; }   // offset from spawn center
        public int  Dy { get; set; }
        public bool AddShelter { get; set; }
        public List<(ResourceType Type, int Amount)>  Resources   { get; } = new();
        public Dictionary<ResourceType, int>          HomeStorage { get; } = new();
    }

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly List<AgentSpec>                _agentList = new();
    private readonly Dictionary<string, AgentSpec>  _agentMap  = new(StringComparer.Ordinal);
    private readonly List<TileSpec>                 _tileSpecs = new();

    // ── Grid ──────────────────────────────────────────────────────────────────
    public TestSimBuilder GridSize(int w, int h) { _width = w; _height = h; return this; }
    public TestSimBuilder Seed(int seed)         { _seed  = seed;           return this; }

    // ── Agent ─────────────────────────────────────────────────────────────────
    public TestSimBuilder AddAgent(string name, bool isMale = true,
                                   float hunger = 80f, int health = 100)
    {
        var spec = new AgentSpec { Name = name, IsMale = isMale,
                                   Hunger = hunger, Health = health };
        _agentList.Add(spec);
        _agentMap[name] = spec;
        return this;
    }

    private AgentSpec Spec(string name)
    {
        if (!_agentMap.TryGetValue(name, out var s))
            throw new InvalidOperationException($"Unknown agent '{name}'. Call AddAgent first.");
        return s;
    }

    /// <summary>Position agent at spawn_center + (dx, dy).</summary>
    public TestSimBuilder AgentAt(string name, int dx, int dy)
        { Spec(name).PositionOffset = (dx, dy); return this; }

    public TestSimBuilder AgentAge(string name, int ageTicks)
        { Spec(name).Age = ageTicks; return this; }

    /// <summary>Set home tile at spawn_center + (dx, dy).</summary>
    public TestSimBuilder AgentHome(string name, int dx, int dy)
        { Spec(name).HomeOffset = (dx, dy); return this; }

    public TestSimBuilder AgentKnows(string name, params string[] recipes)
        { Spec(name).Knowledge.AddRange(recipes); return this; }

    public TestSimBuilder AgentInventory(string name, ResourceType type, int count)
        { Spec(name).Inventory[type] = count; return this; }

    public TestSimBuilder AgentHunger(string name, float hunger)
        { Spec(name).Hunger = hunger; return this; }

    public TestSimBuilder AgentHealth(string name, int health)
        { Spec(name).Health = health; return this; }

    public TestSimBuilder AgentCooldown(string name, int cooldown)
        { Spec(name).ReproductionCooldown = cooldown; return this; }

    public TestSimBuilder AgentMode(string name, BehaviorMode mode)
        { Spec(name).InitialMode = mode; return this; }

    // ── Tiles ─────────────────────────────────────────────────────────────────
    private TileSpec NewTileSpec(int dx, int dy)
    {
        var t = new TileSpec { Dx = dx, Dy = dy };
        _tileSpecs.Add(t);
        return t;
    }

    /// <summary>Add lean_to shelter at spawn_center + (dx, dy).</summary>
    public TestSimBuilder ShelterAt(int dx, int dy)
        { NewTileSpec(dx, dy).AddShelter = true; return this; }

    /// <summary>Add resource at spawn_center + (dx, dy).</summary>
    public TestSimBuilder ResourceAt(int dx, int dy, ResourceType type, int amount)
        { NewTileSpec(dx, dy).Resources.Add((type, amount)); return this; }

    /// <summary>Add home storage (+ implicit shelter) at spawn_center + (dx, dy).</summary>
    public TestSimBuilder HomeStorageAt(int dx, int dy, ResourceType type, int amount)
    {
        var t = NewTileSpec(dx, dy);
        t.AddShelter = true;
        t.HomeStorage[type] = amount;
        return this;
    }

    // ── Build ─────────────────────────────────────────────────────────────────
    public TestSim Build()
    {
        var world      = new World(_width, _height, _seed);
        var simulation = new Simulation(world, _seed);

        // ── Phase 1: Spawn all agents to discover the spawn center. ──────────
        // SpawnAgent() sets simulation.SpawnCenter on first call.
        var spawnedAgents = new List<(AgentSpec Spec, Agent Agent)>();
        foreach (var spec in _agentList)
        {
            var agent = simulation.SpawnAgent();
            spawnedAgents.Add((spec, agent));
        }

        // Spawn center is the position of the first agent before any teleport.
        int scX = spawnedAgents.Count > 0 ? spawnedAgents[0].Agent.X : _width  / 2;
        int scY = spawnedAgents.Count > 0 ? spawnedAgents[0].Agent.Y : _height / 2;

        // Helper: clamp and get tile with spawn-relative offset.
        Tile TileAt(int dx, int dy)
        {
            int tx = Math.Clamp(scX + dx, 0, _width  - 1);
            int ty = Math.Clamp(scY + dy, 0, _height - 1);
            return world.GetTile(tx, ty);
        }

        // ── Phase 2: Set up tiles using spawn-relative offsets. ──────────────
        foreach (var ts in _tileSpecs)
        {
            var tile = TileAt(ts.Dx, ts.Dy);
            if (ts.AddShelter && !tile.Structures.Contains("lean_to"))
                tile.Structures.Add("lean_to");
            foreach (var (type, amount) in ts.Resources)
                tile.Resources[type] = amount;
            foreach (var (type, amount) in ts.HomeStorage)
                tile.HomeFoodStorage[type] = amount;
        }

        // ── Phase 3: Configure each agent. ───────────────────────────────────
        foreach (var (spec, agent) in spawnedAgents)
        {
            agent.Name   = spec.Name;
            agent.IsMale = spec.IsMale;
            agent.Hunger = spec.Hunger;
            agent.Health = spec.Health;
            agent.Age    = spec.Age >= 0 ? spec.Age : SimConfig.ReproductionMinAge;
            agent.ReproductionCooldownRemaining = spec.ReproductionCooldown;

            // Teleport to spawn_center + offset if a position was requested.
            if (spec.PositionOffset.HasValue)
            {
                int tx = Math.Clamp(scX + spec.PositionOffset.Value.Dx, 0, _width  - 1);
                int ty = Math.Clamp(scY + spec.PositionOffset.Value.Dy, 0, _height - 1);
                world.RemoveAgentFromIndex(agent);
                agent.X = tx;
                agent.Y = ty;
                world.AddAgentToIndex(agent);
            }

            // Set HomeTile using spawn-relative offset.
            if (spec.HomeOffset.HasValue)
            {
                int hx = Math.Clamp(scX + spec.HomeOffset.Value.Dx, 0, _width  - 1);
                int hy = Math.Clamp(scY + spec.HomeOffset.Value.Dy, 0, _height - 1);
                agent.HomeTile = (hx, hy);
            }

            foreach (var recipe in spec.Knowledge)
                agent.LearnDiscovery(recipe);

            foreach (var (type, count) in spec.Inventory)
                agent.Inventory[type] = count;

            if (spec.InitialMode.HasValue)
                agent.TransitionMode(spec.InitialMode.Value, 0);
        }

        return new TestSim(simulation, world, _width, _height, scX, scY);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  TestSim — wrapper around a built test simulation with assertion helpers.
// ─────────────────────────────────────────────────────────────────────────────
public class TestSim
{
    public Simulation Simulation { get; }
    public World      World      { get; }

    /// <summary>Actual world X coordinate of the spawn center.</summary>
    public int SpawnX { get; }
    /// <summary>Actual world Y coordinate of the spawn center.</summary>
    public int SpawnY { get; }

    private readonly int _width, _height;

    public TestSim(Simulation simulation, World world,
                   int width, int height, int spawnX, int spawnY)
    {
        Simulation = simulation;
        World      = world;
        _width     = width;
        _height    = height;
        SpawnX     = spawnX;
        SpawnY     = spawnY;
    }

    // ── Running ───────────────────────────────────────────────────────────────

    /// <summary>Run N ticks.</summary>
    public void Tick(int count = 1)
    {
        for (int i = 0; i < count; i++)
            Simulation.Tick();
    }

    /// <summary>
    /// Run ticks one at a time, checking predicate after each tick.
    /// Returns true if predicate was satisfied within maxTicks.
    /// </summary>
    public bool TickUntil(Func<bool> predicate, int maxTicks)
    {
        for (int i = 0; i < maxTicks; i++)
        {
            Simulation.Tick();
            if (predicate()) return true;
        }
        return false;
    }

    /// <summary>
    /// Run ticks recording agent positions at a given sample rate.
    /// Returns dict of agentName → list of (X, Y) samples in absolute world coords.
    /// </summary>
    public Dictionary<string, List<(int X, int Y)>> SamplePositions(
        int totalTicks, int sampleEveryNTicks = 10)
    {
        var result = new Dictionary<string, List<(int X, int Y)>>();
        foreach (var a in Simulation.Agents)
            result[a.Name] = new List<(int, int)>();

        for (int t = 0; t < totalTicks; t++)
        {
            Simulation.Tick();
            if ((t + 1) % sampleEveryNTicks == 0)
            {
                foreach (var a in Simulation.Agents.Where(ag => ag.IsAlive))
                {
                    if (result.ContainsKey(a.Name))
                        result[a.Name].Add((a.X, a.Y));
                }
            }
        }

        return result;
    }

    // ── Coordinate helpers ────────────────────────────────────────────────────

    /// <summary>Convert a spawn-relative offset to absolute world coordinates.</summary>
    public (int X, int Y) WorldPos(int dx, int dy) =>
        (Math.Clamp(SpawnX + dx, 0, _width - 1),
         Math.Clamp(SpawnY + dy, 0, _height - 1));

    // ── Agent accessors ───────────────────────────────────────────────────────

    public Agent GetAgent(string name) =>
        Simulation.Agents.First(a => a.Name == name);

    public List<Agent> AliveAgents => Simulation.Agents.Where(a => a.IsAlive).ToList();

    public int LiveAgentCount => Simulation.Agents.Count(a => a.IsAlive);

    // ── Tile accessors ────────────────────────────────────────────────────────

    public Tile TileAt(int worldX, int worldY) => World.GetTile(worldX, worldY);

    public int CountSheltersInWorld()
    {
        int count = 0;
        for (int x = 0; x < _width;  x++)
        for (int y = 0; y < _height; y++)
            if (World.GetTile(x, y).HasShelter) count++;
        return count;
    }

    // ── Knowledge helpers ─────────────────────────────────────────────────────

    public bool AllAliveAgentsKnow(string recipe) =>
        Simulation.Agents.Where(a => a.IsAlive).All(a => a.Knowledge.Contains(recipe));

    // ── Discovery trigger ─────────────────────────────────────────────────────

    /// <summary>
    /// Trigger a discovery as if the named agent found it organically.
    /// Adds the recipe to the agent's knowledge AND notifies the settlement
    /// knowledge system so founding-group propagation begins.
    /// </summary>
    public void TriggerDiscovery(string agentName, string recipeId)
    {
        var agent = GetAgent(agentName);
        agent.LearnDiscovery(recipeId);
        Simulation.SettlementKnowledgeSystem.OnDiscovery(
            agent, recipeId,
            Simulation.Settlements, Simulation.Agents,
            Simulation.EventBus, Simulation.CurrentTick);
    }

    // ── Relationship helpers ────────────────────────────────────────────────────

    /// <summary>Set up parent-child relationship between named agents.</summary>
    public void SetParentChild(string parentName, string childName)
    {
        var parent = GetAgent(parentName);
        var child  = GetAgent(childName);
        parent.Relationships[child.Id] = RelationshipType.Child;
        child.Relationships[parent.Id] = RelationshipType.Parent;
    }

    /// <summary>Set up spouse relationship between named agents.</summary>
    public void SetSpouses(string name1, string name2)
    {
        var a = GetAgent(name1);
        var b = GetAgent(name2);
        a.Relationships[b.Id] = RelationshipType.Spouse;
        b.Relationships[a.Id] = RelationshipType.Spouse;
    }

    // ── Distance helper ───────────────────────────────────────────────────────

    public static int ManhattanDistance(int x1, int y1, int x2, int y2)
        => Math.Abs(x1 - x2) + Math.Abs(y1 - y2);
}
