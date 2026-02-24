namespace CivSim.Core;

/// <summary>
/// Types of things an agent can remember from perception.
/// </summary>
public enum MemoryType
{
    Resource,
    AgentSighting,
    Structure
}

/// <summary>
/// A single entry in an agent's short-term memory buffer.
/// Recorded during perception, decays over time, capped in quantity.
/// </summary>
public class MemoryEntry
{
    public int X { get; set; }
    public int Y { get; set; }
    public MemoryType Type { get; set; }

    /// <summary>Resource type observed (only set when Type == Resource).</summary>
    public ResourceType? Resource { get; set; }

    /// <summary>Quantity of resource observed (only set when Type == Resource).</summary>
    public int Quantity { get; set; }

    /// <summary>Agent ID observed (only set when Type == AgentSighting).</summary>
    public int? AgentId { get; set; }

    /// <summary>Structure ID observed (only set when Type == Structure).</summary>
    public string? StructureId { get; set; }

    /// <summary>Tick when this observation was made.</summary>
    public int TickObserved { get; set; }
}
