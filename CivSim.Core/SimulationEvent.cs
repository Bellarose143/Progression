namespace CivSim.Core;

/// <summary>
/// Represents an event that occurred in the simulation.
/// Events are logged and displayed to make the simulation observable.
/// </summary>
public class SimulationEvent
{
    public int Tick { get; set; }
    public string Message { get; set; }
    public EventType Type { get; set; }

    /// <summary>GDD v1.8 Section 9: Recipe ID for Discovery events, used for announcement level lookup.</summary>
    public string? RecipeId { get; set; }

    public SimulationEvent(int tick, string message, EventType type = EventType.Info)
    {
        Tick = tick;
        Message = message;
        Type = type;
    }

    public override string ToString()
    {
        return $"[Tick {Tick,4}] {Message}";
    }
}

/// <summary>
/// Categories of events for potential filtering/coloring.
/// </summary>
public enum EventType
{
    Info,       // General information
    Movement,   // Agent movement
    Action,     // Agent actions (gather, eat, craft, etc.)
    Birth,      // New agent born
    Death,      // Agent died
    Discovery,  // Technology discovered
    Milestone   // Important milestones
}
