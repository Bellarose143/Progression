using CivSim.Core;

namespace CivSim.Diagnostics;

/// <summary>
/// One-shot diagnostic: seed 16001, tick 960, Joshua (Agent 2).
/// Reports full utility scoring pipeline, recipe eligibility, inventory, knowledge.
/// </summary>
public static class JoshuaTick960Diagnostic
{
    public static void Run()
    {
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  JOSHUA TICK 960 DIAGNOSTIC — Seed 16001");
        Console.WriteLine("═══════════════════════════════════════════════════════════════\n");

        var world = new World(64, 64, 16001);
        var sim = new Simulation(world, 16001);
        sim.SpawnAgent();
        sim.SpawnAgent();

        // Fast-forward to tick 1315 (to capture state before tick 1316 decision)
        int targetTick = 1316;
        for (int t = 0; t < targetTick - 1; t++)
            sim.Tick();

        // List all agents first
        Console.WriteLine($"All agents at tick 959 (population={sim.Agents.Count}):");
        for (int i = 0; i < sim.Agents.Count; i++)
        {
            var a = sim.Agents[i];
            Console.WriteLine($"  [{i}] Agent {a.Id} ({a.Name}) pos=({a.X},{a.Y}) mode={a.CurrentMode} action={a.CurrentAction} hunger={a.Hunger:F1} restlessness={a.Restlessness:F1} busy={a.IsBusy}");
        }
        Console.WriteLine();

        // Find Joshua
        var joshua = sim.Agents.FirstOrDefault(a => a.Name == "Joshua");
        if (joshua == null)
        {
            Console.WriteLine("ERROR: No agent named 'Joshua' found at tick 959!");
            Console.WriteLine("Available agent names: " + string.Join(", ", sim.Agents.Select(a => a.Name)));
            return;
        }

        Console.WriteLine($"Agent: {joshua.Name} (ID={joshua.Id})");
        Console.WriteLine($"Position: ({joshua.X}, {joshua.Y})");
        Console.WriteLine($"Hunger: {joshua.Hunger:F1}");
        Console.WriteLine($"Health: {joshua.Health}");
        Console.WriteLine($"Age: {joshua.Age} (BirthTick={joshua.BirthTick})");
        Console.WriteLine($"Restlessness: {joshua.Restlessness:F1}");
        Console.WriteLine($"IsExposed: {joshua.IsExposed}");
        Console.WriteLine($"CurrentMode: {joshua.CurrentMode}");
        Console.WriteLine($"CurrentAction: {joshua.CurrentAction}");
        Console.WriteLine($"CurrentGoal: {joshua.CurrentGoal}");
        Console.WriteLine($"IsBusy: {joshua.IsBusy}");
        Console.WriteLine($"Traits: {string.Join(", ", joshua.Traits)}");
        Console.WriteLine($"LastChosenUtilityAction: {joshua.LastChosenUtilityAction}");
        Console.WriteLine($"ConsecutiveSameActionTicks: {joshua.ConsecutiveSameActionTicks}");
        Console.WriteLine($"LastSettlementDiscoveryTick: {joshua.LastSettlementDiscoveryTick}");
        Console.WriteLine($"HomeTile: {(joshua.HomeTile.HasValue ? $"({joshua.HomeTile.Value.X},{joshua.HomeTile.Value.Y})" : "NONE")}");

        // ── 5. Full Inventory ──
        Console.WriteLine("\n── INVENTORY ──");
        int totalItems = 0;
        foreach (var kvp in joshua.Inventory)
        {
            if (kvp.Value > 0)
            {
                Console.WriteLine($"  {kvp.Key}: {kvp.Value}");
                totalItems += kvp.Value;
            }
        }
        if (totalItems == 0) Console.WriteLine("  (empty)");
        Console.WriteLine($"  Total items: {totalItems} (InventoryCount > 3? {totalItems > 3})");
        Console.WriteLine($"  FoodInInventory: {joshua.FoodInInventory()}");
        Console.WriteLine($"  HasWoodInInventory: {joshua.HasWoodInInventory()}");

        // ── 5. Knowledge Set ──
        Console.WriteLine("\n── KNOWLEDGE ──");
        if (joshua.Knowledge.Count == 0)
            Console.WriteLine("  (none — no discoveries yet)");
        else
            foreach (var k in joshua.Knowledge.OrderBy(k => k))
                Console.WriteLine($"  {k}");
        Console.WriteLine($"  KnowsAnyShelterRecipe: {joshua.KnowsAnyShelterRecipe()}");

        // ── 4. IsNightTime ──
        int currentTick = targetTick;
        int hourOfDay = currentTick % SimConfig.TicksPerSimDay;
        bool isNight = Agent.IsNightTime(currentTick);
        Console.WriteLine($"\n── TIME OF DAY ──");
        Console.WriteLine($"  Tick: {currentTick}");
        Console.WriteLine($"  TicksPerSimDay: {SimConfig.TicksPerSimDay}");
        Console.WriteLine($"  hourOfDay = {currentTick} % {SimConfig.TicksPerSimDay} = {hourOfDay}");
        Console.WriteLine($"  NightStartHour: {SimConfig.NightStartHour}");
        Console.WriteLine($"  NightEndHour: {SimConfig.NightEndHour}");
        Console.WriteLine($"  IsNightTime: hourOfDay >= {SimConfig.NightStartHour} || hourOfDay < {SimConfig.NightEndHour}");
        Console.WriteLine($"  -> {hourOfDay} >= {SimConfig.NightStartHour}? {hourOfDay >= SimConfig.NightStartHour}");
        Console.WriteLine($"  -> {hourOfDay} < {SimConfig.NightEndHour}? {hourOfDay < SimConfig.NightEndHour}");
        Console.WriteLine($"  Result: IsNightTime = {isNight}");

        // ── 2. Recipe Eligibility Analysis ──
        Console.WriteLine("\n── RECIPE ELIGIBILITY (Experiment) ──");
        // Check gates
        bool gateExposedAndKnowsShelter = joshua.IsExposed && joshua.Knowledge.Contains("lean_to");
        Console.WriteLine($"  Gate 1 (exposed + knows lean_to → skip): IsExposed={joshua.IsExposed}, knows lean_to={joshua.Knowledge.Contains("lean_to")} → blocked={gateExposedAndKnowsShelter}");

        bool needsShelterDiscovery = joshua.IsExposed
            && !joshua.Knowledge.Contains("lean_to")
            && !joshua.Knowledge.Contains("improved_shelter");
        float hungerGate = needsShelterDiscovery ? 55f : SimConfig.ExperimentHungerGate;
        Console.WriteLine($"  needsShelterDiscovery: {needsShelterDiscovery}");
        Console.WriteLine($"  Hunger gate: {hungerGate} (ExperimentHungerGate={SimConfig.ExperimentHungerGate})");
        Console.WriteLine($"  Hunger ({joshua.Hunger:F1}) <= {hungerGate}? {joshua.Hunger <= hungerGate} → {(joshua.Hunger <= hungerGate ? "BLOCKED by hunger gate" : "passes hunger gate")}");

        if (!gateExposedAndKnowsShelter && joshua.Hunger > hungerGate)
        {
            // Check each recipe
            Console.WriteLine("\n  Per-recipe analysis:");
            // Get settlement knowledge
            HashSet<string>? settlementKnowledge = null;
            // We don't have direct access to settlement knowledge system, check all recipes manually
            var allRecipes = RecipeRegistry.AllRecipes;
            int eligible = 0;
            foreach (var recipe in allRecipes)
            {
                // Skip innate
                if (recipe.BaseChance <= 0f) continue;

                bool alreadyKnown = joshua.Knowledge.Contains(recipe.Output);
                if (alreadyKnown)
                {
                    Console.WriteLine($"    [{recipe.Id}] SKIP: already known");
                    continue;
                }

                bool hasKnowledge = true;
                string missingKnowledge = "";
                foreach (var req in recipe.RequiredKnowledge)
                {
                    if (!joshua.Knowledge.Contains(req))
                    {
                        hasKnowledge = false;
                        missingKnowledge += (missingKnowledge.Length > 0 ? ", " : "") + req;
                    }
                }

                bool hasResources = true;
                string resourceStatus = "";
                foreach (var kvp in recipe.RequiredResources)
                {
                    int held = joshua.Inventory.GetValueOrDefault(kvp.Key, 0);
                    bool enough = held >= kvp.Value;
                    resourceStatus += $"{kvp.Key}:{held}/{kvp.Value}{(enough ? "✓" : "✗")} ";
                    if (!enough) hasResources = false;
                }

                string status;
                if (!hasKnowledge && !hasResources)
                    status = $"SKIP: missing knowledge [{missingKnowledge}] AND resources [{resourceStatus.Trim()}]";
                else if (!hasKnowledge)
                    status = $"SKIP: missing knowledge [{missingKnowledge}]";
                else if (!hasResources)
                    status = $"SKIP: missing resources [{resourceStatus.Trim()}]";
                else
                {
                    status = $"ELIGIBLE (resources: {resourceStatus.Trim()})";
                    eligible++;
                }

                Console.WriteLine($"    [{recipe.Id}] {status}");
            }
            Console.WriteLine($"\n  Total eligible recipes: {eligible}");
        }
        else
        {
            Console.WriteLine("  Experiment scoring BLOCKED by gate — recipe check skipped");
        }

        // ── 1. Full Scoring Pipeline ──
        Console.WriteLine("\n══════════════════════════════════════════════════════════════");
        Console.WriteLine("  SCORING PIPELINE (tick 960)");
        Console.WriteLine("══════════════════════════════════════════════════════════════");

        // We need to actually run tick 960 with tracing to get the real scoring output
        // But first, let's manually compute what we can

        // Call ScoreHomeActions with trace to capture final results
        var traceLines = new List<string>();
        var random = new Random(16001 + currentTick); // Match sim random seeding

        // We can call ScoreHomeActions directly since it's public static
        var results = UtilityScorer.ScoreHomeActions(joshua, world, currentTick, random,
            sim.Agents, null, null, msg => traceLines.Add(msg));

        Console.WriteLine("\n  Trace output from ScoreHomeActions:");
        foreach (var line in traceLines)
            Console.WriteLine($"    {line}");

        Console.WriteLine($"\n  Final scored actions (after full pipeline, sorted, >0 only):");
        Console.WriteLine($"  Count: {results.Count}");
        for (int i = 0; i < results.Count; i++)
        {
            var sa = results[i];
            Console.WriteLine($"    #{i+1}: {sa.Action,-18} score={sa.Score:F4}" +
                (sa.TargetRecipeId != null ? $"  recipe={sa.TargetRecipeId}" : "") +
                (sa.TargetTile.HasValue ? $"  tile=({sa.TargetTile.Value.X},{sa.TargetTile.Value.Y})" : "") +
                (sa.TargetResource.HasValue ? $"  res={sa.TargetResource}" : ""));
        }

        if (results.Count == 0)
            Console.WriteLine("    >>> NO SCORED ACTIONS — agent will idle!");

        // ── Now let's manually break down the pipeline stages ──
        Console.WriteLine("\n══════════════════════════════════════════════════════════════");
        Console.WriteLine("  PIPELINE STAGE BREAKDOWN");
        Console.WriteLine("══════════════════════════════════════════════════════════════");

        // Stage 1: Raw scores (before any multipliers)
        // We need to call the scorer without multipliers to get raw scores
        // Since the individual methods are private, we'll run ScoreHomeActions on a fresh random
        // and compute the multiplier effects manually

        // Compute what each stage does:

        // Trait multipliers for Joshua's traits
        Console.WriteLine($"\n  Joshua's traits: {string.Join(", ", joshua.Traits)}");
        Console.WriteLine("  Trait multipliers that would apply:");
        foreach (var trait in joshua.Traits)
        {
            Console.WriteLine($"    {trait}:");
            PrintTraitMultipliers(trait);
        }

        // Dampening
        Console.WriteLine($"\n  Dampening:");
        Console.WriteLine($"    LastChosenUtilityAction: {joshua.LastChosenUtilityAction}");
        Console.WriteLine($"    ConsecutiveSameActionTicks: {joshua.ConsecutiveSameActionTicks}");
        if (joshua.LastChosenUtilityAction.HasValue)
        {
            float dampening = MathF.Max(
                SimConfig.ActionDampeningFloor,
                MathF.Pow(SimConfig.ActionDampeningFactor, joshua.ConsecutiveSameActionTicks));
            Console.WriteLine($"    dampening = max({SimConfig.ActionDampeningFloor}, pow({SimConfig.ActionDampeningFactor}, {joshua.ConsecutiveSameActionTicks}))");
            Console.WriteLine($"    dampening = max({SimConfig.ActionDampeningFloor}, {MathF.Pow(SimConfig.ActionDampeningFactor, joshua.ConsecutiveSameActionTicks):F4})");
            Console.WriteLine($"    dampening = {dampening:F4}");
            Console.WriteLine($"    Applied to: {joshua.LastChosenUtilityAction.Value}");
        }
        else
        {
            Console.WriteLine($"    No dampening (no previous action)");
        }

        // Restlessness multiplier
        Console.WriteLine($"\n  Restlessness multiplier:");
        Console.WriteLine($"    Restlessness: {joshua.Restlessness:F1}");
        float r = joshua.Restlessness / 100f;
        Console.WriteLine($"    r = {r:F3}");
        Console.WriteLine($"    Experiment: 1.0 + {r:F3} * {SimConfig.RestlessnessExperimentBoost} = {1.0f + r * SimConfig.RestlessnessExperimentBoost:F3}");
        Console.WriteLine($"    Build:      1.0 + {r:F3} * {SimConfig.RestlessnessBuildBoost} = {1.0f + r * SimConfig.RestlessnessBuildBoost:F3}");
        Console.WriteLine($"    TendFarm:   1.0 + {r:F3} * {SimConfig.RestlessnessTendFarmBoost} = {1.0f + r * SimConfig.RestlessnessTendFarmBoost:F3}");
        Console.WriteLine($"    Gather:     1.0 + {r:F3} * {SimConfig.RestlessnessGatherBoost} = {1.0f + r * SimConfig.RestlessnessGatherBoost:F3}");
        Console.WriteLine($"    Other actions (Rest, Idle, Socialize, etc.): no boost (1.0x)");

        // ── 3. DaytimeIdleGuard analysis ──
        Console.WriteLine($"\n  ApplyDaytimeIdleGuard analysis:");
        Console.WriteLine($"    IsNightTime: {isNight}");
        if (isNight)
        {
            Console.WriteLine($"    → SKIPPED (night time → Rest always valid)");
        }
        else
        {
            Console.WriteLine($"    Health: {joshua.Health} < 30? {joshua.Health < 30}");
            if (joshua.Health < 30)
            {
                Console.WriteLine($"    → SKIPPED (low health → healing rest valid)");
            }
            else
            {
                bool hasProductiveAction = results.Any(sa => sa.Action != ActionType.Rest && sa.Score > 0f);
                Console.WriteLine($"    Any non-Rest action scored > 0? {hasProductiveAction}");
                if (!hasProductiveAction)
                    Console.WriteLine($"    → SKIPPED (no productive actions to protect)");
                else
                    Console.WriteLine($"    → FIRED: Rest scores zeroed out in favor of productive actions");
            }
        }

        // ── Experiment scoring deep dive ──
        Console.WriteLine($"\n  Experiment scoring deep dive:");
        // CalculateFoodSaturation
        int radius = SimConfig.PerceptionRadius;
        int nearbyFood = 0;
        int nearbyPop = 0;
        for (int dx = -radius; dx <= radius; dx++)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                int tx = joshua.X + dx, ty = joshua.Y + dy;
                if (!world.IsInBounds(tx, ty)) continue;
                var tile = world.GetTile(tx, ty);
                foreach (var kvp in tile.Resources)
                {
                    if (kvp.Key == ResourceType.Berries || kvp.Key == ResourceType.Grain
                        || kvp.Key == ResourceType.Meat || kvp.Key == ResourceType.Fish
                        || kvp.Key == ResourceType.PreservedFood)
                        nearbyFood += kvp.Value;
                }
                if (tile.HasGranary)
                    nearbyFood += tile.GranaryTotalFood;
                nearbyPop += world.GetAgentsAt(tx, ty).Count;
            }
        }
        if (nearbyPop <= 0) nearbyPop = 1;
        float foodSaturation = Math.Clamp(nearbyFood / (nearbyPop * 10f), 0f, 1f);
        Console.WriteLine($"    CalculateFoodSaturation:");
        Console.WriteLine($"      PerceptionRadius: {radius}");
        Console.WriteLine($"      nearbyFood: {nearbyFood}");
        Console.WriteLine($"      nearbyPop: {nearbyPop}");
        Console.WriteLine($"      foodSaturation = clamp({nearbyFood} / ({nearbyPop} * 10), 0, 1) = {foodSaturation:F3}");

        bool hasResExp = joshua.InventoryCount() > 3;
        float baseScoreExp = 0.25f * (hasResExp ? 1f : 0.5f) * (0.3f + 0.7f * foodSaturation);
        Console.WriteLine($"    hasResources (InventoryCount > 3): {joshua.InventoryCount()} > 3 = {hasResExp}");
        Console.WriteLine($"    baseScore = 0.25 * {(hasResExp ? "1.0" : "0.5")} * (0.3 + 0.7 * {foodSaturation:F3}) = {baseScoreExp:F4}");

        bool contentBonus = !joshua.IsExposed && joshua.Hunger > 70f && joshua.Health > 70;
        Console.WriteLine($"    Content bonus (!exposed && hunger>70 && health>70): !{joshua.IsExposed} && {joshua.Hunger:F1}>70 && {joshua.Health}>70 = {contentBonus}");
        if (contentBonus) baseScoreExp += 0.10f;

        if (!joshua.IsExposed && joshua.Hunger > 60f)
        {
            int ticksSince = currentTick - joshua.LastSettlementDiscoveryTick;
            float curiosityBonus = Math.Min(ticksSince / 2000f * 0.05f, 0.25f);
            Console.WriteLine($"    Curiosity ramp: ticksSinceLastDiscovery = {currentTick} - {joshua.LastSettlementDiscoveryTick} = {ticksSince}");
            Console.WriteLine($"      bonus = min({ticksSince}/2000 * 0.05, 0.25) = min({ticksSince / 2000f * 0.05f:F4}, 0.25) = {curiosityBonus:F4}");
            baseScoreExp += curiosityBonus;
        }

        bool isAtHome = joshua.HomeTile.HasValue
            && Math.Abs(joshua.X - joshua.HomeTile.Value.X) <= 2
            && Math.Abs(joshua.Y - joshua.HomeTile.Value.Y) <= 2;
        bool isContentExp = joshua.Hunger > 60 && joshua.Health > 70 && !joshua.IsExposed && isAtHome;
        Console.WriteLine($"    isAtHome (within 2 of home): {isAtHome}");
        Console.WriteLine($"    isContent (hunger>60 && health>70 && !exposed && atHome): {isContentExp}");
        if (isContentExp)
        {
            Console.WriteLine($"    Content floor: max({baseScoreExp:F4}, 0.30) = {Math.Max(baseScoreExp, 0.30f):F4}");
            baseScoreExp = Math.Max(baseScoreExp, 0.30f);
        }

        Console.WriteLine($"    FINAL raw Experiment score (before pipeline): {baseScoreExp:F4}");

        // ── CARETAKER-SPECIFIC: childFed gate ──
        Console.WriteLine("\n══════════════════════════════════════════════════════════════");
        Console.WriteLine("  CARETAKER childFed GATE");
        Console.WriteLine("══════════════════════════════════════════════════════════════");
        bool childFed = true;
        foreach (var kvp in joshua.Relationships)
        {
            if (kvp.Value != RelationshipType.Child) continue;
            var child = sim.Agents.FirstOrDefault(a => a.Id == kvp.Key);
            Console.WriteLine($"  Child relationship: AgentId={kvp.Key}");
            if (child != null)
            {
                Console.WriteLine($"    Name: {child.Name}, Alive: {child.IsAlive}, Hunger: {child.Hunger:F1}, Stage: {child.Stage}");
                Console.WriteLine($"    Child hunger < 60? {child.Hunger < 60f}");
                if (child.IsAlive && child.Hunger < 60f)
                    childFed = false;
            }
            else
            {
                Console.WriteLine($"    NOT FOUND in Agents list");
            }
        }
        Console.WriteLine($"  childFed = {childFed}");
        if (!childFed)
        {
            Console.WriteLine("  >>> EXPERIMENT/BUILD/CRAFT WILL BE SUPPRESSED from scored actions!");
            Console.WriteLine("  >>> This is why Joshua idles — caretaker child-hungry gate blocks productive actions.");
        }
        else
        {
            Console.WriteLine("  Child is fed — productive actions allowed.");
        }

        // ── CARETAKER: CaretakerForageRange check ──
        Console.WriteLine($"\n  CaretakerForageRange: {SimConfig.CaretakerForageRange}");
        Console.WriteLine("  Actions with TargetTile outside this range will be filtered out.");

        Console.WriteLine("\n══════════════════════════════════════════════════════════════");
        Console.WriteLine("  DECIDEHOME/CARETAKER PRE-SCORING CHECKS");
        Console.WriteLine("══════════════════════════════════════════════════════════════");

        int food = joshua.FoodInInventory();
        Console.WriteLine($"  FoodInInventory: {food}");
        Console.WriteLine($"  HomeTile: {(joshua.HomeTile.HasValue ? $"({joshua.HomeTile.Value.X},{joshua.HomeTile.Value.Y})" : "NONE")}");
        if (joshua.HomeTile.HasValue)
        {
            int distFromHome = Math.Max(Math.Abs(joshua.X - joshua.HomeTile.Value.X),
                Math.Abs(joshua.Y - joshua.HomeTile.Value.Y));
            Console.WriteLine($"  Distance from home: {distFromHome} (HomeTetherRadius={SimConfig.HomeTetherRadius})");
            Console.WriteLine($"  Would tether fire? {distFromHome > SimConfig.HomeTetherRadius}");
        }

        Console.WriteLine($"\n  Night rest check:");
        Console.WriteLine($"    IsNightTime({currentTick}): {isNight}");
        bool canBuildShelter = joshua.IsExposed
            && joshua.Knowledge.Contains("lean_to")
            && joshua.Inventory.GetValueOrDefault(ResourceType.Wood, 0) >= SimConfig.ShelterWoodCost;
        Console.WriteLine($"    canBuildShelter: {canBuildShelter}");
        Console.WriteLine($"    Would night rest block? {isNight && joshua.Hunger > 40f && !canBuildShelter}");

        Console.WriteLine($"\n  Eat check:");
        Console.WriteLine($"    Hunger <= 65 && food > 0: {joshua.Hunger:F1} <= 65 && {food} > 0 = {joshua.Hunger <= 65f && food > 0}");

        // Also check the tile at Joshua's position
        var joshuaTile = world.GetTile(joshua.X, joshua.Y);
        Console.WriteLine($"\n  Current tile ({joshua.X},{joshua.Y}):");
        Console.WriteLine($"    HasShelter: {joshuaTile.HasShelter}");
        Console.WriteLine($"    HasHomeStorage: {joshuaTile.HasHomeStorage}");
        Console.WriteLine($"    Resources on tile:");
        foreach (var kvp in joshuaTile.Resources)
        {
            if (kvp.Value > 0)
                Console.WriteLine($"      {kvp.Key}: {kvp.Value}");
        }

        // ── Run tick 960 with full tracing ──
        Console.WriteLine("\n══════════════════════════════════════════════════════════════");
        Console.WriteLine("  RUNNING TICK 960 WITH TRACE");
        Console.WriteLine("══════════════════════════════════════════════════════════════");

        // We need to actually run the tick with trace enabled to see what DecideHome does
        var tickTraceLines = new List<string>();
        sim.TraceCallback = msg => tickTraceLines.Add(msg);
        sim.Tick();
        sim.TraceCallback = null;

        Console.WriteLine("\n  Trace output for tick 960:");
        foreach (var line in tickTraceLines.Where(l => l.Contains($"Agent {joshua.Id}")))
            Console.WriteLine($"    {line}");

        Console.WriteLine($"\n  After tick 960:");
        Console.WriteLine($"    CurrentAction: {joshua.CurrentAction}");
        Console.WriteLine($"    CurrentMode: {joshua.CurrentMode}");
        Console.WriteLine($"    CurrentGoal: {joshua.CurrentGoal}");
        Console.WriteLine($"    Hunger: {joshua.Hunger:F1}");
        Console.WriteLine($"    Restlessness: {joshua.Restlessness:F1}");
    }

    public static void ScanForIdleRestless()
    {
        Console.WriteLine("Scanning seed 16001 for Joshua (Agent 1) Idle + Restlessness > 50...\n");
        var world = new World(64, 64, 16001);
        var sim = new Simulation(world, 16001);
        sim.SpawnAgent();
        sim.SpawnAgent();

        int found = 0;
        int firstAt100 = -1;
        for (int t = 0; t < 5000; t++)
        {
            sim.Tick();
            var joshua = sim.Agents[0];
            if (joshua.CurrentAction == ActionType.Idle && joshua.Restlessness > 50)
            {
                int hourOfDay = (t + 1) % SimConfig.TicksPerSimDay;
                bool isNight = Agent.IsNightTime(t + 1);
                int day = (t + 1) / SimConfig.TicksPerSimDay + 1;
                // Log first few, then skip to when restlessness hits 95+
                if (found < 5 || joshua.Restlessness >= 95)
                {
                    Console.WriteLine($"  Tick {t + 1} (Day {day}, hour {hourOfDay}, night={isNight}): Idle, Restlessness={joshua.Restlessness:F1}, Hunger={joshua.Hunger:F1}, Mode={joshua.CurrentMode}");
                }
                if (joshua.Restlessness >= 100 && firstAt100 < 0)
                    firstAt100 = t + 1;
                found++;
            }
        }
        if (found == 0)
            Console.WriteLine("  No Idle + Restlessness>50 found in first 5000 ticks.");
        Console.WriteLine($"\nTotal Idle+Restlessness>50 ticks: {found}");
        Console.WriteLine($"First tick at Restlessness >= 100: {(firstAt100 > 0 ? firstAt100.ToString() : "never in first 5000 ticks")}");
    }

    private static void PrintTraitMultipliers(PersonalityTrait trait)
    {
        var multipliers = trait switch
        {
            PersonalityTrait.Builder => new[] { ("Build/Craft", 1.4f), ("Gather", 1.2f) },
            PersonalityTrait.Explorer => new[] { ("Explore", 1.5f), ("ReturnHome", 0.8f) },
            PersonalityTrait.Social => new[] { ("Socialize", 1.4f), ("ShareFood", 1.4f), ("Reproduce", 1.2f) },
            PersonalityTrait.Cautious => new[] { ("Gather", 1.3f), ("ReturnHome", 1.3f), ("Explore", 0.8f) },
            PersonalityTrait.Curious => new[] { ("Experiment", 1.5f), ("Explore", 1.2f) },
            _ => Array.Empty<(string, float)>()
        };
        foreach (var (action, mult) in multipliers)
            Console.WriteLine($"      {action}: {mult:F1}x");
        if (multipliers.Length == 0)
            Console.WriteLine($"      (no multipliers for this trait)");
    }
}
