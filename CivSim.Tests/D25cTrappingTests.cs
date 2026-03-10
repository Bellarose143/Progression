using Xunit;
using CivSim.Core;

namespace CivSim.Tests;

[Collection("Sequential")]
[Trait("Category", "Integration")]
public class D25cTrappingTests
{
    [Fact]
    public void Trap_Config_Constants_Exist()
    {
        Assert.Equal(2, SimConfig.TrapPlacementTicks);
        Assert.Equal(0.03f, SimConfig.TrapCatchChance);
        Assert.Equal(200, SimConfig.TrapDecayTicks);
        Assert.Equal(3, SimConfig.MaxTrapsPerAgent);
        Assert.Equal(0.15f, SimConfig.TrapBaseScore);
        Assert.Equal(25, SimConfig.TrapPlacementRadius);
    }

    [Fact]
    public void SetTrap_ActionType_Exists()
    {
        var action = ActionType.SetTrap;
        Assert.Equal("SetTrap", action.ToString());
    }

    [Fact]
    public void SetTrapAt_GoalType_Exists()
    {
        var goal = GoalType.SetTrapAt;
        Assert.Equal("SetTrapAt", goal.ToString());
    }

    [Fact]
    public void Trap_Class_Properties()
    {
        var trap = new Trap
        {
            X = 10,
            Y = 20,
            PlacedByAgentId = 1,
            TickPlaced = 500,
            IsActive = true
        };
        Assert.Equal(10, trap.X);
        Assert.Equal(20, trap.Y);
        Assert.Equal(1, trap.PlacedByAgentId);
        Assert.Equal(500, trap.TickPlaced);
        Assert.True(trap.IsActive);
        Assert.Null(trap.CaughtCarcass);
    }

    [Fact]
    public void World_Has_Traps_List()
    {
        Agent.ResetIdCounter();
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
        var world = new World(16, 16, 42);
        Assert.NotNull(world.Traps);
        Assert.Empty(world.Traps);
    }

    [Fact]
    public void Trapping_Recipe_Requires_Stone_Knife_And_Foraging_Knowledge()
    {
        var recipe = RecipeRegistry.AllRecipes.FirstOrDefault(r => r.Id == "trapping");
        Assert.NotNull(recipe);
        Assert.Contains("stone_knife", recipe!.RequiredKnowledge);
        Assert.Contains("foraging_knowledge", recipe.RequiredKnowledge);
        Assert.Equal(3, recipe.Tier);
        Assert.Equal("Food", recipe.Branch);
    }

    [Fact]
    public void ProcessTraps_Decays_Trap_After_TrapDecayTicks()
    {
        Agent.ResetIdCounter();
        Animal.ResetIdCounter();
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
        var world = new World(16, 16, 42);

        // Add trap at tick 0
        var trap = new Trap
        {
            X = 5, Y = 5,
            PlacedByAgentId = 1,
            TickPlaced = 0,
            IsActive = true
        };
        world.Traps.Add(trap);

        var sim = new Simulation(world, 42);
        // Run for TrapDecayTicks + margin
        for (int i = 0; i < SimConfig.TrapDecayTicks + 10; i++)
            sim.Tick();

        // Trap should be decayed (inactive)
        Assert.False(world.Traps[0].IsActive);
    }
}
