namespace CivSim.Core.Events;

/// <summary>
/// Lightweight event struct produced by the simulation and consumed by subscribers.
/// Value-type structs, not heap-allocated objects.
/// GDD v1.8: Added RecipeId for discovery milestone tracking.
/// </summary>
public readonly struct SimEvent
{
    public int Tick { get; init; }
    public EventType Type { get; init; }
    public EventCategory Category { get; init; }
    public string Message { get; init; }
    public int AgentId { get; init; }
    public int SecondaryId { get; init; }

    /// <summary>GDD v1.8 Section 9: Recipe ID for Discovery events, for announcement level lookup.</summary>
    public string? RecipeId { get; init; }

    /// <summary>Maps existing EventType enum to GDD v1.6.2 event categories.</summary>
    public static EventCategory ClassifyType(EventType type) => type switch
    {
        EventType.Birth => EventCategory.Critical,
        EventType.Death => EventCategory.Critical,
        EventType.Discovery => EventCategory.Critical,
        EventType.Milestone => EventCategory.Notable,
        EventType.Info => EventCategory.Notable,
        EventType.Movement => EventCategory.Routine,
        EventType.Action => EventCategory.Routine,
        _ => EventCategory.Routine
    };
}
