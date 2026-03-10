using CivSim.Core.Events;

namespace CivSim.Core;

/// <summary>
/// Event storage for the simulation. Receives events from the EventBus via subscriber bridge.
/// No synchronous console writes. Events are stored for Raylib renderer and diagnostics.
/// GDD v1.8: Copies RecipeId from SimEvent for milestone announcement support.
/// </summary>
public class EventLogger
{
    private readonly List<SimulationEvent> events = new();
    private readonly int maxStoredEvents;

    public EventLogger(int maxStoredEvents = 1000)
    {
        this.maxStoredEvents = maxStoredEvents;
    }

    /// <summary>
    /// Called by EventBus subscriber bridge. Ingests batched events at tick end.
    /// </summary>
    public void IngestFromBus(IReadOnlyList<SimEvent> tickEvents)
    {
        foreach (var evt in tickEvents)
        {
            var simEvt = new SimulationEvent(evt.Tick, evt.Message, evt.Type)
            {
                RecipeId = evt.RecipeId,
                AgentId = evt.AgentId
            };
            events.Add(simEvt);

            if (events.Count > maxStoredEvents)
                events.RemoveAt(0);
        }
    }

    /// <summary>
    /// Legacy direct log method. Kept for any remaining callers outside the bus pipeline.
    /// </summary>
    public void Log(int tick, string message, EventType type = EventType.Info)
    {
        var evt = new SimulationEvent(tick, message, type);
        events.Add(evt);

        if (events.Count > maxStoredEvents)
            events.RemoveAt(0);
    }

    /// <summary>Get recent events for display (Raylib UI panel).</summary>
    public IReadOnlyList<SimulationEvent> GetRecentEvents(int count = 50)
    {
        int skip = Math.Max(0, events.Count - count);
        return events.Skip(skip).ToList();
    }

    /// <summary>Returns all events that occurred on the specified tick.</summary>
    public List<SimulationEvent> GetEventsForTick(int tick)
    {
        return events.Where(e => e.Tick == tick).ToList();
    }

    public int TotalEvents => events.Count;
}
