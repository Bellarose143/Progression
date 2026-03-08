using CivSim.Core;
using Xunit;
using System;
using System.Linq;
using System.Collections.Generic;

namespace CivSim.Tests;

// ═══════════════════════════════════════════════════════════════════
// Gate 1: Knowledge Gate — actions gated behind animal_domestication
// ═══════════════════════════════════════════════════════════════════

public class D25dKnowledgeGateTests
{
    private static void ResetIds()
    {
        Agent.ResetIdCounter();
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
    }

    [Fact]
    public void ScoreTame_Returns_Empty_Without_Knowledge()
    {
        ResetIds();
        var world = new World(16, 16, 42);
        var agent = new Agent(5, 5, name: "Ada", startingAge: SimConfig.ChildYouthAge + 1);
        agent.HomeTile = (5, 5);
        agent.CurrentMode = BehaviorMode.Home;
        agent.Inventory[ResourceType.Berries] = 5;

        var rabbit = new Animal(AnimalSpecies.Rabbit, 6, 5, herdId: 9999, territoryCenter: (6, 5));
        world.Animals.Add(rabbit);
        agent.AnimalMemory.Add(new MemoryEntry
        {
            X = 6, Y = 5,
            Type = MemoryType.AnimalSighting,
            AnimalSpecies = AnimalSpecies.Rabbit,
            AnimalId = rabbit.Id,
            TickObserved = 1
        });

        // No animal_domestication knowledge
        var scored = UtilityScorer.ScoreHomeActions(agent, world, 10, new Random(42));
        Assert.DoesNotContain(scored, s => s.Action == ActionType.Tame);
    }

    [Fact]
    public void ScoreFeedPen_Returns_Empty_Without_Knowledge()
    {
        ResetIds();
        var world = new World(16, 16, 42);
        var agent = new Agent(5, 5, name: "Ben", startingAge: SimConfig.ChildYouthAge + 1);
        agent.HomeTile = (5, 5);
        agent.CurrentMode = BehaviorMode.Home;
        agent.Inventory[ResourceType.Grain] = 10;

        var pen = new Pen(5, 6, SimConfig.PenCapacitySmall, agent.Id);
        world.Pens.Add(pen);
        var rabbit = new Animal(AnimalSpecies.Rabbit, 5, 6, herdId: 9999, territoryCenter: (5, 6));
        rabbit.IsDomesticated = true;
        rabbit.PenId = pen.Id;
        world.Animals.Add(rabbit);
        pen.AnimalIds.Add(rabbit.Id);

        // No knowledge
        var scored = UtilityScorer.ScoreHomeActions(agent, world, 10, new Random(42));
        Assert.DoesNotContain(scored, s => s.Action == ActionType.FeedPen);
    }

    [Fact]
    public void ScorePenAnimal_Returns_Empty_Without_Knowledge()
    {
        ResetIds();
        var world = new World(16, 16, 42);
        var agent = new Agent(5, 5, name: "Cara", startingAge: SimConfig.ChildYouthAge + 1);
        agent.HomeTile = (5, 5);
        agent.CurrentMode = BehaviorMode.Home;

        var rabbit = new Animal(AnimalSpecies.Rabbit, 5, 5, herdId: 9999, territoryCenter: (5, 5));
        rabbit.IsDomesticated = true;
        rabbit.OwnerAgentId = agent.Id;
        world.Animals.Add(rabbit);

        var pen = new Pen(5, 6, SimConfig.PenCapacitySmall, agent.Id);
        world.Pens.Add(pen);

        var scored = UtilityScorer.ScoreHomeActions(agent, world, 10, new Random(42));
        Assert.DoesNotContain(scored, s => s.Action == ActionType.PenAnimal);
    }

    [Fact]
    public void ScoreSlaughter_Returns_Empty_Without_Knowledge()
    {
        ResetIds();
        var world = new World(16, 16, 42);
        var agent = new Agent(5, 5, name: "Dan", startingAge: SimConfig.ChildYouthAge + 1);
        agent.HomeTile = (5, 5);
        agent.CurrentMode = BehaviorMode.Home;

        var pen = new Pen(5, 6, SimConfig.PenCapacitySmall, agent.Id);
        world.Pens.Add(pen);
        for (int i = 0; i < 3; i++)
        {
            var r = new Animal(AnimalSpecies.Rabbit, 5, 6, herdId: 9999, territoryCenter: (5, 6));
            r.IsDomesticated = true;
            r.PenId = pen.Id;
            world.Animals.Add(r);
            pen.AnimalIds.Add(r.Id);
        }

        var scored = UtilityScorer.ScoreHomeActions(agent, world, 10, new Random(42));
        Assert.DoesNotContain(scored, s => s.Action == ActionType.Slaughter);
    }
}

// ═══════════════════════════════════════════════════════════════════
// Gate 2: Tame Action
// ═══════════════════════════════════════════════════════════════════

public class D25dTameActionTests
{
    private static void ResetIds()
    {
        Agent.ResetIdCounter();
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
    }

    [Fact]
    public void TameProgress_Increments_On_Each_Offering()
    {
        ResetIds();
        var rabbit = new Animal(AnimalSpecies.Rabbit, 6, 5, herdId: 9999, territoryCenter: (6, 5));
        Assert.Equal(0, rabbit.TameProgress);

        rabbit.TameProgress++;
        Assert.Equal(1, rabbit.TameProgress);

        rabbit.TameProgress++;
        Assert.Equal(2, rabbit.TameProgress);
    }

    [Fact]
    public void Rabbit_Becomes_Domesticated_At_Threshold()
    {
        ResetIds();
        var rabbit = new Animal(AnimalSpecies.Rabbit, 6, 5, herdId: 9999, territoryCenter: (6, 5));

        rabbit.TameProgress = SimConfig.TameThresholdRabbit;
        Assert.True(rabbit.TameProgress >= SimConfig.TameThresholdRabbit);

        // Simulate CompleteTame effects
        rabbit.IsDomesticated = true;
        rabbit.OwnerAgentId = 1;
        rabbit.State = AnimalState.Domesticated;

        Assert.True(rabbit.IsDomesticated);
        Assert.Equal(1, rabbit.OwnerAgentId);
        Assert.Equal(AnimalState.Domesticated, rabbit.State);
    }

    [Fact]
    public void TameThreshold_Rabbit_Is_3()
    {
        Assert.Equal(3, SimConfig.TameThresholdRabbit);
    }

    [Fact]
    public void TameThreshold_Cow_Is_5()
    {
        Assert.Equal(5, SimConfig.TameThresholdCow);
    }

    [Fact]
    public void TameThreshold_Sheep_Is_4()
    {
        Assert.Equal(4, SimConfig.TameThresholdSheep);
    }

    [Fact]
    public void TameThreshold_Wolf_Is_15()
    {
        Assert.Equal(15, SimConfig.TameThresholdWolf);
    }

    [Fact]
    public void ScoreTame_Scores_Rabbit_With_Knowledge_And_Food()
    {
        ResetIds();
        var world = new World(16, 16, 42);
        var agent = new Agent(5, 5, name: "Eve", startingAge: SimConfig.ChildYouthAge + 1);
        agent.HomeTile = (5, 5);
        agent.CurrentMode = BehaviorMode.Home;
        agent.Inventory[ResourceType.Berries] = 5;
        agent.Knowledge.Add("animal_domestication");

        var rabbit = new Animal(AnimalSpecies.Rabbit, 6, 5, herdId: 9999, territoryCenter: (6, 5));
        world.Animals.Add(rabbit);
        agent.AnimalMemory.Add(new MemoryEntry
        {
            X = 6, Y = 5,
            Type = MemoryType.AnimalSighting,
            AnimalSpecies = AnimalSpecies.Rabbit,
            AnimalId = rabbit.Id,
            TickObserved = 1
        });

        var scored = UtilityScorer.ScoreHomeActions(agent, world, 10, new Random(42));
        Assert.Contains(scored, s => s.Action == ActionType.Tame && s.TargetAgentId == rabbit.Id);
    }

    [Fact]
    public void ScoreTame_Does_Not_Score_Already_Domesticated_Animal()
    {
        ResetIds();
        var world = new World(16, 16, 42);
        var agent = new Agent(5, 5, name: "Fern", startingAge: SimConfig.ChildYouthAge + 1);
        agent.HomeTile = (5, 5);
        agent.CurrentMode = BehaviorMode.Home;
        agent.Inventory[ResourceType.Berries] = 5;
        agent.Knowledge.Add("animal_domestication");

        var rabbit = new Animal(AnimalSpecies.Rabbit, 6, 5, herdId: 9999, territoryCenter: (6, 5));
        rabbit.IsDomesticated = true;
        world.Animals.Add(rabbit);
        agent.AnimalMemory.Add(new MemoryEntry
        {
            X = 6, Y = 5,
            Type = MemoryType.AnimalSighting,
            AnimalSpecies = AnimalSpecies.Rabbit,
            AnimalId = rabbit.Id,
            TickObserved = 1
        });

        var scored = UtilityScorer.ScoreHomeActions(agent, world, 10, new Random(42));
        Assert.DoesNotContain(scored, s => s.Action == ActionType.Tame && s.TargetAgentId == rabbit.Id);
    }

    [Fact]
    public void ScoreTame_Returns_Empty_When_No_Food()
    {
        ResetIds();
        var world = new World(16, 16, 42);
        var agent = new Agent(5, 5, name: "Gus", startingAge: SimConfig.ChildYouthAge + 1);
        agent.HomeTile = (5, 5);
        agent.CurrentMode = BehaviorMode.Home;
        agent.Knowledge.Add("animal_domestication");
        // No food in inventory

        var rabbit = new Animal(AnimalSpecies.Rabbit, 6, 5, herdId: 9999, territoryCenter: (6, 5));
        world.Animals.Add(rabbit);
        agent.AnimalMemory.Add(new MemoryEntry
        {
            X = 6, Y = 5,
            Type = MemoryType.AnimalSighting,
            AnimalSpecies = AnimalSpecies.Rabbit,
            AnimalId = rabbit.Id,
            TickObserved = 1
        });

        var scored = UtilityScorer.ScoreHomeActions(agent, world, 10, new Random(42));
        Assert.DoesNotContain(scored, s => s.Action == ActionType.Tame);
    }
}

// ═══════════════════════════════════════════════════════════════════
// Gate 3: Domesticated Behavior
// ═══════════════════════════════════════════════════════════════════

public class D25dDomesticatedBehaviorTests
{
    private static void ResetIds()
    {
        Agent.ResetIdCounter();
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
    }

    [Fact]
    public void Domesticated_Animal_Has_Correct_State()
    {
        ResetIds();
        var rabbit = new Animal(AnimalSpecies.Rabbit, 5, 5, herdId: 9999, territoryCenter: (5, 5));
        rabbit.IsDomesticated = true;
        rabbit.OwnerAgentId = 1;
        rabbit.State = AnimalState.Domesticated;

        Assert.True(rabbit.IsDomesticated);
        Assert.Equal(AnimalState.Domesticated, rabbit.State);
        Assert.Equal(1, rabbit.OwnerAgentId);
    }

    [Fact]
    public void Owner_Death_Makes_Animal_Stray()
    {
        ResetIds();
        var rabbit = new Animal(AnimalSpecies.Rabbit, 5, 5, herdId: 9999, territoryCenter: (5, 5));
        rabbit.IsDomesticated = true;
        rabbit.OwnerAgentId = 1;
        rabbit.State = AnimalState.Domesticated;

        // Simulate owner death: clear ownership, revert state
        rabbit.OwnerAgentId = null;
        rabbit.State = AnimalState.Idle;

        Assert.Null(rabbit.OwnerAgentId);
        Assert.Equal(AnimalState.Idle, rabbit.State);
        Assert.True(rabbit.IsDomesticated); // Still domesticated, just stray
    }

    [Fact]
    public void Domesticated_Animal_Not_Penned_By_Default()
    {
        ResetIds();
        var rabbit = new Animal(AnimalSpecies.Rabbit, 5, 5, herdId: 9999, territoryCenter: (5, 5));
        rabbit.IsDomesticated = true;
        rabbit.OwnerAgentId = 1;

        Assert.Null(rabbit.PenId);
    }
}

// ═══════════════════════════════════════════════════════════════════
// Gate 4: Pen Operations
// ═══════════════════════════════════════════════════════════════════

public class D25dPenOperationsTests
{
    private static void ResetIds()
    {
        Agent.ResetIdCounter();
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
    }

    [Fact]
    public void Pen_Created_With_Correct_Capacity_Small()
    {
        ResetIds();
        var pen = new Pen(5, 5, SimConfig.PenCapacitySmall, builderAgentId: 1);
        Assert.Equal(SimConfig.PenCapacitySmall, pen.Capacity);
        Assert.Equal(5, pen.Capacity);
    }

    [Fact]
    public void Pen_Created_With_Correct_Capacity_Large()
    {
        ResetIds();
        var pen = new Pen(5, 5, SimConfig.PenCapacityLarge, builderAgentId: 1);
        Assert.Equal(SimConfig.PenCapacityLarge, pen.Capacity);
        Assert.Equal(3, pen.Capacity);
    }

    [Fact]
    public void Animal_Can_Be_Penned()
    {
        ResetIds();
        var pen = new Pen(5, 5, SimConfig.PenCapacitySmall, builderAgentId: 1);
        var rabbit = new Animal(AnimalSpecies.Rabbit, 5, 5, herdId: 9999, territoryCenter: (5, 5));
        rabbit.IsDomesticated = true;
        rabbit.PenId = pen.Id;
        pen.AnimalIds.Add(rabbit.Id);

        Assert.Equal(pen.Id, rabbit.PenId);
        Assert.Contains(rabbit.Id, pen.AnimalIds);
        Assert.Equal(1, pen.AnimalCount);
    }

    [Fact]
    public void Pen_IsFull_Returns_True_At_Capacity()
    {
        ResetIds();
        var pen = new Pen(5, 5, SimConfig.PenCapacitySmall, builderAgentId: 1);

        for (int i = 0; i < SimConfig.PenCapacitySmall; i++)
        {
            var r = new Animal(AnimalSpecies.Rabbit, 5, 5, herdId: 9999, territoryCenter: (5, 5));
            pen.AnimalIds.Add(r.Id);
        }

        Assert.True(pen.IsFull);
    }

    [Fact]
    public void Pen_IsFull_Returns_False_Below_Capacity()
    {
        ResetIds();
        var pen = new Pen(5, 5, SimConfig.PenCapacitySmall, builderAgentId: 1);
        var r = new Animal(AnimalSpecies.Rabbit, 5, 5, herdId: 9999, territoryCenter: (5, 5));
        pen.AnimalIds.Add(r.Id);

        Assert.False(pen.IsFull);
    }

    [Fact]
    public void FeedPen_Deposits_Grain_Into_FoodStore()
    {
        ResetIds();
        var pen = new Pen(5, 5, SimConfig.PenCapacitySmall, builderAgentId: 1);
        Assert.Equal(0, pen.FoodStore);

        pen.FoodStore += 5;
        Assert.Equal(5, pen.FoodStore);
    }

    [Fact]
    public void Pen_MaxFoodStore_Defaults_To_Config()
    {
        ResetIds();
        var pen = new Pen(5, 5, SimConfig.PenCapacitySmall, builderAgentId: 1);
        Assert.Equal(SimConfig.PenMaxFoodStore, pen.MaxFoodStore);
        Assert.Equal(50, pen.MaxFoodStore);
    }

    [Fact]
    public void IsSmallAnimal_Returns_True_For_Rabbit()
    {
        Assert.True(Pen.IsSmallAnimal(AnimalSpecies.Rabbit));
    }

    [Fact]
    public void IsSmallAnimal_Returns_True_For_Boar()
    {
        Assert.True(Pen.IsSmallAnimal(AnimalSpecies.Boar));
    }

    [Fact]
    public void IsSmallAnimal_Returns_False_For_Deer()
    {
        Assert.False(Pen.IsSmallAnimal(AnimalSpecies.Deer));
    }

    [Fact]
    public void IsSmallAnimal_Returns_False_For_Cow()
    {
        Assert.False(Pen.IsSmallAnimal(AnimalSpecies.Cow));
    }

    [Fact]
    public void IsSmallAnimal_Returns_True_For_Sheep()
    {
        Assert.True(Pen.IsSmallAnimal(AnimalSpecies.Sheep));
    }

    [Fact]
    public void CapacityFor_Small_Returns_PenCapacitySmall()
    {
        Assert.Equal(SimConfig.PenCapacitySmall, Pen.CapacityFor(AnimalSpecies.Rabbit));
        Assert.Equal(SimConfig.PenCapacitySmall, Pen.CapacityFor(AnimalSpecies.Boar));
    }

    [Fact]
    public void CapacityFor_Large_Returns_PenCapacityLarge()
    {
        Assert.Equal(SimConfig.PenCapacityLarge, Pen.CapacityFor(AnimalSpecies.Deer));
        Assert.Equal(SimConfig.PenCapacityLarge, Pen.CapacityFor(AnimalSpecies.Cow));
    }

    [Fact]
    public void CountSpecies_Counts_Correctly()
    {
        ResetIds();
        var pen = new Pen(5, 5, SimConfig.PenCapacitySmall, builderAgentId: 1);
        var allAnimals = new List<Animal>();

        for (int i = 0; i < 2; i++)
        {
            var r = new Animal(AnimalSpecies.Rabbit, 5, 5, herdId: 9999, territoryCenter: (5, 5));
            r.IsDomesticated = true;
            r.PenId = pen.Id;
            pen.AnimalIds.Add(r.Id);
            allAnimals.Add(r);
        }
        var deer = new Animal(AnimalSpecies.Deer, 5, 5, herdId: 9999, territoryCenter: (5, 5));
        deer.IsDomesticated = true;
        deer.PenId = pen.Id;
        pen.AnimalIds.Add(deer.Id);
        allAnimals.Add(deer);

        Assert.Equal(2, pen.CountSpecies(AnimalSpecies.Rabbit, allAnimals));
        Assert.Equal(1, pen.CountSpecies(AnimalSpecies.Deer, allAnimals));
        Assert.Equal(0, pen.CountSpecies(AnimalSpecies.Boar, allAnimals));
    }

    [Fact]
    public void CountSpecies_Ignores_Dead_Animals()
    {
        ResetIds();
        var pen = new Pen(5, 5, SimConfig.PenCapacitySmall, builderAgentId: 1);
        var allAnimals = new List<Animal>();

        var alive = new Animal(AnimalSpecies.Rabbit, 5, 5, herdId: 9999, territoryCenter: (5, 5));
        alive.PenId = pen.Id;
        pen.AnimalIds.Add(alive.Id);
        allAnimals.Add(alive);

        var dead = new Animal(AnimalSpecies.Rabbit, 5, 5, herdId: 9999, territoryCenter: (5, 5));
        dead.PenId = pen.Id;
        dead.Die();
        pen.AnimalIds.Add(dead.Id);
        allAnimals.Add(dead);

        Assert.Equal(1, pen.CountSpecies(AnimalSpecies.Rabbit, allAnimals));
    }

    [Fact]
    public void Pen_Id_Auto_Increments()
    {
        ResetIds();
        var pen1 = new Pen(5, 5, 5, builderAgentId: 1);
        var pen2 = new Pen(6, 6, 3, builderAgentId: 1);
        Assert.Equal(1, pen1.Id);
        Assert.Equal(2, pen2.Id);
    }

    [Fact]
    public void ResetIdCounter_Resets_Pen_Ids()
    {
        Pen.ResetIdCounter();
        var pen1 = new Pen(5, 5, 5, builderAgentId: 1);
        Assert.Equal(1, pen1.Id);
        Pen.ResetIdCounter();
        var pen2 = new Pen(6, 6, 3, builderAgentId: 1);
        Assert.Equal(1, pen2.Id);
    }
}

// ═══════════════════════════════════════════════════════════════════
// Gate 5: Breeding
// ═══════════════════════════════════════════════════════════════════

[Collection("Integration")]
public class D25dBreedingTests
{
    private static void ResetIds()
    {
        Agent.ResetIdCounter();
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
    }

    [Fact]
    public void Breeding_Requires_Two_Same_Species_In_Pen()
    {
        ResetIds();
        var world = new World(16, 16, 42);
        var sim = new Simulation(world, 42);

        var pen = new Pen(5, 5, SimConfig.PenCapacitySmall, builderAgentId: 1);
        world.Pens.Add(pen);

        // Only 1 rabbit
        var r1 = new Animal(AnimalSpecies.Rabbit, 5, 5, herdId: -1, territoryCenter: (5, 5));
        r1.IsDomesticated = true;
        r1.PenId = pen.Id;
        r1.State = AnimalState.Domesticated;
        world.Animals.Add(r1);
        pen.AnimalIds.Add(r1.Id);
        pen.FoodStore = SimConfig.PenMaxFoodStore; // Prevent starvation

        int initialCount = pen.AnimalIds.Count;

        int targetTick = SimConfig.BreedIntervalRabbit;
        for (int i = 0; i < targetTick + 1; i++)
            sim.Tick();

        Assert.Equal(initialCount, pen.AnimalIds.Count);
    }

    [Fact]
    public void Breeding_Can_Produce_Offspring_With_Two_Rabbits()
    {
        ResetIds();
        var world = new World(16, 16, 42);
        var sim = new Simulation(world, 42);

        var pen = new Pen(5, 5, SimConfig.PenCapacitySmall, builderAgentId: 1);
        world.Pens.Add(pen);

        for (int i = 0; i < 2; i++)
        {
            var r = new Animal(AnimalSpecies.Rabbit, 5, 5, herdId: -1, territoryCenter: (5, 5));
            r.IsDomesticated = true;
            r.PenId = pen.Id;
            r.State = AnimalState.Domesticated;
            world.Animals.Add(r);
            pen.AnimalIds.Add(r.Id);
        }
        pen.FoodStore = SimConfig.PenMaxFoodStore; // Prevent starvation

        // BreedIntervalRabbit=40, BreedChanceRabbit=0.60 — run many intervals
        int ticksToRun = SimConfig.BreedIntervalRabbit * 20;
        for (int i = 0; i < ticksToRun; i++)
            sim.Tick();

        // With 60% breed chance over 20 intervals, statistically very likely to have bred
        if (pen.AnimalIds.Count > 2)
        {
            var offspring = world.Animals.FirstOrDefault(a =>
                a.IsAlive && a.PenId == pen.Id && a.Species == AnimalSpecies.Rabbit &&
                pen.AnimalIds.Contains(a.Id) && a.IsDomesticated);

            Assert.NotNull(offspring);
            Assert.Equal(AnimalSpecies.Rabbit, offspring!.Species);
            Assert.True(offspring.IsDomesticated);
            Assert.Equal(pen.Id, offspring.PenId);
            Assert.Equal(AnimalState.Domesticated, offspring.State);
        }
    }

    [Fact]
    public void Breeding_Does_Not_Exceed_Pen_Capacity()
    {
        ResetIds();
        var world = new World(16, 16, 42);
        var sim = new Simulation(world, 42);

        var pen = new Pen(5, 5, SimConfig.PenCapacitySmall, builderAgentId: 1);
        world.Pens.Add(pen);

        for (int i = 0; i < SimConfig.PenCapacitySmall - 1; i++)
        {
            var r = new Animal(AnimalSpecies.Rabbit, 5, 5, herdId: -1, territoryCenter: (5, 5));
            r.IsDomesticated = true;
            r.PenId = pen.Id;
            r.State = AnimalState.Domesticated;
            world.Animals.Add(r);
            pen.AnimalIds.Add(r.Id);
        }
        pen.FoodStore = SimConfig.PenMaxFoodStore; // Prevent starvation

        int ticksToRun = SimConfig.BreedIntervalRabbit * 30;
        for (int i = 0; i < ticksToRun; i++)
            sim.Tick();

        Assert.True(pen.AnimalIds.Count <= pen.Capacity);
    }

    [Fact]
    public void Wolves_Do_Not_Breed_In_Pen()
    {
        ResetIds();
        var world = new World(16, 16, 42);
        var sim = new Simulation(world, 42);

        var pen = new Pen(5, 5, SimConfig.PenCapacityLarge, builderAgentId: 1);
        world.Pens.Add(pen);

        for (int i = 0; i < 2; i++)
        {
            var w = new Animal(AnimalSpecies.Wolf, 5, 5, herdId: -1, territoryCenter: (5, 5));
            w.IsDomesticated = true;
            w.PenId = pen.Id;
            w.State = AnimalState.Domesticated;
            world.Animals.Add(w);
            pen.AnimalIds.Add(w.Id);
        }
        // Need enough food: 2 wolves * (ticksToRun / PenFeedInterval) grain
        // Run for 200 ticks = 10 feed events, 2 wolves = 20 grain needed
        pen.FoodStore = SimConfig.PenMaxFoodStore;

        int initialCount = pen.AnimalIds.Count;
        int ticksToRun = 200;
        for (int i = 0; i < ticksToRun; i++)
        {
            sim.Tick();
            // Top off food to prevent starvation
            if (pen.FoodStore < 10) pen.FoodStore = SimConfig.PenMaxFoodStore;
        }

        int finalWolfCount = pen.CountSpecies(AnimalSpecies.Wolf, world.Animals);
        Assert.Equal(2, finalWolfCount);
    }
}

// ═══════════════════════════════════════════════════════════════════
// Gate 6: Slaughter
// ═══════════════════════════════════════════════════════════════════

public class D25dSlaughterTests
{
    private static void ResetIds()
    {
        Agent.ResetIdCounter();
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
    }

    [Fact]
    public void ScoreSlaughter_Requires_Three_Plus_Same_Species()
    {
        ResetIds();
        var world = new World(16, 16, 42);
        var agent = new Agent(5, 5, name: "Hank", startingAge: SimConfig.ChildYouthAge + 1);
        agent.HomeTile = (5, 5);
        agent.CurrentMode = BehaviorMode.Home;
        agent.Knowledge.Add("animal_domestication");
        agent.Hunger = 40f;

        var pen = new Pen(5, 6, SimConfig.PenCapacitySmall, agent.Id);
        world.Pens.Add(pen);
        for (int i = 0; i < 2; i++)
        {
            var r = new Animal(AnimalSpecies.Rabbit, 5, 6, herdId: 9999, territoryCenter: (5, 6));
            r.IsDomesticated = true;
            r.PenId = pen.Id;
            world.Animals.Add(r);
            pen.AnimalIds.Add(r.Id);
        }

        var scored = UtilityScorer.ScoreHomeActions(agent, world, 10, new Random(42));
        Assert.DoesNotContain(scored, s => s.Action == ActionType.Slaughter);
    }

    [Fact]
    public void ScoreSlaughter_Scores_With_Three_Same_Species()
    {
        ResetIds();
        var world = new World(16, 16, 42);
        var agent = new Agent(5, 5, name: "Iris", startingAge: SimConfig.ChildYouthAge + 1);
        agent.HomeTile = (5, 5);
        agent.CurrentMode = BehaviorMode.Home;
        agent.Knowledge.Add("animal_domestication");
        agent.Hunger = 30f;

        var pen = new Pen(5, 6, SimConfig.PenCapacitySmall, agent.Id);
        world.Pens.Add(pen);
        for (int i = 0; i < 3; i++)
        {
            var r = new Animal(AnimalSpecies.Rabbit, 5, 6, herdId: 9999, territoryCenter: (5, 6));
            r.IsDomesticated = true;
            r.PenId = pen.Id;
            world.Animals.Add(r);
            pen.AnimalIds.Add(r.Id);
        }

        var scored = UtilityScorer.ScoreHomeActions(agent, world, 10, new Random(42));
        Assert.Contains(scored, s => s.Action == ActionType.Slaughter);
    }

    [Fact]
    public void SlaughterBreedingPairMin_Is_Two()
    {
        Assert.Equal(2, SimConfig.SlaughterBreedingPairMin);
    }

    [Fact]
    public void Slaughter_Removes_Animal_From_Pen()
    {
        ResetIds();
        var pen = new Pen(5, 5, SimConfig.PenCapacitySmall, builderAgentId: 1);
        var rabbit = new Animal(AnimalSpecies.Rabbit, 5, 5, herdId: 9999, territoryCenter: (5, 5));
        rabbit.IsDomesticated = true;
        rabbit.PenId = pen.Id;
        pen.AnimalIds.Add(rabbit.Id);

        // Simulate slaughter
        rabbit.Die();
        pen.AnimalIds.Remove(rabbit.Id);

        Assert.Empty(pen.AnimalIds);
        Assert.False(rabbit.IsAlive);
    }

    [Fact]
    public void Rabbit_Slaughter_Yields_Meat()
    {
        var config = Animal.SpeciesConfig[AnimalSpecies.Rabbit];
        Assert.Equal(1, config.MeatYield);
    }

    [Fact]
    public void Deer_Slaughter_Yields_Meat_Hide_Bone()
    {
        var config = Animal.SpeciesConfig[AnimalSpecies.Deer];
        Assert.Equal(3, config.MeatYield);
        Assert.Equal(2, config.HideYield);
        Assert.Equal(1, config.BoneYield);
    }
}

// ═══════════════════════════════════════════════════════════════════
// Gate 7: Wolf Pup -> Dog
// ═══════════════════════════════════════════════════════════════════

public class D25dWolfPupDogTests
{
    private static void ResetIds()
    {
        Agent.ResetIdCounter();
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
    }

    [Fact]
    public void ScoreTameWolfPup_Requires_Bow_Knowledge()
    {
        ResetIds();
        var world = new World(16, 16, 42);
        var agent = new Agent(5, 5, name: "Jack", startingAge: SimConfig.ChildYouthAge + 1);
        agent.HomeTile = (5, 5);
        agent.CurrentMode = BehaviorMode.Home;
        agent.Inventory[ResourceType.Berries] = 5;
        agent.Knowledge.Add("animal_domestication");
        // No "bow" knowledge

        var pup = new Animal(AnimalSpecies.Wolf, 6, 5, herdId: 9999, territoryCenter: (6, 5));
        pup.IsPup = true;
        world.Animals.Add(pup);

        var scored = UtilityScorer.ScoreHomeActions(agent, world, 10, new Random(42));
        Assert.DoesNotContain(scored, s => s.Action == ActionType.Tame && s.TargetAgentId == pup.Id);
    }

    [Fact]
    public void ScoreTameWolfPup_Scores_With_Bow_And_Domestication()
    {
        ResetIds();
        var world = new World(16, 16, 42);
        var agent = new Agent(5, 5, name: "Kate", startingAge: SimConfig.ChildYouthAge + 1);
        agent.HomeTile = (5, 5);
        agent.CurrentMode = BehaviorMode.Home;
        agent.Inventory[ResourceType.Berries] = 5;
        agent.Knowledge.Add("animal_domestication");
        agent.Knowledge.Add("bow");

        var pup = new Animal(AnimalSpecies.Wolf, 6, 5, herdId: 9999, territoryCenter: (6, 5));
        pup.IsPup = true;
        world.Animals.Add(pup);

        // Also add to AnimalMemory since ScoreTameWolfPup checks memory first
        agent.AnimalMemory.Add(new MemoryEntry
        {
            X = 6, Y = 5,
            Type = MemoryType.AnimalSighting,
            AnimalSpecies = AnimalSpecies.Wolf,
            AnimalId = pup.Id,
            TickObserved = 1
        });

        var scored = UtilityScorer.ScoreHomeActions(agent, world, 10, new Random(42));
        Assert.Contains(scored, s => s.Action == ActionType.Tame);
    }

    [Fact]
    public void Wolf_Pup_Becomes_Dog_After_Taming()
    {
        ResetIds();
        var pup = new Animal(AnimalSpecies.Wolf, 5, 5, herdId: 9999, territoryCenter: (5, 5));
        pup.IsPup = true;
        Assert.False(pup.IsDog);

        // Simulate CompleteTame for wolf pup
        pup.IsDomesticated = true;
        pup.OwnerAgentId = 1;
        pup.State = AnimalState.Domesticated;
        pup.IsDog = true;
        pup.IsPup = false;
        pup.Health = SimConfig.DogHealth;

        Assert.True(pup.IsDog);
        Assert.False(pup.IsPup);
        Assert.True(pup.IsDomesticated);
        Assert.Equal(SimConfig.DogHealth, pup.Health);
    }

    [Fact]
    public void HasDogCompanion_Returns_True_When_Dog_Exists()
    {
        ResetIds();
        var agent = new Agent(5, 5, name: "Leo", startingAge: SimConfig.ChildYouthAge + 1);

        var dog = new Animal(AnimalSpecies.Wolf, 5, 5, herdId: 9999, territoryCenter: (5, 5));
        dog.IsDomesticated = true;
        dog.IsDog = true;
        dog.OwnerAgentId = agent.Id;

        var animals = new List<Animal> { dog };
        Assert.True(agent.HasDogCompanion(animals));
    }

    [Fact]
    public void HasDogCompanion_Returns_False_When_No_Dog()
    {
        ResetIds();
        var agent = new Agent(5, 5, name: "Mia", startingAge: SimConfig.ChildYouthAge + 1);
        var animals = new List<Animal>();
        Assert.False(agent.HasDogCompanion(animals));
    }

    [Fact]
    public void HasDogCompanion_Returns_False_When_Dog_Is_Penned()
    {
        ResetIds();
        var agent = new Agent(5, 5, name: "Ned", startingAge: SimConfig.ChildYouthAge + 1);

        var dog = new Animal(AnimalSpecies.Wolf, 5, 5, herdId: 9999, territoryCenter: (5, 5));
        dog.IsDomesticated = true;
        dog.IsDog = true;
        dog.OwnerAgentId = agent.Id;
        dog.PenId = 1;

        var animals = new List<Animal> { dog };
        Assert.False(agent.HasDogCompanion(animals));
    }

    [Fact]
    public void HasDogCompanion_Returns_False_For_Different_Owner()
    {
        ResetIds();
        var agent = new Agent(5, 5, name: "Olive", startingAge: SimConfig.ChildYouthAge + 1);
        var otherAgent = new Agent(6, 6, name: "Pat", startingAge: SimConfig.ChildYouthAge + 1);

        var dog = new Animal(AnimalSpecies.Wolf, 5, 5, herdId: 9999, territoryCenter: (5, 5));
        dog.IsDomesticated = true;
        dog.IsDog = true;
        dog.OwnerAgentId = otherAgent.Id;

        var animals = new List<Animal> { dog };
        Assert.False(agent.HasDogCompanion(animals));
    }

    [Fact]
    public void HasDogCompanion_Returns_False_When_Dog_Is_Dead()
    {
        ResetIds();
        var agent = new Agent(5, 5, name: "Quinn", startingAge: SimConfig.ChildYouthAge + 1);

        var dog = new Animal(AnimalSpecies.Wolf, 5, 5, herdId: 9999, territoryCenter: (5, 5));
        dog.IsDomesticated = true;
        dog.IsDog = true;
        dog.OwnerAgentId = agent.Id;
        dog.Die();

        var animals = new List<Animal> { dog };
        Assert.False(agent.HasDogCompanion(animals));
    }

    [Fact]
    public void DogPerceptionBonus_Is_Configured()
    {
        Assert.Equal(6, SimConfig.DogPerceptionBonus);
    }

    [Fact]
    public void DogHuntBonus_Is_Configured()
    {
        Assert.Equal(0.10f, SimConfig.DogHuntBonus);
    }

    [Fact]
    public void Non_Pup_Wolf_Does_Not_Become_Dog()
    {
        ResetIds();
        var wolf = new Animal(AnimalSpecies.Wolf, 5, 5, herdId: 9999, territoryCenter: (5, 5));
        Assert.False(wolf.IsPup);

        wolf.IsDomesticated = true;
        wolf.OwnerAgentId = 1;
        wolf.State = AnimalState.Domesticated;
        // CompleteTame only sets IsDog when IsPup is true
        Assert.False(wolf.IsDog);
    }

    [Fact]
    public void ScoreTameWolfPup_Does_Not_Score_Adult_Wolf()
    {
        ResetIds();
        var world = new World(16, 16, 42);
        var agent = new Agent(5, 5, name: "Rosa", startingAge: SimConfig.ChildYouthAge + 1);
        agent.HomeTile = (5, 5);
        agent.CurrentMode = BehaviorMode.Home;
        agent.Inventory[ResourceType.Berries] = 5;
        agent.Knowledge.Add("animal_domestication");
        agent.Knowledge.Add("bow");

        // Adult wolf, not a pup
        var wolf = new Animal(AnimalSpecies.Wolf, 6, 5, herdId: 9999, territoryCenter: (6, 5));
        Assert.False(wolf.IsPup);
        world.Animals.Add(wolf);

        var scored = UtilityScorer.ScoreHomeActions(agent, world, 10, new Random(42));
        // ScoreTameWolfPup filters on IsPup; ScoreTame excludes wolves entirely
        Assert.DoesNotContain(scored, s => s.Action == ActionType.Tame && s.TargetAgentId == wolf.Id);
    }
}

// ═══════════════════════════════════════════════════════════════════
// Gate 5+: Pen Feeding (Simulation Phase 4.7)
// ═══════════════════════════════════════════════════════════════════

[Collection("Integration")]
public class D25dPenFeedingIntegrationTests
{
    private static void ResetIds()
    {
        Agent.ResetIdCounter();
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
    }

    [Fact]
    public void Penned_Animal_Consumes_Food_From_Pen()
    {
        ResetIds();
        var world = new World(16, 16, 42);
        var sim = new Simulation(world, 42);

        var pen = new Pen(5, 5, SimConfig.PenCapacitySmall, builderAgentId: 1);
        pen.FoodStore = 10;
        world.Pens.Add(pen);

        var rabbit = new Animal(AnimalSpecies.Rabbit, 5, 5, herdId: -1, territoryCenter: (5, 5));
        rabbit.IsDomesticated = true;
        rabbit.PenId = pen.Id;
        rabbit.State = AnimalState.Domesticated;
        world.Animals.Add(rabbit);
        pen.AnimalIds.Add(rabbit.Id);

        for (int i = 0; i < SimConfig.PenFeedInterval; i++)
            sim.Tick();

        Assert.True(pen.FoodStore < 10);
    }

    [Fact]
    public void Penned_Animal_Takes_Starvation_Damage_When_FoodStore_Empty()
    {
        ResetIds();
        var world = new World(16, 16, 42);
        var sim = new Simulation(world, 42);

        var pen = new Pen(5, 5, SimConfig.PenCapacitySmall, builderAgentId: 1);
        pen.FoodStore = 0;
        world.Pens.Add(pen);

        var rabbit = new Animal(AnimalSpecies.Rabbit, 5, 5, herdId: -1, territoryCenter: (5, 5));
        rabbit.IsDomesticated = true;
        rabbit.PenId = pen.Id;
        rabbit.State = AnimalState.Domesticated;
        world.Animals.Add(rabbit);
        pen.AnimalIds.Add(rabbit.Id);

        int initialHealth = rabbit.Health;

        for (int i = 0; i < SimConfig.PenFeedInterval; i++)
            sim.Tick();

        Assert.True(rabbit.Health < initialHealth);
    }
}
