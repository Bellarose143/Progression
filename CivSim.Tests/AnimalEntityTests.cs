using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// D25a: Tests for Animal entity, Carcass, and AnimalAI state machine.
/// </summary>
public class AnimalEntityTests : IDisposable
{
    public AnimalEntityTests()
    {
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
    }

    public void Dispose()
    {
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static World CreateSmallWorld(int size = 16, int seed = 42)
    {
        return new World(size, size, seed);
    }

    /// <summary>
    /// Creates a minimal agent at the given position for flee tests.
    /// Uses a simple constructor approach — agents need X, Y, IsAlive at minimum.
    /// </summary>
    private static Agent CreateAgentAt(int x, int y)
    {
        var agent = new Agent(x, y, name: "TestAgent", isMale: true);
        agent.Hunger = 100;
        agent.Health = 100;
        return agent;
    }

    /// <summary>
    /// Finds a passable (non-water) tile position in the world for placing animals/agents.
    /// </summary>
    private static (int X, int Y) FindPassableTile(World world, int startX = 0, int startY = 0)
    {
        for (int x = startX; x < world.Width; x++)
            for (int y = startY; y < world.Height; y++)
                if (world.GetTile(x, y).Biome != BiomeType.Water)
                    return (x, y);
        // Fallback: scan from 0,0
        for (int x = 0; x < world.Width; x++)
            for (int y = 0; y < world.Height; y++)
                if (world.GetTile(x, y).Biome != BiomeType.Water)
                    return (x, y);
        throw new InvalidOperationException("No passable tile found in world");
    }

    /// <summary>
    /// Creates an animal at a known passable position and registers it in the world.
    /// </summary>
    private static Animal CreateAnimalInWorld(World world, AnimalSpecies species, int x, int y, int herdId = -1)
    {
        var animal = new Animal(species, x, y, herdId, (x, y));
        world.Animals.Add(animal);
        world.AddAnimalToIndex(animal);
        return animal;
    }

    /// <summary>
    /// Returns a tick value that is daytime (not night).
    /// NightStartHour=420, NightEndHour=100, TicksPerSimDay=480.
    /// So daytime is ticks 100..419 within a day cycle.
    /// </summary>
    private static int DayTick => 200; // well within daytime

    /// <summary>
    /// Returns a tick value that is nighttime.
    /// </summary>
    private static int NightTick => 420; // exactly NightStartHour = night

    // ═══════════════════════════════════════════════════════════════════════
    //  1. Animal ID Auto-Increment
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Animal_IdAutoIncrements()
    {
        Animal.ResetIdCounter();
        var a1 = new Animal(AnimalSpecies.Rabbit, 0, 0, -1, (0, 0));
        var a2 = new Animal(AnimalSpecies.Deer, 1, 1, -1, (1, 1));
        var a3 = new Animal(AnimalSpecies.Boar, 2, 2, -1, (2, 2));

        Assert.Equal(1, a1.Id);
        Assert.Equal(2, a2.Id);
        Assert.Equal(3, a3.Id);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  2. Species Config Coverage
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Animal_SpeciesConfig_AllSpeciesHaveConfig()
    {
        foreach (AnimalSpecies species in Enum.GetValues<AnimalSpecies>())
        {
            Assert.True(Animal.SpeciesConfig.ContainsKey(species),
                $"Missing config for species: {species}");
            var config = Animal.SpeciesConfig[species];
            Assert.True(config.MaxHealth > 0, $"{species} MaxHealth must be > 0");
            Assert.True(config.MoveSpeed >= 1, $"{species} MoveSpeed must be >= 1");
            Assert.True(config.DetectionRange > 0, $"{species} DetectionRange must be > 0");
            Assert.True(config.TerritoryRadius > 0, $"{species} TerritoryRadius must be > 0");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  3. Idle Transitions to Grazing or Moving
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Idle_TransitionsToGrazingOrMoving()
    {
        var world = CreateSmallWorld();
        var pos = FindPassableTile(world);
        var animal = CreateAnimalInWorld(world, AnimalSpecies.Deer, pos.X, pos.Y);
        animal.State = AnimalState.Idle;
        animal.TicksInState = 0;
        var rng = new Random(42);
        var agents = new List<Agent>(); // no agents to trigger flee

        // Run enough ticks for idle duration to expire (max 80 ticks)
        bool transitioned = false;
        for (int tick = DayTick; tick < DayTick + 200; tick++)
        {
            AnimalAI.UpdateAnimal(animal, world, tick, agents, rng);
            if (animal.State != AnimalState.Idle)
            {
                transitioned = true;
                break;
            }
        }

        Assert.True(transitioned, "Animal should transition out of Idle after duration expires");
        Assert.True(animal.State == AnimalState.Grazing || animal.State == AnimalState.Moving ||
                     animal.State == AnimalState.Idle, // could transition and come back
            $"Expected Grazing or Moving, got {animal.State}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  4. Grazing Transitions After Duration
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Grazing_TransitionsAfterDuration()
    {
        var world = CreateSmallWorld();
        var pos = FindPassableTile(world);
        var animal = CreateAnimalInWorld(world, AnimalSpecies.Deer, pos.X, pos.Y);
        animal.State = AnimalState.Grazing;
        animal.TicksInState = 0;
        var rng = new Random(42);
        var agents = new List<Agent>();

        // Grazing lasts 30-60 ticks. Run enough ticks.
        bool transitioned = false;
        for (int tick = DayTick; tick < DayTick + 200; tick++)
        {
            AnimalAI.UpdateAnimal(animal, world, tick, agents, rng);
            if (animal.State != AnimalState.Grazing)
            {
                transitioned = true;
                break;
            }
        }

        Assert.True(transitioned, "Animal should transition out of Grazing after duration expires");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  5. Moving Animal Changes Position
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Moving_AnimalChangesPosition()
    {
        var world = CreateSmallWorld();
        // Place animal in center of world so it has room to move
        var pos = FindPassableTile(world, 7, 7);
        var animal = CreateAnimalInWorld(world, AnimalSpecies.Deer, pos.X, pos.Y);
        animal.State = AnimalState.Moving;
        animal.TicksInState = 0;
        animal.TicksSinceLastMove = animal.MoveSpeed; // ready to move
        var rng = new Random(42);
        var agents = new List<Agent>();

        int origX = animal.X, origY = animal.Y;

        // Run multiple ticks to ensure a move happens (Moving transitions to Idle,
        // then Idle will eventually go to Moving again)
        bool moved = false;
        for (int tick = DayTick; tick < DayTick + 300; tick++)
        {
            AnimalAI.UpdateAnimal(animal, world, tick, agents, rng);
            if (animal.X != origX || animal.Y != origY)
            {
                moved = true;
                break;
            }
        }

        Assert.True(moved, "Animal should change position when Moving state processes a move");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  6. Moving Respects Territory
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Moving_RespectsTerritory()
    {
        var world = CreateSmallWorld();
        var pos = FindPassableTile(world, 8, 8);
        var animal = CreateAnimalInWorld(world, AnimalSpecies.Rabbit, pos.X, pos.Y);
        // Rabbit TerritoryRadius = 3
        var rng = new Random(42);
        var agents = new List<Agent>();

        // Run many ticks and verify animal stays within territory
        for (int tick = DayTick; tick < DayTick + 500; tick++)
        {
            AnimalAI.UpdateAnimal(animal, world, tick, agents, rng);
            int distFromCenter = Math.Max(
                Math.Abs(animal.X - animal.TerritoryCenter.X),
                Math.Abs(animal.Y - animal.TerritoryCenter.Y));
            // Allow TerritoryRadius + 1 tolerance for single-step overshoot before correction
            Assert.True(distFromCenter <= animal.TerritoryRadius + 1,
                $"Animal at ({animal.X},{animal.Y}) is {distFromCenter} tiles from territory center " +
                $"({animal.TerritoryCenter.X},{animal.TerritoryCenter.Y}), max allowed {animal.TerritoryRadius + 1}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  7. Fleeing Triggered by Nearby Agent
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Fleeing_TriggeredByNearbyAgent()
    {
        var world = CreateSmallWorld();
        var pos = FindPassableTile(world, 8, 8);
        var animal = CreateAnimalInWorld(world, AnimalSpecies.Deer, pos.X, pos.Y);
        // Deer DetectionRange = 5
        animal.State = AnimalState.Idle;

        // Place agent within detection range (2 tiles away)
        Agent.ResetIdCounter();
        var agent = CreateAgentAt(pos.X + 2, pos.Y);
        var agents = new List<Agent> { agent };
        var rng = new Random(42);

        AnimalAI.UpdateAnimal(animal, world, DayTick, agents, rng);

        Assert.Equal(AnimalState.Fleeing, animal.State);
        Assert.NotNull(animal.FleeTarget);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  8. Herd Flees Together
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Fleeing_HerdFleesTogether()
    {
        var world = CreateSmallWorld();
        var pos = FindPassableTile(world, 8, 8);

        // Create a herd of 3 deer with same herdId
        int herdId = 1;
        var deer1 = CreateAnimalInWorld(world, AnimalSpecies.Deer, pos.X, pos.Y, herdId);
        var deer2 = CreateAnimalInWorld(world, AnimalSpecies.Deer, pos.X + 1, pos.Y, herdId);
        var deer3 = CreateAnimalInWorld(world, AnimalSpecies.Deer, pos.X, pos.Y + 1, herdId);

        // Place agent near deer1
        Agent.ResetIdCounter();
        var agent = CreateAgentAt(pos.X + 2, pos.Y);
        var agents = new List<Agent> { agent };
        var rng = new Random(42);

        // Update deer1 — it should trigger herd flee
        AnimalAI.UpdateAnimal(deer1, world, DayTick, agents, rng);

        Assert.Equal(AnimalState.Fleeing, deer1.State);
        Assert.Equal(AnimalState.Fleeing, deer2.State);
        Assert.Equal(AnimalState.Fleeing, deer3.State);
        Assert.NotNull(deer2.FleeTarget);
        Assert.NotNull(deer3.FleeTarget);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  9. Boar Does Not Flee
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Boar_DoesNotFlee()
    {
        var world = CreateSmallWorld();
        var pos = FindPassableTile(world, 8, 8);
        var boar = CreateAnimalInWorld(world, AnimalSpecies.Boar, pos.X, pos.Y);
        boar.State = AnimalState.Idle;

        // Place agent right next to boar (1 tile away, well within detection range)
        Agent.ResetIdCounter();
        var agent = CreateAgentAt(pos.X + 1, pos.Y);
        var agents = new List<Agent> { agent };
        var rng = new Random(42);

        AnimalAI.UpdateAnimal(boar, world, DayTick, agents, rng);

        Assert.NotEqual(AnimalState.Fleeing, boar.State);
        Assert.Null(boar.FleeTarget);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  10. Night Transitions to Sleeping
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Night_TransitionsToSleeping()
    {
        var world = CreateSmallWorld();
        var pos = FindPassableTile(world);
        var animal = CreateAnimalInWorld(world, AnimalSpecies.Deer, pos.X, pos.Y);
        animal.State = AnimalState.Idle;
        animal.TicksInState = 0;
        var rng = new Random(42);
        var agents = new List<Agent>();

        // Use a night tick
        AnimalAI.UpdateAnimal(animal, world, NightTick, agents, rng);

        Assert.Equal(AnimalState.Sleeping, animal.State);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  11. Sleeping Wakes at Dawn
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Sleeping_WakesAtDawn()
    {
        var world = CreateSmallWorld();
        var pos = FindPassableTile(world);
        var animal = CreateAnimalInWorld(world, AnimalSpecies.Deer, pos.X, pos.Y);
        animal.State = AnimalState.Sleeping;
        animal.TicksInState = 50;
        var agents = new List<Agent>();

        // Use a daytime tick (dawn)
        AnimalAI.UpdateAnimal(animal, world, DayTick, agents, new Random(42));

        Assert.Equal(AnimalState.Idle, animal.State);
        Assert.Equal(0, animal.TicksInState);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  12. Herd Following — Followers Track Leader
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void HerdFollowing_FollowersTrackLeader()
    {
        var world = CreateSmallWorld();
        var pos = FindPassableTile(world, 4, 4);
        // Use a high herd ID that won't collide with world gen herds
        int herdId = 9999;

        // Leader gets lowest ID (herd leader = lowest alive ID in herd)
        var leader = CreateAnimalInWorld(world, AnimalSpecies.Deer, pos.X, pos.Y, herdId);

        // Follower 5 tiles away with large territory centered between them
        var followerPos = FindPassableTile(world, pos.X + 5, pos.Y);
        var follower = new Animal(AnimalSpecies.Deer, followerPos.X, followerPos.Y, herdId, (pos.X + 2, pos.Y));
        world.Animals.Add(follower);
        world.AddAnimalToIndex(follower);

        var agents = new List<Agent>();
        var rng = new Random(42);

        int initialDist = Math.Max(Math.Abs(follower.X - leader.X), Math.Abs(follower.Y - leader.Y));

        // Force follower into Moving state with enough ticks to move
        follower.State = AnimalState.Moving;
        follower.TicksSinceLastMove = follower.MoveSpeed;

        AnimalAI.UpdateAnimal(follower, world, DayTick, agents, rng);

        int newDist = Math.Max(Math.Abs(follower.X - leader.X), Math.Abs(follower.Y - leader.Y));
        Assert.True(newDist <= initialDist,
            $"Follower should move toward leader. Initial dist={initialDist}, new dist={newDist}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  13. Territory Redirects Toward Center
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Territory_RedirectsTowardCenter()
    {
        var world = CreateSmallWorld();
        var center = FindPassableTile(world, 8, 8);

        // Create animal at territory edge (TerritoryRadius away from center)
        // Rabbit has TerritoryRadius = 3
        var animal = new Animal(AnimalSpecies.Rabbit, center.X + 3, center.Y, -1, (center.X, center.Y));
        world.Animals.Add(animal);
        world.AddAnimalToIndex(animal);

        var agents = new List<Agent>();
        // Use a fixed seed that would normally push the animal further from center
        var rng = new Random(99);

        animal.State = AnimalState.Moving;
        animal.TicksSinceLastMove = animal.MoveSpeed;

        int startX = animal.X;

        // Run several move cycles
        for (int tick = DayTick; tick < DayTick + 100; tick++)
        {
            AnimalAI.UpdateAnimal(animal, world, tick, agents, rng);
        }

        // After many ticks, animal should still be within territory bounds (or close)
        int distFromCenter = Math.Max(
            Math.Abs(animal.X - animal.TerritoryCenter.X),
            Math.Abs(animal.Y - animal.TerritoryCenter.Y));
        Assert.True(distFromCenter <= animal.TerritoryRadius + 1,
            $"Animal should stay near territory. Distance from center: {distFromCenter}, " +
            $"radius: {animal.TerritoryRadius}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  14. Carcass Decay Tracking
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Carcass_DecayTracking()
    {
        var carcass = new Carcass(5, 5, AnimalSpecies.Deer, 3);

        Assert.True(carcass.IsActive);
        // D25b: Carcass lasts 2 * CarcassDecayTicks (40 ticks total)
        Assert.Equal(2 * SimConfig.CarcassDecayTicks, carcass.DecayTicksRemaining);
        Assert.Equal(3, carcass.MeatYield);

        // Simulate decay to half-life
        for (int i = 0; i < SimConfig.CarcassDecayTicks; i++)
        {
            Assert.True(carcass.IsActive, $"Carcass should be active at tick {i}");
            carcass.DecayTicksRemaining--;
        }
        // At half-life, still active but meat should be reduced (handled by Simulation tick)
        Assert.True(carcass.IsActive);
        Assert.Equal(SimConfig.CarcassDecayTicks, carcass.DecayTicksRemaining);

        // Continue to full decay
        for (int i = 0; i < SimConfig.CarcassDecayTicks; i++)
        {
            Assert.True(carcass.IsActive, $"Carcass should be active at tick {SimConfig.CarcassDecayTicks + i}");
            carcass.DecayTicksRemaining--;
        }

        Assert.False(carcass.IsActive);
        Assert.Equal(0, carcass.DecayTicksRemaining);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  15. FleeSlow — Cow Moves Slower While Fleeing
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void FleeSlow_CowMovesSlower()
    {
        var world = CreateSmallWorld();
        var pos = FindPassableTile(world, 8, 8);
        var cow = CreateAnimalInWorld(world, AnimalSpecies.Cow, pos.X, pos.Y);
        cow.State = AnimalState.Fleeing;
        cow.FleeTarget = (pos.X - 5, pos.Y); // flee target to the left
        cow.TicksSinceLastMove = 0; // just moved

        // Place agent nearby to keep flee active
        Agent.ResetIdCounter();
        var agent = CreateAgentAt(pos.X + 1, pos.Y);
        var agents = new List<Agent> { agent };
        var rng = new Random(42);

        int startX = cow.X;

        // Cow MoveSpeed is 3, so UpdateFleeing gates on TicksSinceLastMove < MoveSpeed (i.e. < 3).
        // Tick 1: TicksSinceLastMove incremented to 1 (< 3), should NOT move
        AnimalAI.UpdateAnimal(cow, world, DayTick, agents, rng);
        Assert.Equal(startX, cow.X);

        // Tick 2: TicksSinceLastMove incremented to 2 (< 3), still should NOT move
        AnimalAI.UpdateAnimal(cow, world, DayTick + 1, agents, rng);
        Assert.Equal(startX, cow.X);

        // Tick 3: TicksSinceLastMove incremented to 3 (>= 3), should now attempt to move
        AnimalAI.UpdateAnimal(cow, world, DayTick + 2, agents, rng);

        // The cow should have moved (if flee target tile is passable and not water)
        // We verify by checking that either it moved OR the target tile was impassable
        bool movedOrBlocked = cow.X != startX ||
            cow.Y != pos.Y ||
            (world.IsInBounds(pos.X - 1, pos.Y) && world.GetTile(pos.X - 1, pos.Y).Biome == BiomeType.Water);
        Assert.True(movedOrBlocked || cow.State == AnimalState.Idle,
            "Cow should attempt to move after 3 ticks (MoveSpeed=3) or transition to Idle if no agent nearby");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Additional edge case tests
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public void Dead_Animal_IsNotUpdated()
    {
        var world = CreateSmallWorld();
        var pos = FindPassableTile(world);
        var animal = CreateAnimalInWorld(world, AnimalSpecies.Deer, pos.X, pos.Y);
        animal.Die();

        int origTicks = animal.TicksInState;
        var rng = new Random(42);
        var agents = new List<Agent>();

        AnimalAI.UpdateAnimal(animal, world, DayTick, agents, rng);

        Assert.Equal(origTicks, animal.TicksInState);
        Assert.Equal(AnimalState.Dead, animal.State);
    }

    [Fact]
    public void Fleeing_StopsWhenNoAgentNearby()
    {
        var world = CreateSmallWorld();
        var pos = FindPassableTile(world, 8, 8);
        var animal = CreateAnimalInWorld(world, AnimalSpecies.Deer, pos.X, pos.Y);
        animal.State = AnimalState.Fleeing;
        animal.FleeTarget = (pos.X + 3, pos.Y);
        animal.TicksSinceLastMove = 1;

        // No agents at all — should stop fleeing
        var agents = new List<Agent>();
        var rng = new Random(42);

        AnimalAI.UpdateAnimal(animal, world, DayTick, agents, rng);

        Assert.Equal(AnimalState.Idle, animal.State);
        Assert.Null(animal.FleeTarget);
    }

    [Fact]
    public void Fleeing_WithoutFleeTarget_TransitionsToIdle()
    {
        var world = CreateSmallWorld();
        var pos = FindPassableTile(world);
        var animal = CreateAnimalInWorld(world, AnimalSpecies.Deer, pos.X, pos.Y);
        animal.State = AnimalState.Fleeing;
        animal.FleeTarget = null; // no target set

        var agents = new List<Agent>();
        var rng = new Random(42);

        AnimalAI.UpdateAnimal(animal, world, DayTick, agents, rng);

        Assert.Equal(AnimalState.Idle, animal.State);
    }

    [Fact]
    public void Animal_Die_SetsDeadState()
    {
        var animal = new Animal(AnimalSpecies.Deer, 5, 5, -1, (5, 5));

        Assert.True(animal.IsAlive);

        animal.Die();

        Assert.False(animal.IsAlive);
        Assert.Equal(AnimalState.Dead, animal.State);
        Assert.Equal(0, animal.Health);
    }

    [Fact]
    public void Moving_AnimalAtMapEdge_DoesNotGetStuck()
    {
        Animal.ResetIdCounter();
        var world = CreateSmallWorld(16); // 16x16 map

        // Place animal at corner with territory center nearby but inside bounds
        int herdId = 9998;
        var animal = new Animal(AnimalSpecies.Deer, 14, 14, herdId, (10, 10));
        world.Animals.Add(animal);
        world.AddAnimalToIndex(animal);

        var agents = new List<Agent>();
        var rng = new Random(42);

        // Run 200 ticks of movement — animal should not stay at (15,15) the entire time
        int moveCount = 0;
        for (int tick = 0; tick < 200; tick++)
        {
            int prevX = animal.X, prevY = animal.Y;
            animal.State = AnimalState.Moving;
            animal.TicksSinceLastMove = animal.MoveSpeed; // Ready to move
            AnimalAI.UpdateAnimal(animal, world, DayTick + tick, agents, rng);
            if (animal.X != prevX || animal.Y != prevY)
                moveCount++;
        }

        Assert.True(moveCount > 0,
            $"Animal at map edge should move at least once in 200 ticks, but stayed at ({animal.X},{animal.Y})");
    }

    [Fact]
    public void Carcass_IdAutoIncrements()
    {
        var c1 = new Carcass(0, 0, AnimalSpecies.Rabbit, 1);
        var c2 = new Carcass(1, 1, AnimalSpecies.Deer, 3);
        var c3 = new Carcass(2, 2, AnimalSpecies.Cow, 4);

        Assert.Equal(1, c1.Id);
        Assert.Equal(2, c2.Id);
        Assert.Equal(3, c3.Id);
    }
}
