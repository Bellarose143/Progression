namespace CivSim.Core.Events;

/// <summary>
/// Event Bus: pre-allocated ring buffer with per-tick staging and subscriber dispatch.
/// Events are collected during a tick, then flushed to subscribers at tick end.
/// Ring buffer capacity: 1024. Overflow drops Routine first, then Notable. Critical never dropped.
/// GDD v1.8: Emit() accepts optional recipeId for discovery event tracking.
/// </summary>
public class EventBus
{
    private const int RingCapacity = 1024;
    private readonly SimEvent[] _ring = new SimEvent[RingCapacity];
    private int _head;
    private int _count;

    // Per-tick staging buffer
    private readonly List<SimEvent> _staged = new(64);

    // Subscribers receive batched events at tick end
    private readonly List<Action<IReadOnlyList<SimEvent>>> _subscribers = new();

    /// <summary>Number of events staged this tick (before flush).</summary>
    public int StagedCount => _staged.Count;

    /// <summary>Emit a typed event into the per-tick staging buffer.</summary>
    public void Emit(SimEvent evt)
    {
        _staged.Add(evt);
    }

    /// <summary>Convenience overload matching the old logger.Log() signature for easy migration.</summary>
    public void Emit(int tick, string message, EventType type, int agentId = -1, int secondaryId = -1, string? recipeId = null)
    {
        _staged.Add(new SimEvent
        {
            Tick = tick,
            Type = type,
            Category = SimEvent.ClassifyType(type),
            Message = message,
            AgentId = agentId,
            SecondaryId = secondaryId,
            RecipeId = recipeId
        });
    }

    /// <summary>
    /// Called at end of each tick. Commits staged events to ring buffer and dispatches to all subscribers.
    /// </summary>
    public void FlushTick()
    {
        if (_staged.Count == 0) return;

        // Commit to ring buffer
        foreach (var evt in _staged)
        {
            int idx = (_head + _count) % RingCapacity;

            if (_count < RingCapacity)
            {
                _ring[idx] = evt;
                _count++;
            }
            else
            {
                // Buffer full — try to evict a lower-priority event
                if (TryEvict(evt.Category))
                {
                    // Re-find a slot (eviction freed one)
                    idx = (_head + _count) % RingCapacity;
                    _ring[idx] = evt;
                    _count++;
                }
                else if (evt.Category == EventCategory.Critical)
                {
                    // Force overwrite oldest for Critical events
                    _ring[_head] = evt;
                    _head = (_head + 1) % RingCapacity;
                }
                // Otherwise drop the event (Routine/Notable when buffer is full of higher-priority)
            }
        }

        // Dispatch to subscribers
        var batch = (IReadOnlyList<SimEvent>)_staged;
        foreach (var sub in _subscribers)
            sub(batch);

        _staged.Clear();
    }

    /// <summary>Subscribe to receive batched events at tick end.</summary>
    public void Subscribe(Action<IReadOnlyList<SimEvent>> handler)
    {
        _subscribers.Add(handler);
    }

    /// <summary>Get recent events from ring buffer for display.</summary>
    public List<SimEvent> GetRecent(int count, EventCategory? maxCategory = null)
    {
        var result = new List<SimEvent>();
        int start = Math.Max(0, _count - count);
        for (int i = start; i < _count; i++)
        {
            int idx = (_head + i) % RingCapacity;
            if (maxCategory == null || _ring[idx].Category <= maxCategory.Value)
                result.Add(_ring[idx]);
        }
        return result;
    }

    /// <summary>Get events for a specific tick from the ring buffer.</summary>
    public List<SimEvent> GetEventsForTick(int tick)
    {
        var result = new List<SimEvent>();
        for (int i = 0; i < _count; i++)
        {
            int idx = (_head + i) % RingCapacity;
            if (_ring[idx].Tick == tick)
                result.Add(_ring[idx]);
        }
        return result;
    }

    /// <summary>Check staged (not yet flushed) events for a specific type this tick.</summary>
    public bool HasStagedEventOfType(EventType type)
    {
        foreach (var evt in _staged)
        {
            if (evt.Type == type)
                return true;
        }
        return false;
    }

    /// <summary>Try to evict the lowest-priority event from the ring buffer to make room.</summary>
    private bool TryEvict(EventCategory incomingCategory)
    {
        // Search for the lowest-priority event (Trace > Routine > Notable > Critical)
        for (var cat = EventCategory.Trace; cat > incomingCategory; cat--)
        {
            for (int i = 0; i < _count; i++)
            {
                int idx = (_head + i) % RingCapacity;
                if (_ring[idx].Category == cat)
                {
                    // Evict by shifting remaining events down
                    // For simplicity in a ring buffer, just mark as default and decrement count
                    // This leaves a gap, but the ring buffer will overwrite it
                    RemoveAt(i);
                    return true;
                }
            }
        }
        return false;
    }

    private void RemoveAt(int ringOffset)
    {
        // Shift elements after the removed one toward the head
        for (int i = ringOffset; i < _count - 1; i++)
        {
            int from = (_head + i + 1) % RingCapacity;
            int to = (_head + i) % RingCapacity;
            _ring[to] = _ring[from];
        }
        _count--;
    }
}
