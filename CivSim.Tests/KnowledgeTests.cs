using CivSim.Core;
using Xunit;

namespace CivSim.Tests;

/// <summary>
/// Category 2: Knowledge Propagation
/// All positions are OFFSETS from the simulation spawn center.
/// Rules: RULE-K1, RULE-K3
/// </summary>
public class KnowledgeTests
{
    [Fact]
    public void Discovery_Propagates_To_Settlement_Member_Within_Deadline()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 80f).AgentAt("Alice", 0, 0)
            .AddAgent("Bob",   isMale: true,  hunger: 80f).AgentAt("Bob",   1, 0)
            .ResourceAt(0, 1, ResourceType.Berries, 30)
            .ResourceAt(1, 1, ResourceType.Berries, 30)
            .Build();

        sim.TriggerDiscovery("Alice", "fire_making");
        sim.Tick(960);

        Assert.True(sim.GetAgent("Bob").Knowledge.Contains("fire_making"),
            "fire_making should propagate from Alice to Bob within 960 ticks (RULE-K1)");
    }

    [Fact]
    public void Knowledge_Does_Not_Propagate_Without_Discovery_Event()
    {
        // Adding knowledge via LearnDiscovery (no settlement event) should NOT
        // propagate to other agents, even nearby ones.
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 80f).AgentAt("Alice", 0, 0)
            .AddAgent("Bob",   isMale: true,  hunger: 80f).AgentAt("Bob",   2, 0)
            .ResourceAt(0, 1, ResourceType.Berries, 20)
            .ResourceAt(2, 1, ResourceType.Berries, 20)
            .Build();

        // Add directly — no discovery event fired, no propagation started.
        sim.GetAgent("Alice").LearnDiscovery("clay_pottery");

        sim.Tick(1200);

        Assert.False(sim.GetAgent("Bob").Knowledge.Contains("clay_pottery"),
            "Knowledge added without a discovery event should not propagate (geographic friction)");
    }

    [Fact]
    public void All_Group_Members_Receive_Propagated_Discovery()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice",   isMale: false, hunger: 80f).AgentAt("Alice",   0, 0)
            .AddAgent("Bob",     isMale: true,  hunger: 80f).AgentAt("Bob",     1, 0)
            .AddAgent("Charlie", isMale: true,  hunger: 80f).AgentAt("Charlie", 0, 1)
            .AgentInventory("Alice",   ResourceType.Berries, 8)
            .AgentInventory("Bob",     ResourceType.Berries, 8)
            .AgentInventory("Charlie", ResourceType.Berries, 8)
            .ResourceAt(0, -1, ResourceType.Berries, 30)
            .ResourceAt(1, 1,  ResourceType.Berries, 30)
            .Build();

        sim.TriggerDiscovery("Alice", "stone_knife");
        sim.Tick(1200);

        Assert.True(sim.GetAgent("Bob").Knowledge.Contains("stone_knife"),
            "Bob should receive propagated discovery within 1200 ticks");
        Assert.True(sim.GetAgent("Charlie").Knowledge.Contains("stone_knife"),
            "Charlie should receive propagated discovery within 1200 ticks");
    }

    [Fact]
    public void Multiple_Discoveries_All_Propagate_Independently()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 80f).AgentAt("Alice", 0, 0)
            .AddAgent("Bob",   isMale: true,  hunger: 80f).AgentAt("Bob",   1, 0)
            .ResourceAt(0, 1, ResourceType.Berries, 30)
            .Build();

        sim.TriggerDiscovery("Alice", "fire_making");
        sim.TriggerDiscovery("Alice", "stone_knife");

        sim.Tick(1200);

        var bob = sim.GetAgent("Bob");
        Assert.True(bob.Knowledge.Contains("fire_making"),  "fire_making should propagate to Bob");
        Assert.True(bob.Knowledge.Contains("stone_knife"), "stone_knife should propagate to Bob");
    }
}
