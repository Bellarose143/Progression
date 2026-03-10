namespace CivSim.Core.Events;

/// <summary>
/// Event severity categories for filtering and overflow handling.
/// GDD v1.6.2: Critical events are never dropped from the ring buffer.
/// </summary>
public enum EventCategory
{
    /// <summary>Birth, death, discovery, extinction — always captured.</summary>
    Critical = 0,

    /// <summary>Teaching, building, depletion, migration, milestones.</summary>
    Notable = 1,

    /// <summary>Individual eat, gather, move, rest actions.</summary>
    Routine = 2,

    /// <summary>AI decision reasoning, perception details — dev builds only.</summary>
    Trace = 3
}
