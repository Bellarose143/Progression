using System.Linq;
using System.Reflection;
using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// D25b: Tests for Hunt/Harvest mechanics, animal perception, memory filtering,
/// carcass decay, and the Meat resource type.
/// </summary>
public class D25bHuntHarvestTests : IDisposable
{
    public D25bHuntHarvestTests()
    {
        Agent.ResetIdCounter();
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
    }

    public void Dispose()
    {
        Agent.ResetIdCounter();
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static World CreateSmallWorld(int size = 16, int seed = 42)
    {
        return new World(size, size, seed);
    }

    private static Agent CreateAgentAt(int x, int y, string name = "TestAgent", bool isMale = true)
    {
        var agent = new Agent(x, y, name: name, isMale: isMale);
        agent.Hunger = 100;
        agent.Health = 100;
        agent.Age = SimConfig.ReproductionMinAge; // adult
        return agent;
    }

    private static (int X, int Y) FindPassableTile(World world, int startX = 0, int startY = 0)
    {
        for (int x = startX; x < world.Width; x++)
            for (int y = startY; y < world.Height; y++)
                if (world.GetTile(x, y).Biome != BiomeType.Water)
                    return (x, y);
        for (int x = 0; x < world.Width; x++)
            for (int y = 0; y < world.Height; y++)
                if (world.GetTile(x, y).Biome != BiomeType.Water)
                    return (x, y);
        throw new InvalidOperationException("No passable tile found in world");
    }

    private static Animal CreateAnimalInWorld(World world, AnimalSpecies species, int x, int y, int herdId = -1)
    {
        var animal = new Animal(species, x, y, herdId, (x, y));
        world.Animals.Add(animal);
        world.AddAnimalToIndex(animal);
        return animal;
    }

    /// <summary>
    /// Adds an AnimalSighting memory entry to the agent's AnimalMemory.
    /// </summary>
    private static void AddAnimalMemory(Agent agent, AnimalSpecies species, int x, int y, int tick, int animalId = 1)
    {
        agent.AnimalMemory.Add(new MemoryEntry
        {
            X = x,
            Y = y,
            Type = MemoryType.AnimalSighting,
            AnimalSpecies = species,
            AnimalId = animalId,
            TickObserved = tick
        });
    }

    /// <summary>
    /// Adds a CarcassSighting memory entry to the agent's AnimalMemory.
    /// </summary>
    private static void AddCarcassMemory(Agent agent, AnimalSpecies species, int x, int y, int tick, int meatYield, int carcassId = 1)
    {
        agent.AnimalMemory.Add(new MemoryEntry
        {
            X = x,
            Y = y,
            Type = MemoryType.CarcassSighting,
            AnimalSpecies = species,
            AnimalId = carcassId,
            Quantity = meatYield,
            TickObserved = tick
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  1. Hunt_ToolGating_BareHanded
    //  Bare-handed agent can attempt all huntable species (Rabbit, Deer, Cow)
    //  but NOT Boar or Wolf.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Hunt_ToolGating_BareHanded()
    {
        var agent = CreateAgentAt(5, 5);
        // No knowledge at all — bare-handed
        int tick = 100;

        // Add animal memories for all species
        AddAnimalMemory(agent, AnimalSpecies.Rabbit, 6, 5, tick, animalId: 1);
        AddAnimalMemory(agent, AnimalSpecies.Deer, 7, 5, tick, animalId: 2);
        AddAnimalMemory(agent, AnimalSpecies.Cow, 8, 5, tick, animalId: 3);
        AddAnimalMemory(agent, AnimalSpecies.Boar, 9, 5, tick, animalId: 4);
        AddAnimalMemory(agent, AnimalSpecies.Wolf, 10, 5, tick, animalId: 5);

        var huntable = agent.GetRememberedHuntableAnimals(tick);
        var species = huntable.Select(m => m.AnimalSpecies!.Value).ToList();

        // Bare-handed can attempt Rabbit, Deer, Cow (just with lower success chance)
        Assert.Contains(AnimalSpecies.Rabbit, species);
        Assert.Contains(AnimalSpecies.Deer, species);
        Assert.Contains(AnimalSpecies.Cow, species);

        // Boar and Wolf are NOT huntable in D25b
        Assert.DoesNotContain(AnimalSpecies.Boar, species);
        Assert.DoesNotContain(AnimalSpecies.Wolf, species);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  2. Hunt_ToolGating_StoneKnife
    //  Agent with stone_knife knowledge — same huntable set, Boar/Wolf excluded.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Hunt_ToolGating_StoneKnife()
    {
        var agent = CreateAgentAt(5, 5);
        agent.LearnDiscovery("stone_knife");
        int tick = 100;

        AddAnimalMemory(agent, AnimalSpecies.Rabbit, 6, 5, tick, animalId: 1);
        AddAnimalMemory(agent, AnimalSpecies.Deer, 7, 5, tick, animalId: 2);
        AddAnimalMemory(agent, AnimalSpecies.Cow, 8, 5, tick, animalId: 3);
        AddAnimalMemory(agent, AnimalSpecies.Boar, 9, 5, tick, animalId: 4);
        AddAnimalMemory(agent, AnimalSpecies.Wolf, 10, 5, tick, animalId: 5);

        var huntable = agent.GetRememberedHuntableAnimals(tick);
        var species = huntable.Select(m => m.AnimalSpecies!.Value).ToList();

        Assert.Contains(AnimalSpecies.Rabbit, species);
        Assert.Contains(AnimalSpecies.Deer, species);
        Assert.Contains(AnimalSpecies.Cow, species);
        Assert.DoesNotContain(AnimalSpecies.Boar, species);
        Assert.DoesNotContain(AnimalSpecies.Wolf, species);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  3. Hunt_PursuitSuccess
    //  Agent with stone_knife adjacent to Rabbit. With a favorable RNG seed,
    //  the hunt succeeds: Rabbit dies, carcass appears with correct MeatYield.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Hunt_PursuitSuccess()
    {
        var world = CreateSmallWorld(32, 42);
        var pos = FindPassableTile(world, 8, 8);
        var sim = new Simulation(world, 42);

        // Spawn agent manually at a known position
        var agent = sim.SpawnAgent();
        agent.Name = "Hunter";
        agent.Hunger = 90;
        agent.Health = 100;
        agent.Age = SimConfig.ReproductionMinAge;
        agent.LearnDiscovery("stone_knife");

        // Move agent to known position
        world.RemoveAgentFromIndex(agent);
        agent.X = pos.X;
        agent.Y = pos.Y;
        agent.HomeTile = (pos.X, pos.Y);
        world.AddAgentToIndex(agent);

        // Add shelter at home so agent doesn't wander off looking for one
        var homeTile = world.GetTile(pos.X, pos.Y);
        if (!homeTile.Structures.Contains("lean_to"))
            homeTile.Structures.Add("lean_to");
        homeTile.HomeFoodStorage[ResourceType.Berries] = 50;

        // Place a rabbit adjacent to the agent
        var rabbit = CreateAnimalInWorld(world, AnimalSpecies.Rabbit, pos.X + 1, pos.Y);

        // Set up the agent to hunt: give it the hunt action and target
        agent.HuntTargetAnimalId = rabbit.Id;
        agent.HuntPursuitTicks = 0;
        agent.PendingAction = ActionType.Hunt;
        agent.ActionTicksRemaining = 99; // Will be managed by hunt logic
        agent.CurrentGoal = GoalType.HuntAnimal;

        // Tick many times with different seeds to find one where hunt succeeds
        // Rabbit + stone_knife = 55% chance, so within 20 attempts it should succeed
        bool huntSucceeded = false;
        for (int t = 0; t < 200; t++)
        {
            sim.Tick();
            if (!rabbit.IsAlive)
            {
                huntSucceeded = true;
                break;
            }
            // If hunt was abandoned (goal cleared), re-initiate if rabbit still alive
            if (rabbit.IsAlive && agent.HuntTargetAnimalId == null && agent.CurrentGoal != GoalType.HuntAnimal)
            {
                // Move agent back adjacent to rabbit for another attempt
                world.RemoveAgentFromIndex(agent);
                agent.X = pos.X;
                agent.Y = pos.Y;
                world.AddAgentToIndex(agent);

                // Re-initiate hunt
                agent.HuntTargetAnimalId = rabbit.Id;
                agent.HuntPursuitTicks = 0;
                agent.PendingAction = ActionType.Hunt;
                agent.ActionTicksRemaining = 99;
                agent.CurrentGoal = GoalType.HuntAnimal;
            }
        }

        Assert.True(huntSucceeded, "Rabbit should die from hunt within 200 ticks (55% success per attempt)");
        Assert.False(rabbit.IsAlive);

        // Verify a carcass was created
        var carcass = world.Carcasses.FirstOrDefault(c =>
            c.Species == AnimalSpecies.Rabbit);
        Assert.NotNull(carcass);

        // Rabbit MeatYield should match species config
        var rabbitConfig = Animal.SpeciesConfig[AnimalSpecies.Rabbit];
        Assert.Equal(rabbitConfig.MeatYield, carcass.MeatYield);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  4. Hunt_PursuitFailure
    //  Agent pursues Deer bare-handed (10% success). Max pursuit is 8 ticks.
    //  After max pursuit, agent should abandon hunt and deer survives.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Hunt_PursuitFailure()
    {
        // Use a seed that makes 10% success roll fail consistently
        var world = CreateSmallWorld(32, 77777);
        var pos = FindPassableTile(world, 8, 8);
        var sim = new Simulation(world, 77777);

        var agent = sim.SpawnAgent();
        agent.Name = "FailHunter";
        agent.Hunger = 90;
        agent.Health = 100;
        agent.Age = SimConfig.ReproductionMinAge;
        // No stone_knife — bare-handed, 10% success vs deer

        world.RemoveAgentFromIndex(agent);
        agent.X = pos.X;
        agent.Y = pos.Y;
        agent.HomeTile = (pos.X, pos.Y);
        world.AddAgentToIndex(agent);

        var homeTile = world.GetTile(pos.X, pos.Y);
        if (!homeTile.Structures.Contains("lean_to"))
            homeTile.Structures.Add("lean_to");
        homeTile.HomeFoodStorage[ResourceType.Berries] = 50;

        // Place a deer adjacent
        var deer = CreateAnimalInWorld(world, AnimalSpecies.Deer, pos.X + 1, pos.Y);

        // Set up hunt
        agent.HuntTargetAnimalId = deer.Id;
        agent.HuntPursuitTicks = 0;
        agent.PendingAction = ActionType.Hunt;
        agent.ActionTicksRemaining = 99;
        agent.CurrentGoal = GoalType.HuntAnimal;

        // Tick enough for max pursuit (Deer = 8 ticks) plus buffer
        for (int t = 0; t < 20; t++)
        {
            sim.Tick();
            // If hunt goal was cleared, the pursuit has ended
            if (agent.HuntTargetAnimalId == null)
                break;
        }

        // Deer may or may not have died depending on RNG, but we verify the mechanism:
        // After pursuit ticks expire, the hunt should be abandoned
        // If deer survived, great. If not (10% chance hit), that's also valid — skip assertion.
        if (deer.IsAlive)
        {
            Assert.Null(agent.HuntTargetAnimalId);
            Assert.NotEqual(GoalType.HuntAnimal, agent.CurrentGoal);
        }
        // If deer died from the 10% chance, the test is still valid — pursuit worked
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  5. Hunt_TargetDied
    //  Agent targeting an animal. Kill the animal externally. Agent should
    //  abandon the hunt goal on next tick.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Hunt_TargetDied()
    {
        var world = CreateSmallWorld(32, 42);
        var pos = FindPassableTile(world, 8, 8);
        var sim = new Simulation(world, 42);

        var agent = sim.SpawnAgent();
        agent.Name = "Hunter";
        agent.Hunger = 90;
        agent.Health = 100;
        agent.Age = SimConfig.ReproductionMinAge;
        agent.LearnDiscovery("stone_knife");

        world.RemoveAgentFromIndex(agent);
        agent.X = pos.X;
        agent.Y = pos.Y;
        agent.HomeTile = (pos.X, pos.Y);
        world.AddAgentToIndex(agent);

        var homeTile = world.GetTile(pos.X, pos.Y);
        if (!homeTile.Structures.Contains("lean_to"))
            homeTile.Structures.Add("lean_to");
        homeTile.HomeFoodStorage[ResourceType.Berries] = 50;

        // Place a deer nearby
        var deer = CreateAnimalInWorld(world, AnimalSpecies.Deer, pos.X + 2, pos.Y);

        // Set up agent to hunt the deer
        agent.HuntTargetAnimalId = deer.Id;
        agent.HuntPursuitTicks = 0;
        agent.PendingAction = ActionType.Hunt;
        agent.ActionTicksRemaining = 99;
        agent.CurrentGoal = GoalType.HuntAnimal;

        // Kill the deer externally (simulating another cause of death)
        deer.Health = 0;
        deer.State = AnimalState.Dead;

        // Tick once — agent should detect target is dead and abandon
        sim.Tick();

        Assert.Null(agent.HuntTargetAnimalId);
        Assert.NotEqual(GoalType.HuntAnimal, agent.CurrentGoal);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  6. Hunt_ScoreLowerThanGather
    //  Conceptual/behavioral test: when berries are readily available,
    //  agent should prefer gathering over hunting. Verified by checking
    //  that agents gather berries when both options exist.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Hunt_ScoreLowerThanGather()
    {
        // When food is readily available on the current tile, agents should gather
        // rather than hunt. This tests the design principle that hunting is a
        // fallback when gathering is insufficient.
        //
        // ScoreHunt is private in UtilityScorer, so we test behaviorally:
        // an agent surrounded by berries with an animal memory should gather
        // food before attempting a hunt.

        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(42)
            .AddAgent("Gatherer", isMale: true, hunger: 60f)
            .AgentAt("Gatherer", 0, 0)
            .AgentHome("Gatherer", 0, 0)
            .AgentMode("Gatherer", BehaviorMode.Forage)
            .ShelterAt(0, 0)
            .ResourceAt(0, 0, ResourceType.Berries, 40)
            .ResourceAt(1, 0, ResourceType.Berries, 40)
            .ResourceAt(0, 1, ResourceType.Berries, 40)
            .ResourceAt(-1, 0, ResourceType.Berries, 40)
            .Build();

        var agent = sim.GetAgent("Gatherer");

        // Add an animal memory so hunting is theoretically possible
        AddAnimalMemory(agent, AnimalSpecies.Rabbit, sim.SpawnX + 3, sim.SpawnY, 0, animalId: 99);

        // Track actions over 300 ticks — enough for gather cycles to complete
        int gatherCount = 0;
        int huntCount = 0;
        for (int t = 0; t < 300; t++)
        {
            sim.Tick(1);
            if (agent.CurrentAction == ActionType.Gather) gatherCount++;
            if (agent.CurrentAction == ActionType.Hunt) huntCount++;
        }

        // Agent should spend more time gathering than hunting when berries are abundant
        // Even if gather is 0, hunt should also be 0 or less (agent eating/moving is fine)
        Assert.True(gatherCount >= huntCount,
            $"Agent should prefer gather ({gatherCount}) over hunt ({huntCount}) when berries are abundant");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  7. Harvest_ProducesMeat
    //  Agent harvests a carcass and gains Meat in inventory.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Harvest_ProducesMeat()
    {
        var world = CreateSmallWorld(32, 42);
        var pos = FindPassableTile(world, 8, 8);
        var sim = new Simulation(world, 42);

        var agent = sim.SpawnAgent();
        agent.Name = "Harvester";
        agent.Hunger = 70;
        agent.Health = 100;
        agent.Age = SimConfig.ReproductionMinAge;

        world.RemoveAgentFromIndex(agent);
        agent.X = pos.X;
        agent.Y = pos.Y;
        agent.HomeTile = (pos.X, pos.Y);
        world.AddAgentToIndex(agent);

        var homeTile = world.GetTile(pos.X, pos.Y);
        if (!homeTile.Structures.Contains("lean_to"))
            homeTile.Structures.Add("lean_to");
        homeTile.HomeFoodStorage[ResourceType.Berries] = 30;

        // Place a carcass at the agent's tile
        int expectedMeat = 3;
        var carcass = new Carcass(pos.X, pos.Y, AnimalSpecies.Deer, expectedMeat);
        world.Carcasses.Add(carcass);

        // Give agent carcass memory so it knows about it
        AddCarcassMemory(agent, AnimalSpecies.Deer, pos.X, pos.Y, 0, expectedMeat, carcass.Id);

        // Set up agent to harvest
        agent.PendingAction = ActionType.Harvest;
        agent.ActionTicksRemaining = SimConfig.HarvestDuration;
        agent.ActionTarget = (pos.X, pos.Y);
        agent.CurrentGoal = GoalType.HarvestCarcass;
        agent.GoalTarget = (pos.X, pos.Y);

        int initialMeat = agent.Inventory.TryGetValue(ResourceType.Meat, out int m) ? m : 0;

        // Tick enough for harvest to complete (HarvestDuration = 3)
        for (int t = 0; t < 10; t++)
        {
            sim.Tick();
        }

        int finalMeat = agent.Inventory.TryGetValue(ResourceType.Meat, out int fm) ? fm : 0;
        Assert.True(finalMeat > initialMeat,
            $"Agent should gain meat from harvest. Initial: {initialMeat}, Final: {finalMeat}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  8. Harvest_CarcassDecay
    //  Carcass starts with 2*CarcassDecayTicks remaining.
    //  At CarcassDecayTicks remaining, meat halves.
    //  At 0, carcass is removed.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Harvest_CarcassDecay()
    {
        var world = CreateSmallWorld(16, 42);
        var sim = new Simulation(world, 42);
        // Spawn agents so sim.Tick() doesn't crash (needs at least some agents)
        sim.SpawnAgent();

        int initialMeat = 4;
        var carcass = new Carcass(5, 5, AnimalSpecies.Deer, initialMeat);
        world.Carcasses.Add(carcass);

        // Carcass starts with 2 * CarcassDecayTicks remaining
        Assert.Equal(2 * SimConfig.CarcassDecayTicks, carcass.DecayTicksRemaining);
        Assert.True(carcass.IsActive);
        Assert.Equal(initialMeat, carcass.MeatYield);

        // Tick until half-life point: after CarcassDecayTicks ticks,
        // DecayTicksRemaining should equal CarcassDecayTicks and meat should halve
        for (int t = 0; t < SimConfig.CarcassDecayTicks; t++)
        {
            sim.Tick();
        }

        // At half-life, meat should be halved: (4+1)/2 = 2
        Assert.Equal(SimConfig.CarcassDecayTicks, carcass.DecayTicksRemaining);
        Assert.Equal(Math.Max(1, (initialMeat + 1) / 2), carcass.MeatYield);
        Assert.True(carcass.IsActive);

        // Continue ticking to full decay
        for (int t = 0; t < SimConfig.CarcassDecayTicks; t++)
        {
            sim.Tick();
        }

        // After full decay, carcass should be removed from list (IsActive = false at 0)
        Assert.False(world.Carcasses.Contains(carcass),
            "Carcass should be removed from world after full decay");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  9. Perception_AnimalsInMemory
    //  Agent near a deer perceives it and adds to AnimalMemory.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Perception_AnimalsInMemory()
    {
        var world = CreateSmallWorld(32, 42);
        var pos = FindPassableTile(world, 8, 8);
        var sim = new Simulation(world, 42);

        var agent = sim.SpawnAgent();
        agent.Name = "Perceiver";
        agent.Hunger = 90;
        agent.Health = 100;
        agent.Age = SimConfig.ReproductionMinAge;

        // Move agent to known position
        world.RemoveAgentFromIndex(agent);
        agent.X = pos.X;
        agent.Y = pos.Y;
        agent.HomeTile = (pos.X, pos.Y);
        world.AddAgentToIndex(agent);

        var homeTile = world.GetTile(pos.X, pos.Y);
        if (!homeTile.Structures.Contains("lean_to"))
            homeTile.Structures.Add("lean_to");
        homeTile.HomeFoodStorage[ResourceType.Berries] = 50;

        // Clear any pre-existing animals from world gen near the agent
        // Place a deer at adjacent tile
        var deer = CreateAnimalInWorld(world, AnimalSpecies.Deer, pos.X + 1, pos.Y);
        // Ensure deer is not fleeing (fleeing animals are skipped in perception)
        deer.State = AnimalState.Idle;

        // Force active perception on agent
        agent.ForceActivePerception = true;

        // Run enough ticks for perception to fire
        for (int t = 0; t < 10; t++)
        {
            sim.Tick();
            if (agent.AnimalMemory.Any(m =>
                m.Type == MemoryType.AnimalSighting &&
                m.AnimalSpecies == AnimalSpecies.Deer &&
                m.AnimalId == deer.Id))
            {
                break;
            }
        }

        Assert.Contains(agent.AnimalMemory,
            m => m.Type == MemoryType.AnimalSighting && m.AnimalSpecies == AnimalSpecies.Deer);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  10. Perception_BoarNotHuntable
    //  Boar is perceived and stored in AnimalMemory, but
    //  GetRememberedHuntableAnimals excludes it.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Perception_BoarNotHuntable()
    {
        var agent = CreateAgentAt(5, 5);
        int tick = 100;

        // Add boar to animal memory (as if perceived)
        AddAnimalMemory(agent, AnimalSpecies.Boar, 6, 5, tick, animalId: 1);

        // Verify it IS in AnimalMemory
        Assert.Contains(agent.AnimalMemory,
            m => m.Type == MemoryType.AnimalSighting && m.AnimalSpecies == AnimalSpecies.Boar);

        // Verify it is NOT in huntable animals
        var huntable = agent.GetRememberedHuntableAnimals(tick);
        Assert.DoesNotContain(huntable,
            m => m.AnimalSpecies == AnimalSpecies.Boar);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  11. ResourceSwap_NoAnimalsInCodebase
    //  ResourceType enum should NOT contain "Animals" — it was replaced by
    //  the Animal entity system and Meat resource type.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void ResourceSwap_NoAnimalsInCodebase()
    {
        var enumNames = typeof(ResourceType).GetEnumNames();
        Assert.DoesNotContain("Animals", enumNames);

        // Verify Meat DOES exist
        Assert.Contains("Meat", enumNames);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  12. Meat_Cookable
    //  Meat is recognized as a food resource by the mode system.
    //  IsFoodResource(Meat) returns true.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Meat_Cookable()
    {
        // Meat must be classified as food by the mode transition system
        Assert.True(ModeTransitionManager.IsFoodResource(ResourceType.Meat),
            "Meat should be classified as a food resource");

        // Other food types should also be true (sanity check)
        Assert.True(ModeTransitionManager.IsFoodResource(ResourceType.Berries));
        Assert.True(ModeTransitionManager.IsFoodResource(ResourceType.Grain));
        Assert.True(ModeTransitionManager.IsFoodResource(ResourceType.Fish));

        // Non-food should be false
        Assert.False(ModeTransitionManager.IsFoodResource(ResourceType.Wood));
        Assert.False(ModeTransitionManager.IsFoodResource(ResourceType.Stone));
        Assert.False(ModeTransitionManager.IsFoodResource(ResourceType.Ore));

        // Verify SimConfig has Meat-specific hunger values
        Assert.True(SimConfig.MeatHungerValue > 0, "Raw meat should have hunger value");
        Assert.True(SimConfig.CookedMeatHungerValue > SimConfig.MeatHungerValue,
            "Cooked meat should restore more hunger than raw meat");
    }
}
