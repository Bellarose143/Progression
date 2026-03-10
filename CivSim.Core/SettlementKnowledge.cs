using CivSim.Core.Events;

namespace CivSim.Core;

/// <summary>
/// GDD v1.8 Section 5: A geographic lore entry representing a permanent landmark
/// or resource deposit known to the settlement. These survive agent deaths and
/// represent "grandpa's knowledge of the copper vein in the eastern cliffs."
/// </summary>
public class GeographicEntry
{
    /// <summary>Tile X coordinate of the feature.</summary>
    public int X { get; set; }

    /// <summary>Tile Y coordinate of the feature.</summary>
    public int Y { get; set; }

    /// <summary>Type of feature: "ore_vein", "rich_quarry", "cave", "natural_spring",
    /// "berry_patch", "animal_herd", "fish_school", "grain_field".</summary>
    public string FeatureType { get; set; } = string.Empty;

    /// <summary>Primary resource at this location, if any. Null for landmark-only features (caves, springs).</summary>
    public ResourceType? Resource { get; set; }

    /// <summary>Tick when this location was first discovered and reported to the settlement.</summary>
    public int DiscoveredTick { get; set; }

    /// <summary>Name of the agent who first discovered and reported this feature.</summary>
    public string DiscovererName { get; set; } = string.Empty;

    /// <summary>Whether this deposit has been confirmed depleted (resource gone). Lore remains but agents won't path to it.</summary>
    public bool IsDepleted { get; set; }
}

/// <summary>
/// GDD v1.8 Section 2: Communal knowledge propagation within settlements.
///
/// When an agent makes a discovery within their settlement, the knowledge enters
/// settlement propagation:
///   - Pre-writing: Oral propagation takes 1-2 sim-days (reduced by oral_tradition).
///     If discoverer dies mid-propagation, knowledge may be partially lost.
///   - Post-writing: Propagation is near-instant and permanent. Discoveries persist
///     in the settlement knowledge base even if all agents die.
///
/// An agent who discovers something OUTSIDE their settlement carries it as personal
/// knowledge. They must physically return to share it.
///
/// oral_tradition auto-triggers after 5 oral propagations complete.
/// Social trait agents reduce propagation time by 40%.
/// Monument halves propagation time.
///
/// GDD v1.8 Section 5: Geographic lore system — permanent settlement knowledge of
/// notable locations (ore veins, quarries, caves, resource patches). Persists across
/// generations. Explorers carry geographic discoveries as personal knowledge until
/// they return home.
/// </summary>
public class SettlementKnowledge
{
    /// <summary>Tracks an in-progress oral knowledge propagation.</summary>
    public class PropagationEntry
    {
        /// <summary>The discovery being propagated.</summary>
        public string DiscoveryId { get; set; } = string.Empty;

        /// <summary>Agent ID of the discoverer (needed for death-during-propagation check).</summary>
        public int DiscovererId { get; set; }

        /// <summary>Tick when propagation started.</summary>
        public int StartTick { get; set; }

        /// <summary>Total ticks required for propagation to complete.</summary>
        public int DurationTicks { get; set; }

        /// <summary>Whether the discoverer was alive when last checked.</summary>
        public bool DiscovererAlive { get; set; } = true;

        /// <summary>Tick at which discoverer died (0 = still alive).</summary>
        public int DiscovererDeathTick { get; set; }
    }

    /// <summary>Per-settlement knowledge tracking.</summary>
    public class SettlementKnowledgeState
    {
        /// <summary>Settlement ID this state belongs to.</summary>
        public int SettlementId { get; set; }

        /// <summary>Permanent knowledge base for this settlement.
        /// Pre-writing: only completed propagations.
        /// Post-writing: all discoveries, persists even if agents die.</summary>
        public HashSet<string> KnowledgeBase { get; set; } = new();

        /// <summary>In-progress oral propagation queue.</summary>
        public List<PropagationEntry> PendingPropagations { get; set; } = new();

        /// <summary>Count of discoveries that have been successfully propagated through
        /// the oral system. When this reaches OralTraditionPropagationThreshold,
        /// oral_tradition is auto-granted.</summary>
        public int OralPropagationCount { get; set; }

        /// <summary>Whether this settlement has writing (cached for performance).</summary>
        public bool HasWriting { get; set; }

        /// <summary>Whether this settlement has oral_tradition (cached for performance).</summary>
        public bool HasOralTradition { get; set; }

        /// <summary>Whether this settlement has a monument within radius (cached, updated periodically).</summary>
        public bool HasMonument { get; set; }

        /// <summary>GDD v1.8 Section 5: Permanent geographic lore — notable locations known to this settlement.
        /// Keyed by (X, Y) position. Persists across generations and agent deaths.</summary>
        public Dictionary<(int X, int Y), GeographicEntry> GeographicLore { get; set; } = new();
    }

    /// <summary>Knowledge state per settlement, keyed by settlement center position for persistence.</summary>
    private readonly Dictionary<(int X, int Y), SettlementKnowledgeState> _settlementStates = new();

    /// <summary>
    /// Called when an agent makes a discovery. Determines whether it enters settlement
    /// propagation or remains personal knowledge.
    /// </summary>
    /// <returns>True if discovery entered settlement propagation, false if personal only.</returns>
    public bool OnDiscovery(Agent agent, string discoveryId, List<Settlement> settlements,
        List<Agent> allAgents, EventBus bus, int currentTick)
    {
        // Find which settlement this agent belongs to (if any)
        var settlement = FindAgentSettlement(agent, settlements);
        if (settlement == null)
        {
            // Check for existing founding group first (avoid creating duplicates)
            // A founding group may already exist from a previous discovery
            settlement = settlements.FirstOrDefault(s =>
                s.Id < 0 && s.ResidentAgentIds.Contains(agent.Id));

            if (settlement == null)
            {
                // No formal settlement or founding group — check for nearby agents to form one.
                // Two people standing next to each other in the open are still a community.
                settlement = TryCreateFoundingGroup(agent, allAgents, currentTick);
                if (settlement == null)
                {
                    // Truly alone — personal knowledge only (explorer knowledge)
                    return false;
                }
                // Add the founding group to the settlements list so propagation can find it
                settlements.Add(settlement);
            }
        }

        var state = GetOrCreateState(settlement);

        // Already known by settlement?
        if (state.KnowledgeBase.Contains(discoveryId))
            return true; // Already propagated, nothing to do

        // Post-writing era: instant and permanent propagation
        if (state.HasWriting)
        {
            state.KnowledgeBase.Add(discoveryId);
            PropagateToResidents(settlement, discoveryId, allAgents);

            bus.Emit(currentTick,
                $"Settlement '{settlement.Name}' recorded '{discoveryId}' (writing - instant propagation)",
                EventType.Discovery);
            return true;
        }

        // Pre-writing era: start oral propagation
        // Check if already propagating this discovery
        if (state.PendingPropagations.Any(p => p.DiscoveryId == discoveryId))
            return true;

        int propagationSimDays = state.HasOralTradition
            ? SimConfig.OralPropagationWithTraditionSimDays
            : SimConfig.OralPropagationSimDays;

        int propagationTicks = propagationSimDays * SimConfig.TicksPerSimDay;

        // Social trait agents reduce propagation time by 40%
        if (HasSocialAgent(settlement, allAgents))
            propagationTicks = Math.Max(1, (int)(propagationTicks * 0.6f));

        // Monument halves propagation time
        if (state.HasMonument)
            propagationTicks = Math.Max(1, propagationTicks / 2);

        state.PendingPropagations.Add(new PropagationEntry
        {
            DiscoveryId = discoveryId,
            DiscovererId = agent.Id,
            StartTick = currentTick,
            DurationTicks = propagationTicks,
            DiscovererAlive = true,
            DiscovererDeathTick = 0
        });

        bus.Emit(currentTick,
            $"{agent.Name}'s discovery of '{discoveryId}' is spreading through {settlement.Name} (oral, {propagationSimDays} sim-day{(propagationSimDays > 1 ? "s" : "")})",
            EventType.Action);

        return true;
    }

    /// <summary>
    /// Called when an explorer returns to their settlement with personal knowledge.
    /// Triggers propagation for any knowledge the settlement doesn't have.
    /// </summary>
    public void OnExplorerReturn(Agent agent, List<Settlement> settlements,
        List<Agent> allAgents, EventBus bus, int currentTick)
    {
        var settlement = FindAgentSettlement(agent, settlements);
        if (settlement == null) return;

        var state = GetOrCreateState(settlement);

        foreach (var knowledge in agent.Knowledge)
        {
            if (state.KnowledgeBase.Contains(knowledge)) continue;
            if (state.PendingPropagations.Any(p => p.DiscoveryId == knowledge)) continue;

            // This is new knowledge for the settlement — start propagation
            OnDiscovery(agent, knowledge, settlements, allAgents, bus, currentTick);

            bus.Emit(currentTick,
                $"{agent.Name} returned to {settlement.Name} with knowledge of '{knowledge}'",
                EventType.Action);
        }
    }

    /// <summary>
    /// Called each tick to advance propagation timers and complete propagations.
    /// Also processes orphaned states (e.g., founding groups that haven't become formal settlements yet).
    /// </summary>
    public void UpdatePropagation(List<Settlement> settlements, List<Agent> allAgents,
        EventBus bus, int currentTick)
    {
        // Track which states are processed via the settlements list
        var processedStateKeys = new HashSet<(int X, int Y)>();

        foreach (var settlement in settlements)
        {
            var state = GetOrCreateState(settlement);
            processedStateKeys.Add((settlement.CenterX, settlement.CenterY));

            // Update cached flags
            state.HasWriting = SettlementHasKnowledge(settlement, allAgents, "writing")
                               || state.KnowledgeBase.Contains("writing");
            state.HasOralTradition = SettlementHasKnowledge(settlement, allAgents, "oral_tradition")
                                     || state.KnowledgeBase.Contains("oral_tradition");
            state.HasMonument = CheckForMonument(settlement, allAgents);

            // Sync settlement knowledge base with any agent knowledge
            // (agents may have discoveries from before the propagation system existed)
            SyncAgentKnowledge(settlement, state, allAgents);

            // Process pending oral propagations
            var completed = new List<PropagationEntry>();

            foreach (var prop in state.PendingPropagations)
            {
                // Check if discoverer is still alive
                if (prop.DiscovererAlive)
                {
                    var discoverer = allAgents.FirstOrDefault(a => a.Id == prop.DiscovererId);
                    if (discoverer == null || !discoverer.IsAlive)
                    {
                        prop.DiscovererAlive = false;
                        prop.DiscovererDeathTick = currentTick;
                    }
                }

                // Check if propagation is complete
                int elapsed = currentTick - prop.StartTick;
                if (elapsed >= prop.DurationTicks)
                {
                    completed.Add(prop);
                }
            }

            foreach (var prop in completed)
            {
                state.PendingPropagations.Remove(prop);

                if (prop.DiscovererAlive)
                {
                    // Full propagation — all residents get it
                    state.KnowledgeBase.Add(prop.DiscoveryId);
                    PropagateToResidents(settlement, prop.DiscoveryId, allAgents);
                    state.OralPropagationCount++;

                    bus.Emit(currentTick,
                        $"Settlement '{settlement.Name}' learned '{prop.DiscoveryId}' through oral tradition",
                        EventType.Discovery);
                }
                else
                {
                    // Discoverer died during propagation — partial knowledge loss
                    // Each resident's chance of retaining = proportion of time survived
                    int survivedTicks = prop.DiscovererDeathTick - prop.StartTick;
                    float retentionChance = (float)survivedTicks / prop.DurationTicks;

                    // oral_tradition reduces loss probability
                    if (state.HasOralTradition)
                        retentionChance = Math.Min(1f, retentionChance + 0.3f);

                    var rng = new Random(currentTick + prop.DiscovererId);
                    int learned = 0;
                    int total = 0;

                    foreach (var agentId in settlement.ResidentAgentIds)
                    {
                        var resident = allAgents.FirstOrDefault(a => a.Id == agentId && a.IsAlive);
                        if (resident == null) continue;
                        if (resident.Knowledge.Contains(prop.DiscoveryId)) continue;

                        total++;
                        if (rng.NextDouble() < retentionChance)
                        {
                            resident.LearnDiscovery(prop.DiscoveryId);
                            learned++;
                        }
                    }

                    if (learned > 0)
                    {
                        state.OralPropagationCount++; // Count partial as still propagated
                        bus.Emit(currentTick,
                            $"Knowledge of '{prop.DiscoveryId}' partially lost in {settlement.Name} — {learned}/{total} residents retained it",
                            EventType.Discovery);
                    }
                    else
                    {
                        bus.Emit(currentTick,
                            $"Knowledge of '{prop.DiscoveryId}' lost in {settlement.Name} — discoverer died before propagation completed",
                            EventType.Death);
                    }
                }

                // Check for oral_tradition auto-trigger
                if (state.OralPropagationCount >= SimConfig.OralTraditionPropagationThreshold
                    && !state.KnowledgeBase.Contains("oral_tradition"))
                {
                    state.KnowledgeBase.Add("oral_tradition");
                    PropagateToResidents(settlement, "oral_tradition", allAgents);
                    state.HasOralTradition = true;

                    bus.Emit(currentTick,
                        $"Settlement '{settlement.Name}' developed oral tradition after {state.OralPropagationCount} shared discoveries!",
                        EventType.Discovery);
                }
            }
        }

        // Process orphaned states — founding groups or former settlements that still have
        // pending propagations but aren't in the current formal settlements list.
        // This ensures knowledge continues spreading even before the first shelter is built.
        // Use wider radius (2x SettlementRadius) since founding group agents may wander
        // before building a shelter, and the state key is the midpoint at creation time.
        int orphanSearchRadius = SimConfig.SettlementRadius * 2;
        foreach (var kvp in _settlementStates)
        {
            if (processedStateKeys.Contains(kvp.Key))
                continue; // Already handled above

            var orphanState = kvp.Value;
            if (orphanState.PendingPropagations.Count == 0)
                continue; // Nothing pending, skip

            // Build a virtual resident list from alive agents near this state's position
            var virtualResidents = new List<int>();
            foreach (var agent in allAgents)
            {
                if (!agent.IsAlive) continue;
                int dist = Math.Max(Math.Abs(agent.X - kvp.Key.X), Math.Abs(agent.Y - kvp.Key.Y));
                if (dist <= orphanSearchRadius)
                    virtualResidents.Add(agent.Id);
            }

            if (virtualResidents.Count == 0)
                continue; // No one nearby to propagate to

            // Process pending propagations for this orphaned state
            var completed = new List<PropagationEntry>();
            foreach (var prop in orphanState.PendingPropagations)
            {
                if (prop.DiscovererAlive)
                {
                    var discoverer = allAgents.FirstOrDefault(a => a.Id == prop.DiscovererId);
                    if (discoverer == null || !discoverer.IsAlive)
                    {
                        prop.DiscovererAlive = false;
                        prop.DiscovererDeathTick = currentTick;
                    }
                }

                int elapsed = currentTick - prop.StartTick;
                if (elapsed >= prop.DurationTicks)
                    completed.Add(prop);
            }

            foreach (var prop in completed)
            {
                orphanState.PendingPropagations.Remove(prop);

                if (prop.DiscovererAlive)
                {
                    orphanState.KnowledgeBase.Add(prop.DiscoveryId);

                    // Propagate to all nearby alive agents
                    foreach (var agentId in virtualResidents)
                    {
                        var resident = allAgents.FirstOrDefault(a => a.Id == agentId && a.IsAlive);
                        if (resident != null && !resident.Knowledge.Contains(prop.DiscoveryId))
                            resident.LearnDiscovery(prop.DiscoveryId);
                    }

                    orphanState.OralPropagationCount++;
                    int elapsedTicks = currentTick - prop.StartTick;
                    bus.Emit(currentTick,
                        $"Founding group learned '{prop.DiscoveryId}' through oral tradition ({elapsedTicks} ticks elapsed)",
                        EventType.Discovery);
                }
                else
                {
                    // Discoverer died — partial retention (same logic as formal settlements)
                    int survivedTicks = prop.DiscovererDeathTick - prop.StartTick;
                    float retentionChance = (float)survivedTicks / prop.DurationTicks;
                    if (orphanState.HasOralTradition)
                        retentionChance = Math.Min(1f, retentionChance + 0.3f);

                    var rng = new Random(currentTick + prop.DiscovererId);
                    foreach (var agentId in virtualResidents)
                    {
                        var resident = allAgents.FirstOrDefault(a => a.Id == agentId && a.IsAlive);
                        if (resident == null || resident.Knowledge.Contains(prop.DiscoveryId)) continue;
                        if (rng.NextDouble() < retentionChance)
                            resident.LearnDiscovery(prop.DiscoveryId);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Called when an agent dies. Checks if they were carrying explorer knowledge
    /// that hasn't been propagated yet.
    /// </summary>
    public void OnAgentDeath(Agent agent, List<Settlement> settlements, EventBus bus, int currentTick)
    {
        // Check if agent had knowledge not in any settlement
        bool hadUniqueKnowledge = false;
        foreach (var knowledge in agent.Knowledge)
        {
            bool knownByAny = false;
            foreach (var settlement in settlements)
            {
                var state = GetOrCreateState(settlement);
                if (state.KnowledgeBase.Contains(knowledge))
                {
                    knownByAny = true;
                    break;
                }
                // Also check if any other resident knows it
                foreach (var resId in settlement.ResidentAgentIds)
                {
                    if (resId == agent.Id) continue;
                    // We don't have direct agent reference here, so we can't check
                    // This will be handled by the propagation entries tracking
                }
            }
            if (!knownByAny)
                hadUniqueKnowledge = true;
        }

        if (hadUniqueKnowledge && agent.Knowledge.Count > 0)
        {
            var settlement = FindAgentSettlement(agent, settlements);
            if (settlement == null)
            {
                // Explorer died outside settlement — knowledge potentially lost
                bus.Emit(currentTick,
                    $"Explorer {agent.Name} died with personal knowledge — discoveries may be lost",
                    EventType.Death);
            }
        }
    }

    /// <summary>
    /// Gets settlement knowledge for a specific settlement position (for child inheritance).
    /// Children inherit settlement knowledge, not individual parent knowledge.
    /// </summary>
    public HashSet<string> GetSettlementKnowledge(List<Settlement> settlements, int agentX, int agentY)
    {
        var settlement = FindSettlementAt(settlements, agentX, agentY);
        if (settlement == null) return new HashSet<string>();

        var state = GetOrCreateState(settlement);
        return new HashSet<string>(state.KnowledgeBase);
    }

    /// <summary>
    /// Returns all knowledge the settlement has — both completed propagations AND
    /// in-progress pending propagations. Used to prevent agents from experimenting
    /// for recipes the settlement already knows or is currently learning.
    /// </summary>
    public HashSet<string> GetAllKnowledgeIncludingPending(List<Settlement> settlements, int agentX, int agentY)
    {
        var settlement = FindSettlementAt(settlements, agentX, agentY);
        if (settlement == null) return new HashSet<string>();

        var state = GetOrCreateState(settlement);
        var combined = new HashSet<string>(state.KnowledgeBase);
        foreach (var pending in state.PendingPropagations)
            combined.Add(pending.DiscoveryId);
        return combined;
    }

    // ── GDD v1.8 Section 5: Geographic Lore ─────────────────────────────

    /// <summary>
    /// Section 5: Adds a geographic discovery to the settlement's permanent lore.
    /// Called when an agent returns home with pending geographic discoveries.
    /// Returns true if this was new knowledge for the settlement.
    /// </summary>
    public bool AddGeographicKnowledge(Agent agent, int x, int y, string featureType,
        ResourceType? resource, List<Settlement> settlements, EventBus bus, int currentTick)
    {
        var settlement = FindAgentSettlement(agent, settlements);
        if (settlement == null) return false;

        var state = GetOrCreateState(settlement);
        var key = (X: x, Y: y);

        // Already known?
        if (state.GeographicLore.ContainsKey(key))
            return false;

        state.GeographicLore[key] = new GeographicEntry
        {
            X = x,
            Y = y,
            FeatureType = featureType,
            Resource = resource,
            DiscoveredTick = currentTick,
            DiscovererName = agent.Name,
            IsDepleted = false
        };

        return true;
    }

    /// <summary>
    /// Section 5: Gets all geographic lore for the settlement an agent belongs to.
    /// Returns empty list if agent is not in a settlement.
    /// </summary>
    public List<GeographicEntry> GetGeographicLore(Agent agent, List<Settlement> settlements)
    {
        var settlement = FindAgentSettlement(agent, settlements);
        if (settlement == null) return new List<GeographicEntry>();

        var state = GetOrCreateState(settlement);
        return state.GeographicLore.Values.ToList();
    }

    /// <summary>
    /// Section 5: Gets geographic lore entries for a specific resource type, excluding depleted ones.
    /// Used by UtilityScorer to find known ore/stone deposits when transient memory has no results.
    /// </summary>
    public List<GeographicEntry> GetGeographicLoreByResource(Agent agent, ResourceType resource,
        List<Settlement> settlements)
    {
        var settlement = FindAgentSettlement(agent, settlements);
        if (settlement == null) return new List<GeographicEntry>();

        var state = GetOrCreateState(settlement);
        return state.GeographicLore.Values
            .Where(e => e.Resource == resource && !e.IsDepleted)
            .ToList();
    }

    /// <summary>
    /// Section 5: Marks a geographic lore entry as depleted. Called when an agent arrives
    /// at a known location and finds the resource gone. The lore remains (it's history)
    /// but agents won't path to it anymore.
    /// </summary>
    public void MarkResourceDepleted(int x, int y, List<Settlement> settlements)
    {
        var key = (X: x, Y: y);
        foreach (var settlement in settlements)
        {
            var state = GetOrCreateState(settlement);
            if (state.GeographicLore.TryGetValue(key, out var entry))
            {
                entry.IsDepleted = true;
            }
        }
    }

    // ── Private Helpers ─────────────────────────────────────────────────

    private SettlementKnowledgeState GetOrCreateState(Settlement settlement)
    {
        var key = (X: settlement.CenterX, Y: settlement.CenterY);

        // Check for existing state within proximity (settlement centers may shift slightly)
        foreach (var kvp in _settlementStates)
        {
            int dist = Math.Max(Math.Abs(kvp.Key.X - key.X), Math.Abs(kvp.Key.Y - key.Y));
            if (dist <= 3)
                return kvp.Value;
        }

        var state = new SettlementKnowledgeState { SettlementId = settlement.Id };
        _settlementStates[key] = state;
        return state;
    }

    private static Settlement? FindAgentSettlement(Agent agent, List<Settlement> settlements)
    {
        // Check by HomeTile first (agents with shelters)
        if (agent.HomeTile.HasValue)
        {
            foreach (var settlement in settlements)
            {
                int dist = Math.Max(
                    Math.Abs(agent.HomeTile.Value.X - settlement.CenterX),
                    Math.Abs(agent.HomeTile.Value.Y - settlement.CenterY));
                if (dist <= SimConfig.SettlementRadius)
                    return settlement;
            }
        }

        // Fallback: check by current position (for agents without HomeTile, e.g. founding pair)
        foreach (var settlement in settlements)
        {
            int dist = Math.Max(
                Math.Abs(agent.X - settlement.CenterX),
                Math.Abs(agent.Y - settlement.CenterY));
            if (dist <= SimConfig.SettlementRadius)
                return settlement;
        }

        return null;
    }

    /// <summary>
    /// Creates a temporary "founding group" settlement when no formal settlement exists.
    /// If 2+ alive agents are within SettlementRadius of the discovering agent, they form
    /// a proto-settlement centered on the midpoint of the group. Using midpoint (instead of
    /// the discoverer's current position) provides a more stable center that doesn't shift
    /// as agents move, preventing orphaned propagation states from stalling.
    /// </summary>
    private static Settlement? TryCreateFoundingGroup(Agent agent, List<Agent> allAgents, int currentTick)
    {
        int radius = SimConfig.SettlementRadius;
        var nearbyAgentObjects = new List<Agent> { agent };

        foreach (var other in allAgents)
        {
            if (other.Id == agent.Id || !other.IsAlive) continue;
            int dist = Math.Max(Math.Abs(other.X - agent.X), Math.Abs(other.Y - agent.Y));
            if (dist <= radius)
                nearbyAgentObjects.Add(other);
        }

        // Need at least 2 agents to form a group
        if (nearbyAgentObjects.Count < 2)
            return null;

        // Use midpoint of all nearby agents for a stable center position
        int midX = (int)nearbyAgentObjects.Average(a => a.X);
        int midY = (int)nearbyAgentObjects.Average(a => a.Y);

        return new Settlement(-1) // Negative ID = founding group (not from SettlementDetector)
        {
            Name = "Founding Group",
            CenterX = midX,
            CenterY = midY,
            ShelterCount = 0,
            ResidentAgentIds = nearbyAgentObjects.Select(a => a.Id).ToList(),
            FoundedTick = currentTick
        };
    }

    private static Settlement? FindSettlementAt(List<Settlement> settlements, int x, int y)
    {
        foreach (var settlement in settlements)
        {
            int dist = Math.Max(Math.Abs(x - settlement.CenterX), Math.Abs(y - settlement.CenterY));
            if (dist <= SimConfig.SettlementRadius)
                return settlement;
        }
        return null;
    }

    private static void PropagateToResidents(Settlement settlement, string discoveryId, List<Agent> allAgents)
    {
        // Directive Fix 5: Propagate to listed residents AND to any alive agent whose
        // HomeTile is within SettlementRadius of the settlement center.
        // This fixes silent propagation failure when agents drift from their settlement
        // (e.g., due to Socialize chase) and aren't in ResidentAgentIds at propagation time.
        var propagated = new HashSet<int>();

        // First pass: listed residents
        foreach (var agentId in settlement.ResidentAgentIds)
        {
            var resident = allAgents.FirstOrDefault(a => a.Id == agentId && a.IsAlive);
            if (resident != null && !resident.Knowledge.Contains(discoveryId))
            {
                resident.LearnDiscovery(discoveryId);
                propagated.Add(agentId);
            }
        }

        // Second pass: agents whose HomeTile is near the settlement center
        foreach (var agent in allAgents)
        {
            if (!agent.IsAlive || propagated.Contains(agent.Id)) continue;
            if (!agent.HomeTile.HasValue) continue;
            if (agent.Knowledge.Contains(discoveryId)) continue;

            int distHome = Math.Max(
                Math.Abs(agent.HomeTile.Value.X - settlement.CenterX),
                Math.Abs(agent.HomeTile.Value.Y - settlement.CenterY));
            if (distHome <= SimConfig.SettlementRadius)
                agent.LearnDiscovery(discoveryId);
        }
    }

    private static bool HasSocialAgent(Settlement settlement, List<Agent> allAgents)
    {
        foreach (var agentId in settlement.ResidentAgentIds)
        {
            var agent = allAgents.FirstOrDefault(a => a.Id == agentId && a.IsAlive);
            if (agent?.Traits != null && agent.Traits.Contains(PersonalityTrait.Social))
                return true;
        }
        return false;
    }

    private static bool SettlementHasKnowledge(Settlement settlement, List<Agent> allAgents, string knowledgeId)
    {
        foreach (var agentId in settlement.ResidentAgentIds)
        {
            var agent = allAgents.FirstOrDefault(a => a.Id == agentId && a.IsAlive);
            if (agent != null && agent.Knowledge.Contains(knowledgeId))
                return true;
        }
        return false;
    }

    private static bool CheckForMonument(Settlement settlement, List<Agent> allAgents)
    {
        // Check if any resident knows "monument" (which means one was built)
        foreach (var agentId in settlement.ResidentAgentIds)
        {
            var agent = allAgents.FirstOrDefault(a => a.Id == agentId && a.IsAlive);
            if (agent != null && agent.Knowledge.Contains("monument"))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Syncs the settlement knowledge base with what agents already know.
    /// This handles the case where agents had knowledge before the propagation
    /// system was added, and ensures the base stays up to date.
    /// </summary>
    private static void SyncAgentKnowledge(Settlement settlement, SettlementKnowledgeState state, List<Agent> allAgents)
    {
        foreach (var agentId in settlement.ResidentAgentIds)
        {
            var agent = allAgents.FirstOrDefault(a => a.Id == agentId && a.IsAlive);
            if (agent == null) continue;

            foreach (var knowledge in agent.Knowledge)
            {
                // If agent knows something the settlement base doesn't, add it
                // (This handles pre-existing knowledge and individual discoveries)
                state.KnowledgeBase.Add(knowledge);
            }
        }
    }
}
