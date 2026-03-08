using CivSim.Core;
using Xunit;
using System.Linq;

namespace CivSim.Tests;

[Collection("Sequential")]
public class D25cCombatTests
{
    // Test 1: Deer carcass has correct yields
    [Fact]
    public void Deer_Carcass_Has_Hide_And_Bone_Yields()
    {
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
        var carcass = new Carcass(5, 5, AnimalSpecies.Deer, 3, 2, 1);
        Assert.Equal(3, carcass.MeatYield);
        Assert.Equal(2, carcass.HideYield);
        Assert.Equal(1, carcass.BoneYield);
    }

    // Test 2: TryAddToInventory respects non-food cap for Hide/Bone
    [Fact]
    public void Hide_And_Bone_Respect_NonFood_Cap()
    {
        Agent.ResetIdCounter();
        var agent = new Agent(5, 5, name: "Test");
        agent.Hunger = 80; // well-fed (above NonFoodPickupHungerGate)

        // Fill up to hard cap with existing non-food
        for (int i = 0; i < SimConfig.NonFoodInventoryHardCap; i++)
            agent.TryAddToInventory(ResourceType.Wood, 1, enforceNonFoodGuards: true);

        // Hide and Bone should be blocked
        Assert.False(agent.TryAddToInventory(ResourceType.Hide, 1, enforceNonFoodGuards: true));
        Assert.False(agent.TryAddToInventory(ResourceType.Bone, 1, enforceNonFoodGuards: true));
    }

    // Test 3: Spear recipe has correct config
    [Fact]
    public void Spear_Recipe_Exists_With_Correct_Prerequisites()
    {
        var spear = RecipeRegistry.AllRecipes.FirstOrDefault(r => r.Id == "spear");
        Assert.NotNull(spear);
        Assert.Equal(3, spear.Tier);
        Assert.Contains("hafted_tools", spear.RequiredKnowledge);
        Assert.Equal(2, spear.RequiredResources[ResourceType.Wood]);
        Assert.Equal(1, spear.RequiredResources[ResourceType.Stone]);
    }

    // Test 4: Bow recipe requires spear + weaving + Hide
    [Fact]
    public void Bow_Recipe_Requires_Spear_Weaving_And_Hide()
    {
        var bow = RecipeRegistry.AllRecipes.FirstOrDefault(r => r.Id == "bow");
        Assert.NotNull(bow);
        Assert.Equal(4, bow.Tier);
        Assert.Contains("spear", bow.RequiredKnowledge);
        Assert.Contains("weaving", bow.RequiredKnowledge);
        Assert.True(bow.RequiredResources.ContainsKey(ResourceType.Hide));
    }

    // Test 5: Trapping recipe config
    [Fact]
    public void Trapping_Recipe_Exists_With_Correct_Prerequisites()
    {
        var trapping = RecipeRegistry.AllRecipes.FirstOrDefault(r => r.Id == "trapping");
        Assert.NotNull(trapping);
        Assert.Equal(3, trapping.Tier);
        Assert.Contains("stone_knife", trapping.RequiredKnowledge);
        Assert.Contains("foraging_knowledge", trapping.RequiredKnowledge);
        Assert.True(trapping.RequiredResources.ContainsKey(ResourceType.Hide));
    }

    // Test 6: bone_tools now requires Bone not Meat
    [Fact]
    public void BoneTools_Requires_Bone_Not_Meat()
    {
        var boneTools = RecipeRegistry.AllRecipes.FirstOrDefault(r => r.Id == "bone_tools");
        Assert.NotNull(boneTools);
        Assert.True(boneTools.RequiredResources.ContainsKey(ResourceType.Bone));
        Assert.False(boneTools.RequiredResources.ContainsKey(ResourceType.Meat));
        Assert.Equal(2, boneTools.RequiredResources[ResourceType.Bone]);
    }

    // Test 7: Wolf carcass has no meat, only hide and bone
    [Fact]
    public void Wolf_Carcass_Has_No_Meat()
    {
        Carcass.ResetIdCounter();
        Pen.ResetIdCounter();
        var carcass = new Carcass(5, 5, AnimalSpecies.Wolf, 0, 2, 1);
        Assert.Equal(0, carcass.MeatYield);
        Assert.Equal(2, carcass.HideYield);
        Assert.Equal(1, carcass.BoneYield);
    }

    // Test 8: Animal species config has correct yields
    [Fact]
    public void Animal_Species_Config_Has_Correct_Yields()
    {
        var boar = Animal.SpeciesConfig[AnimalSpecies.Boar];
        Assert.Equal(4, boar.MeatYield);
        Assert.Equal(1, boar.HideYield);
        Assert.Equal(2, boar.BoneYield);

        var deer = Animal.SpeciesConfig[AnimalSpecies.Deer];
        Assert.Equal(3, deer.MeatYield);
        Assert.Equal(2, deer.HideYield);
        Assert.Equal(1, deer.BoneYield);
    }
}
