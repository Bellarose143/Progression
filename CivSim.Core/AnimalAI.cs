namespace CivSim.Core;

/// <summary>
/// D25a: Static animal AI — state machine for movement, herding, fleeing, and sleeping.
/// Called once per tick for each living animal.
/// </summary>
public static class AnimalAI
{
    public static void UpdateAnimal(Animal animal, World world, int currentTick, IReadOnlyList<Agent> agents, Random rng)
    {
        if (!animal.IsAlive) return;
        animal.TicksInState++;
        animal.TicksSinceLastMove++;
        if (animal.FleeCooldown > 0) animal.FleeCooldown--;
        if (animal.AggressiveCooldown > 0) animal.AggressiveCooldown--;

        // D25d: Tame progress decay — if no offering within decay interval, regress
        if (animal.TameProgress > 0 && !animal.IsDomesticated)
        {
            int ticksSinceOffering = currentTick - animal.LastTameOfferingTick;
            if (animal.TameTargetAgentId.HasValue)
            {
                // Agent is taming but hasn't offered in a while
                if (ticksSinceOffering >= SimConfig.TameDecayInterval)
                {
                    // Check if taming agent is still in perception range
                    var tamer = agents.FirstOrDefault(a => a.Id == animal.TameTargetAgentId.Value && a.IsAlive);
                    if (tamer == null)
                    {
                        // Tamer died or doesn't exist — faster decay
                        if (ticksSinceOffering % SimConfig.TameOutOfRangeDecayRate == 0)
                            animal.TameProgress = Math.Max(0, animal.TameProgress - 1);
                    }
                    else
                    {
                        int dist = Math.Max(Math.Abs(tamer.X - animal.X), Math.Abs(tamer.Y - animal.Y));
                        // US-016: Scale detection range by biome
                        var tameTile = world.GetTile(animal.X, animal.Y);
                        int tameDetection = (int)(animal.DetectionRange * SimConfig.GetBiomePerceptionMultiplier(tameTile.Biome));
                        if (dist > tameDetection + 2)
                        {
                            // Out of perception range — faster decay
                            if (ticksSinceOffering % SimConfig.TameOutOfRangeDecayRate == 0)
                                animal.TameProgress = Math.Max(0, animal.TameProgress - 1);
                        }
                        else if (ticksSinceOffering >= SimConfig.TameDecayInterval)
                        {
                            // In range but no offering — slow decay
                            animal.TameProgress = Math.Max(0, animal.TameProgress - 1);
                            animal.LastTameOfferingTick = currentTick; // Reset timer
                        }
                    }
                }
                // If progress decayed to 0, clear taming state
                if (animal.TameProgress <= 0)
                {
                    animal.TameTargetAgentId = null;
                }
            }
        }

        switch (animal.State)
        {
            case AnimalState.Idle:
                UpdateIdle(animal, world, currentTick, agents, rng);
                break;
            case AnimalState.Grazing:
                UpdateGrazing(animal, world, currentTick, agents, rng);
                break;
            case AnimalState.Moving:
                UpdateMoving(animal, world, currentTick, agents, rng);
                break;
            case AnimalState.Fleeing:
                UpdateFleeing(animal, world, currentTick, agents, rng);
                break;
            case AnimalState.Sleeping:
                UpdateSleeping(animal, world, currentTick, agents);
                break;
            // D25c: Aggressive state exists but is NOT used in animal AI loop.
            // Combat is fully agent-driven to preserve RNG determinism.
            // If an animal somehow enters Aggressive, treat as Idle.
            case AnimalState.Aggressive:
                TransitionState(animal, AnimalState.Idle);
                break;
            case AnimalState.Domesticated:
                UpdateDomesticated(animal, world, currentTick, agents, rng);
                break;
        }
    }

    private static bool IsNight(int currentTick)
    {
        int timeOfDay = currentTick % SimConfig.TicksPerSimDay;
        // Night: from NightStartHour to end of day, and from 0 to NightEndHour
        return timeOfDay >= SimConfig.NightStartHour || timeOfDay < SimConfig.NightEndHour;
    }

    /// <summary>
    /// Checks if any agent is within detection range and triggers flee if so.
    /// Returns true if animal transitioned to Fleeing state.
    /// Boar (FleeBehavior.Idle) never flees.
    /// Sleeping animals have halved detection range.
    /// </summary>
    private static bool CheckFleeTrigger(Animal animal, World world, IReadOnlyList<Agent> agents, Random rng)
    {
        // D25d: Domesticated animals don't flee from agents
        if (animal.IsDomesticated) return false;
        // D25d Fix 6: Wolf pups don't flee — too young/curious
        if (animal.IsPup) return false;

        var config = Animal.SpeciesConfig[animal.Species];
        if (config.FleeBehavior == FleeBehavior.Idle) return false;
        // D25c: Wolves that are already aggressive don't flee (aggro overrides flee)
        if (animal.State == AnimalState.Aggressive) return false;
        if (animal.FleeCooldown > 0) return false; // Recently fled, on cooldown

        // US-016: Scale detection range by current tile biome
        var animalTile = world.GetTile(animal.X, animal.Y);
        float biomeMultiplier = SimConfig.GetBiomePerceptionMultiplier(animalTile.Biome);
        int baseDetection = animal.State == AnimalState.Sleeping
            ? animal.DetectionRange / 2
            : animal.DetectionRange;
        int detectionRange = Math.Max(1, (int)(baseDetection * biomeMultiplier));

        Agent? nearest = null;
        int nearestDist = int.MaxValue;
        foreach (var agent in agents)
        {
            if (!agent.IsAlive) continue;
            int dist = Math.Max(Math.Abs(agent.X - animal.X), Math.Abs(agent.Y - animal.Y));
            if (dist <= detectionRange && dist < nearestDist)
            {
                nearest = agent;
                nearestDist = dist;
            }
        }

        if (nearest == null) return false;

        // Calculate flee direction (away from agent)
        int dx = animal.X - nearest.X;
        int dy = animal.Y - nearest.Y;
        // Normalize to -1, 0, 1
        dx = dx == 0 ? 0 : (dx > 0 ? 1 : -1);
        dy = dy == 0 ? 0 : (dy > 0 ? 1 : -1);
        // If on same tile, pick random direction
        if (dx == 0 && dy == 0)
        {
            dx = rng.Next(-1, 2);
            dy = rng.Next(-1, 2);
            if (dx == 0 && dy == 0) dx = 1;
        }

        // Calculate flee target (DetectionRange + 2 tiles away from agent)
        int fleeDistance = detectionRange + 2;
        int targetX = animal.X + dx * fleeDistance;
        int targetY = animal.Y + dy * fleeDistance;

        // Edge-aware fleeing: if flee would push into map edge, redirect toward territory center
        bool wouldHitEdge = targetX <= 1 || targetY <= 1 || targetX >= world.Width - 2 || targetY >= world.Height - 2;
        if (wouldHitEdge)
        {
            // Flee toward territory center instead (always safe)
            int tcDx = animal.TerritoryCenter.X > animal.X ? 1 : (animal.TerritoryCenter.X < animal.X ? -1 : 0);
            int tcDy = animal.TerritoryCenter.Y > animal.Y ? 1 : (animal.TerritoryCenter.Y < animal.Y ? -1 : 0);
            if (tcDx != 0 || tcDy != 0)
            {
                targetX = animal.X + tcDx * fleeDistance;
                targetY = animal.Y + tcDy * fleeDistance;
            }
        }
        // Clamp to world bounds
        targetX = Math.Clamp(targetX, 1, world.Width - 2);
        targetY = Math.Clamp(targetY, 1, world.Height - 2);

        animal.FleeTarget = (targetX, targetY);
        TransitionState(animal, AnimalState.Fleeing);

        // Herd fleeing: trigger all herd members to flee
        if (animal.HerdId >= 0)
        {
            foreach (var herdmate in world.Animals)
            {
                if (herdmate.Id == animal.Id || !herdmate.IsAlive) continue;
                if (herdmate.HerdId != animal.HerdId) continue;
                if (herdmate.State == AnimalState.Fleeing) continue;

                var herdConfig = Animal.SpeciesConfig[herdmate.Species];
                if (herdConfig.FleeBehavior == FleeBehavior.Idle) continue;

                // Flee in same direction (edge-aware)
                int hmTargetX = herdmate.X + dx * fleeDistance;
                int hmTargetY = herdmate.Y + dy * fleeDistance;
                bool hmHitEdge = hmTargetX <= 1 || hmTargetY <= 1 || hmTargetX >= world.Width - 2 || hmTargetY >= world.Height - 2;
                if (hmHitEdge)
                {
                    int tcDx = herdmate.TerritoryCenter.X > herdmate.X ? 1 : (herdmate.TerritoryCenter.X < herdmate.X ? -1 : 0);
                    int tcDy = herdmate.TerritoryCenter.Y > herdmate.Y ? 1 : (herdmate.TerritoryCenter.Y < herdmate.Y ? -1 : 0);
                    if (tcDx != 0 || tcDy != 0)
                    {
                        hmTargetX = herdmate.X + tcDx * fleeDistance;
                        hmTargetY = herdmate.Y + tcDy * fleeDistance;
                    }
                }
                hmTargetX = Math.Clamp(hmTargetX, 1, world.Width - 2);
                hmTargetY = Math.Clamp(hmTargetY, 1, world.Height - 2);
                herdmate.FleeTarget = (hmTargetX, hmTargetY);
                TransitionState(herdmate, AnimalState.Fleeing);
            }
        }

        return true;
    }

    private static bool IsNearMapEdge(Animal animal, World world)
    {
        return animal.X <= 3 || animal.Y <= 3 || animal.X >= world.Width - 4 || animal.Y >= world.Height - 4;
    }

    private static void UpdateIdle(Animal animal, World world, int currentTick, IReadOnlyList<Agent> agents, Random rng)
    {
        if (CheckFleeTrigger(animal, world, agents, rng)) return;
        if (IsNight(currentTick) && animal.State != AnimalState.Sleeping)
        {
            TransitionState(animal, AnimalState.Sleeping);
            return;
        }

        // If near map edge, immediately start moving toward territory center
        if (IsNearMapEdge(animal, world))
        {
            TransitionState(animal, AnimalState.Moving);
            return;
        }

        int idleDuration = 20 + (animal.Id * 7 + currentTick) % 61; // 20-80 deterministic spread
        if (animal.TicksInState >= idleDuration)
        {
            if (rng.NextDouble() < 0.5)
                TransitionState(animal, AnimalState.Grazing);
            else
                TransitionState(animal, AnimalState.Moving);
        }
    }

    private static void UpdateGrazing(Animal animal, World world, int currentTick, IReadOnlyList<Agent> agents, Random rng)
    {
        if (CheckFleeTrigger(animal, world, agents, rng)) return;
        if (IsNight(currentTick))
        {
            TransitionState(animal, AnimalState.Sleeping);
            return;
        }

        // If near map edge, immediately start moving toward territory center
        if (IsNearMapEdge(animal, world))
        {
            TransitionState(animal, AnimalState.Moving);
            return;
        }

        int grazeDuration = 30 + (animal.Id * 13 + currentTick) % 31; // 30-60
        if (animal.TicksInState >= grazeDuration)
        {
            if (rng.NextDouble() < 0.5)
                TransitionState(animal, AnimalState.Idle);
            else
                TransitionState(animal, AnimalState.Moving);
        }
    }

    private static void UpdateMoving(Animal animal, World world, int currentTick, IReadOnlyList<Agent> agents, Random rng)
    {
        if (CheckFleeTrigger(animal, world, agents, rng)) return;
        // Only sleep at night if NOT near map edge (edge animals must keep moving toward center)
        if (IsNight(currentTick) && !IsNearMapEdge(animal, world))
        {
            TransitionState(animal, AnimalState.Sleeping);
            return;
        }

        // Respect MoveSpeed (ticks between moves)
        if (animal.TicksSinceLastMove < animal.MoveSpeed) return;

        // Herd following: if not leader, move toward leader
        Animal? leader = null;
        if (animal.HerdId >= 0)
        {
            foreach (var other in world.Animals)
            {
                if (!other.IsAlive || other.HerdId != animal.HerdId) continue;
                if (leader == null || other.Id < leader.Id)
                    leader = other;
            }
        }

        int targetX, targetY;
        if (leader != null && leader.Id != animal.Id)
        {
            // Follower: move toward leader if >2 tiles away
            int distToLeader = Math.Max(Math.Abs(leader.X - animal.X), Math.Abs(leader.Y - animal.Y));
            if (distToLeader > 2)
            {
                // Move toward leader
                int dx = leader.X > animal.X ? 1 : (leader.X < animal.X ? -1 : 0);
                int dy = leader.Y > animal.Y ? 1 : (leader.Y < animal.Y ? -1 : 0);
                targetX = animal.X + dx;
                targetY = animal.Y + dy;
            }
            else
            {
                // Near leader, random wander
                targetX = animal.X + rng.Next(-1, 2);
                targetY = animal.Y + rng.Next(-1, 2);
            }
        }
        else
        {
            // Leader or solo: random wander within territory
            targetX = animal.X + rng.Next(-1, 2);
            targetY = animal.Y + rng.Next(-1, 2);
        }

        // Edge proximity enforcement: if near map edge, always move toward territory center
        bool nearEdge = animal.X <= 3 || animal.Y <= 3 || animal.X >= world.Width - 4 || animal.Y >= world.Height - 4;
        if (nearEdge)
        {
            targetX = animal.X + (animal.TerritoryCenter.X > animal.X ? 1 : (animal.TerritoryCenter.X < animal.X ? -1 : 0));
            targetY = animal.Y + (animal.TerritoryCenter.Y > animal.Y ? 1 : (animal.TerritoryCenter.Y < animal.Y ? -1 : 0));
        }

        // Territory enforcement: if destination is outside territory, move toward center
        int distFromCenter = Math.Max(
            Math.Abs(targetX - animal.TerritoryCenter.X),
            Math.Abs(targetY - animal.TerritoryCenter.Y));
        if (distFromCenter > animal.TerritoryRadius)
        {
            // Redirect toward territory center
            targetX = animal.X + (animal.TerritoryCenter.X > animal.X ? 1 : (animal.TerritoryCenter.X < animal.X ? -1 : 0));
            targetY = animal.Y + (animal.TerritoryCenter.Y > animal.Y ? 1 : (animal.TerritoryCenter.Y < animal.Y ? -1 : 0));
        }

        // D25a fix: Clamp target to world bounds (prevents edge sticking)
        targetX = Math.Clamp(targetX, 1, world.Width - 2);
        targetY = Math.Clamp(targetY, 1, world.Height - 2);

        // If clamped target is same as current position, redirect toward territory center
        if (targetX == animal.X && targetY == animal.Y)
        {
            targetX = animal.X + (animal.TerritoryCenter.X > animal.X ? 1 : (animal.TerritoryCenter.X < animal.X ? -1 : 0));
            targetY = animal.Y + (animal.TerritoryCenter.Y > animal.Y ? 1 : (animal.TerritoryCenter.Y < animal.Y ? -1 : 0));
            targetX = Math.Clamp(targetX, 1, world.Width - 2);
            targetY = Math.Clamp(targetY, 1, world.Height - 2);
        }

        // Validate target is passable
        if (world.IsInBounds(targetX, targetY))
        {
            var tile = world.GetTile(targetX, targetY);
            if (tile.Biome != BiomeType.Water)
            {
                int oldX = animal.X, oldY = animal.Y;
                animal.X = targetX;
                animal.Y = targetY;
                animal.FacingDirection = (targetX - oldX, targetY - oldY);
                world.UpdateAnimalPosition(animal, oldX, oldY);
                animal.TicksSinceLastMove = 0;
                animal.SetMoveInterpolation(oldX, oldY, currentTick);
            }
        }

        // After a move, transition back to Idle
        TransitionState(animal, AnimalState.Idle);
    }

    private static void UpdateFleeing(Animal animal, World world, int currentTick, IReadOnlyList<Agent> agents, Random rng)
    {
        if (!animal.FleeTarget.HasValue)
        {
            TransitionState(animal, AnimalState.Idle);
            return;
        }

        // Animals flee at their base MoveSpeed (not every tick).
        // This makes Deer (speed 2), Sheep (speed 2), and Cow (speed 3) catchable by agents.
        // Rabbit (speed 1) matches agent speed — requires ambush (being adjacent before flee starts).
        if (animal.TicksSinceLastMove < animal.MoveSpeed)
            return;

        // Check if any agent still in detection range + 2
        // US-016: Scale detection range by biome
        var fleeTile = world.GetTile(animal.X, animal.Y);
        int fleeDetection = (int)(animal.DetectionRange * SimConfig.GetBiomePerceptionMultiplier(fleeTile.Biome));
        bool agentNearby = false;
        int escapeRange = fleeDetection + 2;
        foreach (var agent in agents)
        {
            if (!agent.IsAlive) continue;
            int dist = Math.Max(Math.Abs(agent.X - animal.X), Math.Abs(agent.Y - animal.Y));
            if (dist <= escapeRange)
            {
                agentNearby = true;
                break;
            }
        }

        if (!agentNearby)
        {
            animal.FleeTarget = null;
            animal.FleeCooldown = 30; // Don't re-flee immediately
            TransitionState(animal, AnimalState.Idle);
            return;
        }

        // Move toward flee target
        var target = animal.FleeTarget.Value;
        int dx = target.X > animal.X ? 1 : (target.X < animal.X ? -1 : 0);
        int dy = target.Y > animal.Y ? 1 : (target.Y < animal.Y ? -1 : 0);

        int newX = Math.Clamp(animal.X + dx, 1, world.Width - 2);
        int newY = Math.Clamp(animal.Y + dy, 1, world.Height - 2);

        // Edge escape: if at map edge and can't flee further, redirect toward territory center
        bool atEdge = animal.X <= 2 || animal.Y <= 2 || animal.X >= world.Width - 3 || animal.Y >= world.Height - 3;
        if (atEdge && newX == animal.X && newY == animal.Y)
        {
            // Can't move in flee direction — go toward territory center instead
            int tcDx = animal.TerritoryCenter.X > animal.X ? 1 : (animal.TerritoryCenter.X < animal.X ? -1 : 0);
            int tcDy = animal.TerritoryCenter.Y > animal.Y ? 1 : (animal.TerritoryCenter.Y < animal.Y ? -1 : 0);
            newX = Math.Clamp(animal.X + tcDx, 1, world.Width - 2);
            newY = Math.Clamp(animal.Y + tcDy, 1, world.Height - 2);
        }

        if (world.IsInBounds(newX, newY) && world.GetTile(newX, newY).Biome != BiomeType.Water)
        {
            int oldX = animal.X, oldY = animal.Y;
            animal.X = newX;
            animal.Y = newY;
            animal.FacingDirection = (newX - oldX, newY - oldY);
            world.UpdateAnimalPosition(animal, oldX, oldY);
            animal.TicksSinceLastMove = 0;
            animal.SetMoveInterpolation(oldX, oldY, currentTick);
        }

        // Check if reached flee target or stuck at edge for too long (>50 ticks)
        if ((animal.X == target.X && animal.Y == target.Y) || animal.TicksInState > 50)
        {
            animal.FleeTarget = null;
            animal.FleeCooldown = 60; // Longer cooldown after edge-stuck or completed flee
            TransitionState(animal, AnimalState.Idle);
        }
    }

    private static void UpdateSleeping(Animal animal, World world, int currentTick, IReadOnlyList<Agent> agents)
    {
        if (!IsNight(currentTick))
        {
            TransitionState(animal, AnimalState.Idle);
            return;
        }
        // If near map edge, wake up and move toward territory center
        if (IsNearMapEdge(animal, world))
        {
            TransitionState(animal, AnimalState.Moving);
            return;
        }
        // Can still be woken by very close agents (halved detection range in CheckFleeTrigger)
    }

    // ── D25c: Aggression system ─────────────────────────────────────────

    /// <summary>
    /// D25c: Checks if a Boar or Wolf should aggro on a nearby agent.
    /// Returns true if animal transitioned to Aggressive state.
    /// Structure deterrent prevents aggression near player structures.
    /// </summary>
    /// <summary>D25c: Check if a dangerous animal should aggro on a nearby agent. Called AFTER normal animal AI to avoid RNG cascade.</summary>
    public static bool CheckAggroTrigger(Animal animal, World world, int currentTick, IReadOnlyList<Agent> agents, IReadOnlyList<Settlement>? settlements = null)
    {
        // Only Boar and Wolf can aggro
        if (animal.Species != AnimalSpecies.Boar && animal.Species != AnimalSpecies.Wolf) return false;
        // D25d: Domesticated animals don't aggro
        if (animal.IsDomesticated) return false;
        if (animal.AggressiveCooldown > 0) return false;

        // US-009: Territory deterrent — no aggression within settlement territory
        if (Settlement.IsInAnyTerritory(settlements, animal.X, animal.Y)) return false;

        // Home deterrent (fallback for agents without settlement territory yet)
        foreach (var agent in agents)
        {
            if (!agent.IsAlive || !agent.HomeTile.HasValue) continue;
            int distToHome = Math.Max(Math.Abs(animal.X - agent.HomeTile.Value.X), Math.Abs(animal.Y - agent.HomeTile.Value.Y));
            if (distToHome <= SimConfig.StructureDeterrentRange + 2) return false;
        }

        int aggroRange = animal.Species == AnimalSpecies.Boar ? SimConfig.BoarChargeRange : SimConfig.WolfAggroRange;
        // Night bonus for wolves
        if (animal.Species == AnimalSpecies.Wolf && IsNight(currentTick))
            aggroRange += SimConfig.WolfNightDetectionBonus;

        Agent? nearest = null;
        int nearestDist = int.MaxValue;
        foreach (var agent in agents)
        {
            if (!agent.IsAlive) continue;
            int dist = Math.Max(Math.Abs(agent.X - animal.X), Math.Abs(agent.Y - animal.Y));
            if (dist <= aggroRange && dist < nearestDist)
            {
                nearest = agent;
                nearestDist = dist;
            }
        }

        if (nearest == null) return false;

        animal.AggressiveTargetAgentId = nearest.Id;
        TransitionState(animal, AnimalState.Aggressive);
        return true;
    }

    /// <summary>
    /// D25c: Aggressive state — animal pursues and attacks its target agent.
    /// Boar charges directly; wolves converge as a pack on first tick.
    /// Gives up if target escapes pursuit range or animal strays too far from territory.
    /// </summary>
    private static void UpdateAggressive(Animal animal, World world, int currentTick, IReadOnlyList<Agent> agents, Random rng)
    {
        if (!animal.AggressiveTargetAgentId.HasValue)
        {
            animal.AggressiveCooldown = animal.Species == AnimalSpecies.Boar
                ? SimConfig.BoarDisengageCooldown : SimConfig.WolfDisengageCooldown;
            TransitionState(animal, AnimalState.Idle);
            return;
        }

        var target = agents.FirstOrDefault(a => a.Id == animal.AggressiveTargetAgentId.Value && a.IsAlive);
        if (target == null)
        {
            animal.AggressiveTargetAgentId = null;
            animal.AggressiveCooldown = 10;
            TransitionState(animal, AnimalState.Idle);
            return;
        }

        int dist = Math.Max(Math.Abs(target.X - animal.X), Math.Abs(target.Y - animal.Y));

        // Check pursuit limits
        int maxPursuit = animal.Species == AnimalSpecies.Boar
            ? SimConfig.BoarMaxPursuitFromTerritory : SimConfig.WolfMaxPursuitFromTerritory;
        int distFromTerritory = Math.Max(
            Math.Abs(animal.X - animal.TerritoryCenter.X),
            Math.Abs(animal.Y - animal.TerritoryCenter.Y));

        // Give up if agent is outside pursuit range or animal too far from territory
        if (dist > maxPursuit + 2 || distFromTerritory > maxPursuit)
        {
            animal.AggressiveTargetAgentId = null;
            animal.AggressiveCooldown = animal.Species == AnimalSpecies.Boar
                ? SimConfig.BoarDisengageCooldown : SimConfig.WolfDisengageCooldown;
            TransitionState(animal, AnimalState.Idle);
            return;
        }

        // Move toward target if not adjacent
        if (dist > 1 && animal.TicksSinceLastMove >= animal.MoveSpeed)
        {
            int dx = target.X > animal.X ? 1 : (target.X < animal.X ? -1 : 0);
            int dy = target.Y > animal.Y ? 1 : (target.Y < animal.Y ? -1 : 0);
            int newX = Math.Clamp(animal.X + dx, 1, world.Width - 2);
            int newY = Math.Clamp(animal.Y + dy, 1, world.Height - 2);

            if (world.IsInBounds(newX, newY) && world.GetTile(newX, newY).Biome != BiomeType.Water)
            {
                int oldX = animal.X, oldY = animal.Y;
                animal.X = newX;
                animal.Y = newY;
                animal.FacingDirection = (newX - oldX, newY - oldY);
                world.UpdateAnimalPosition(animal, oldX, oldY);
                animal.TicksSinceLastMove = 0;
                animal.SetMoveInterpolation(oldX, oldY, currentTick);
            }
        }

        // If adjacent, initiate combat on the agent (if not already in combat)
        if (dist <= 1 && !target.IsInCombat)
        {
            target.CombatTargetAnimalId = animal.Id;
            target.CombatTicksRemaining = 30; // Max combat duration safety net
            target.PendingAction = ActionType.Combat;
            target.CurrentAction = ActionType.Combat;
        }

        // Wolf pack convergence: alert nearby pack members on first tick
        if (animal.Species == AnimalSpecies.Wolf && animal.TicksInState == 1)
        {
            foreach (var packmate in world.Animals)
            {
                if (packmate.Id == animal.Id || !packmate.IsAlive) continue;
                if (packmate.HerdId != animal.HerdId || packmate.Species != AnimalSpecies.Wolf) continue;
                if (packmate.State == AnimalState.Aggressive) continue;
                int pmDist = Math.Max(Math.Abs(packmate.X - animal.X), Math.Abs(packmate.Y - animal.Y));
                if (pmDist <= SimConfig.WolfPackConvergeRange)
                {
                    packmate.AggressiveTargetAgentId = target.Id;
                    TransitionState(packmate, AnimalState.Aggressive);
                }
            }
        }
    }

    // ── D25d: Domesticated animal behavior ─────────────────────────────

    private static void UpdateDomesticated(Animal animal, World world, int currentTick, IReadOnlyList<Agent> agents, Random rng)
    {
        // Penned animals stay put — no movement
        if (animal.PenId.HasValue) return;

        // Following owner behavior
        if (!animal.OwnerAgentId.HasValue) return;

        var owner = agents.FirstOrDefault(a => a.Id == animal.OwnerAgentId.Value && a.IsAlive);
        if (owner == null)
        {
            // Owner died — become stray. Territory = owner's last home or current position.
            animal.OwnerAgentId = null;
            // Stay domesticated but act like a wild animal near settlement
            // Territory already set to home tile when tamed
            TransitionState(animal, AnimalState.Idle);
            return;
        }

        // Sleep when owner sleeps
        if (IsNight(currentTick) && !IsNearMapEdge(animal, world))
            return; // Just idle at night

        // Follow owner — if >2 tiles away, move toward owner
        int distToOwner = Math.Max(Math.Abs(owner.X - animal.X), Math.Abs(owner.Y - animal.Y));
        if (distToOwner > 2)
        {
            if (animal.TicksSinceLastMove < animal.MoveSpeed) return;

            int dx = owner.X > animal.X ? 1 : (owner.X < animal.X ? -1 : 0);
            int dy = owner.Y > animal.Y ? 1 : (owner.Y < animal.Y ? -1 : 0);
            int newX = Math.Clamp(animal.X + dx, 1, world.Width - 2);
            int newY = Math.Clamp(animal.Y + dy, 1, world.Height - 2);

            if (world.IsInBounds(newX, newY) && world.GetTile(newX, newY).Biome != BiomeType.Water)
            {
                int oldX = animal.X, oldY = animal.Y;
                animal.X = newX;
                animal.Y = newY;
                animal.FacingDirection = (newX - oldX, newY - oldY);
                world.UpdateAnimalPosition(animal, oldX, oldY);
                animal.TicksSinceLastMove = 0;
                animal.SetMoveInterpolation(oldX, oldY, currentTick);
            }
        }
    }

    private static void TransitionState(Animal animal, AnimalState newState)
    {
        animal.State = newState;
        animal.TicksInState = 0;
    }
}
