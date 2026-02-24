# CivSim v1.8 — Testing Infrastructure Specification

**Date:** February 2026  
**Status:** Approved for implementation  
**Purpose:** Establish automated testing and diagnostic logging to replace manual observation as the primary feedback mechanism.

---

## Layer 1: Run Logger (Black Box Recorder)

### What It Is

A structured log file written during every simulation run. Records agent decisions, state changes, and system events at decision points. Survives after the sim closes. Can be analyzed by humans or AI after the fact.

### File Format

**Format:** CSV (easy to parse, easy to open in Excel, easy to feed to analysis tools)  
**Location:** `CivSim.Raylib/Logs/` directory  
**Filename:** `run_{seed}_{timestamp}.csv`  
**Example:** `run_-230655624_20260222_143052.csv`

### Log Events

The logger writes a row whenever an agent makes a decision, completes an action, or a significant system event occurs. NOT every tick — that would produce enormous files. Only at meaningful state transitions.

#### Event Type 1: Agent Decision

Written when an agent completes an action and selects a new one (the decision point).

| Column | Type | Description |
|--------|------|-------------|
| Tick | int | Current simulation tick |
| SimDay | float | Tick converted to sim-days (tick / 480) for human readability |
| EventType | string | "DECISION" |
| AgentID | int | Agent's unique ID |
| AgentName | string | Agent's name |
| ChosenAction | string | Action selected (e.g., "Gather", "Build", "Experiment", "ReturnHome") |
| ChosenTarget | string | Target detail (e.g., "tile(45,82)", "recipe:lean_to", "agent:Piper") |
| ChosenScore | float | Utility score of the chosen action |
| RunnerUpAction | string | Second-highest scoring action |
| RunnerUpScore | float | Utility score of the runner-up |
| Hunger | float | Agent's current hunger (0-100) |
| Health | float | Agent's current health (0-100) |
| PosX | int | Agent's X position |
| PosY | int | Agent's Y position |
| HomeTileX | int | HomeTile X (-1 if none) |
| HomeTileY | int | HomeTile Y (-1 if none) |
| DistFromHome | int | Manhattan distance from HomeTile (-1 if no home) |
| IsExposed | bool | Whether agent has EXPOSED status |
| InventoryFood | int | Total food items in inventory |
| InventoryWood | int | Wood count |
| InventoryStone | int | Stone count |
| KnowledgeCount | int | Number of known recipes |
| DependentCount | int | Number of living children (via Relationships) |
| IsNight | bool | Whether it's currently night phase |

**Why RunnerUpAction matters:** When you see an agent choose Explore over Build, seeing that Build scored 0.14 and Explore scored 0.15 tells you the scores are too close. If Build scored 0.0, you know materials are missing or a gate is blocking it. The runner-up is the most diagnostic single field in the log.

#### Event Type 2: Action Completion

Written when an action finishes (success or failure).

| Column | Type | Description |
|--------|------|-------------|
| Tick | int | Current tick |
| SimDay | float | Sim-day equivalent |
| EventType | string | "COMPLETE" |
| AgentID | int | Agent ID |
| AgentName | string | Agent name |
| CompletedAction | string | What just finished |
| Result | string | "success", "failed", "interrupted" |
| Detail | string | Context (e.g., "gathered 3 berries", "discovered stone_knife", "no food at target tile", "interrupted by hunger critical") |
| Duration | int | How many ticks the action took |

#### Event Type 3: System Events

Written for significant simulation events (not per-agent).

| Column | Type | Description |
|--------|------|-------------|
| Tick | int | Current tick |
| SimDay | float | Sim-day equivalent |
| EventType | string | "SYSTEM" |
| SubType | string | Event category (see below) |
| Detail | string | Human-readable description |

**System event subtypes:**

| SubType | When | Detail Example |
|---------|------|----------------|
| BIRTH | Agent born | "Child 'Marcus' born to Piper and Nathan at tile(45,82)" |
| DEATH | Agent dies | "Nathan died of starvation at age 16y 2sn. Knowledge: 4. Position: (129,76)" |
| DISCOVERY | Recipe discovered | "Piper discovered 'fire' (attempt #7, base 8%, actual 14%)" |
| PROPAGATION | Knowledge spreads | "Settlement 'Riverbrook' learned 'fire' via oral propagation (elapsed: 840 ticks)" |
| SETTLEMENT | Settlement created/merged | "Settlement 'Riverbrook' founded with 2 shelters at center (47,80)" |
| BUILD | Structure completed | "Piper built lean_to at tile(45,83)" |
| MILESTONE | Milestone discovery | "MILESTONE: 'fire' discovered by Piper at tick 2400" |

### Implementation Notes

- **Logger class:** Singleton `RunLogger` initialized at sim start, flushed and closed at sim end.
- **Performance:** Buffer writes (flush every 100 entries or every 500 ticks, whichever comes first). Decision events are infrequent relative to tick rate — maybe 5-15 per sim-day per agent. With 2 agents, that's 10-30 rows per sim-day. A 2-year run (56 sim-days) produces ~1,000-2,000 rows. Tiny file.
- **Column ordering:** Match the tables above exactly so the format is predictable for analysis tools.
- **Header row:** First line of CSV is column names.
- **Null handling:** Use -1 for missing numeric values, empty string for missing strings. Never leave columns blank.

### Run Summary Block

At the end of the log file (after the last event), append a summary section:

```
# --- RUN SUMMARY ---
# Seed: -230655624
# Duration: 26880 ticks (56 sim-days, 2 sim-years)
# Peak Population: 3
# Deaths: 1 (Marcus, starvation, age 2sn 2d)
# Discoveries: 6 (stone_knife, crude_axe, fire, lean_to, foraging_knowledge, cooking)
# Milestones: 1 (fire)
# Settlements: 1 (Riverbrook)
# Final Population: 2 (Piper age 18y, Nathan age 18y)
```

This lets you glance at the summary without reading the full log.

---

## Layer 2: Behavioral Test Suite

### What It Is

A set of automated tests that run headless simulations (no rendering) with controlled starting conditions and assert that specific behavioral outcomes occur. These catch regressions — if a code change breaks shelter building, the test fails immediately.

### Architecture

- **Test project:** `CivSim.Tests` (new project in the solution, references CivSim core)
- **Framework:** xUnit or NUnit (coder's preference)
- **Each test:** Creates a `Simulation` instance with specific parameters, runs it for N ticks, asserts conditions
- **Execution:** `dotnet test` from command line. Runs all tests. Pass/fail output.
- **Speed:** Headless sims with 2 agents on small grids should run thousands of ticks in under a second.

### Helper Utilities

The test project needs a few helper methods to set up controlled scenarios:

```csharp
// Create a minimal simulation with specific conditions
TestSimBuilder.Create()
    .WithGridSize(32, 32)           // Small grid for fast tests
    .WithBiome(BiomeType.Forest)    // Uniform biome (or mixed)
    .WithAgent("Alice", isMale: false, traits: new[] { Trait.Builder, Trait.Social })
    .WithAgent("Bob", isMale: true, traits: new[] { Trait.Curious, Trait.Explorer })
    .WithResourceAt(5, 5, ResourceType.Wood, quantity: 20)
    .WithResourceAt(8, 8, ResourceType.Berries, quantity: 15)
    .WithKnowledge("Alice", "lean_to")  // Pre-grant knowledge
    .WithShelterAt(10, 10)              // Pre-place structures
    .WithHomeTile("Alice", 10, 10)      // Assign home
    .Build();

// Run simulation for N ticks
sim.RunHeadless(ticks: 5000);

// Query state
var alice = sim.GetAgent("Alice");
var shelterCount = sim.World.CountStructures(StructureType.LeanTo);
```

This builder pattern lets each test set up exactly the conditions it needs without touching rendering code.

### Test Suite

#### Category 1: Survival Priority Tests

**Test 1.1: Exposed Agent Builds Shelter**
```
Setup: 2 agents, forest biome, 20 wood available within 5 tiles, 
       agent 1 pre-granted lean_to knowledge, no shelter exists
Run:   2000 ticks
Assert: At least 1 lean_to exists
Assert: Agent 1 has a HomeTile
Assert: Agent 1 is NOT exposed
Why:    Validates shelter priority when exposed
```

**Test 1.2: Hungry Agent Eats Before Experimenting**
```
Setup: 1 agent, hunger set to 30 (critical), 5 berries in inventory,
       knows stone_knife, has shelter
Run:   50 ticks
Assert: Agent's hunger increased (ate something)
Assert: Agent's action within first 10 ticks was Eat, not Experiment
Why:    Validates P0/P1 hunger priority over P3 experiment
```

**Test 1.3: Starving Agent Searches For Food**
```
Setup: 1 agent, hunger set to 10, no food in inventory, 
       berries at tile (15, 15) — 10 tiles away, agent at (5, 15)
Run:   500 ticks
Assert: Agent's position moved toward (15, 15)
Assert: Agent did NOT stay at starting position for > 50 ticks
Why:    Validates food-seeking movement (catches the "stuck moving" bug)
```

**Test 1.4: Agent Does Not Experiment While Exposed**
```
Setup: 1 agent, exposed (no shelter), has materials, 
       does NOT know lean_to, hunger at 80 (fine)
Run:   1000 ticks
Assert: Agent experimented (trying to discover shelter)
       — this is correct because they don't KNOW lean_to yet
       
Setup variant: Same but agent DOES know lean_to
Run:   500 ticks  
Assert: Agent's first action is Gather (wood) or Build, NOT Experiment
Why:    Validates experiment suppression when exposed + knows shelter
```

#### Category 2: Knowledge Propagation Tests

**Test 2.1: Settlement Propagation Within Spec Time**
```
Setup: 2 agents at same location, shelter exists, both in same settlement
       Agent 1 granted stone_knife at tick 0
Run:   1000 ticks (just over 2 sim-days)
Assert: Agent 2 knows stone_knife
Assert: Propagation completed within 960 ticks (2 sim-days)
Why:    Core v1.8 propagation timing
```

**Test 2.2: Founding Group Propagation (No Shelter)**
```
Setup: 2 agents at same location, NO shelter, no settlement
       Agent 1 granted stone_knife at tick 0
Run:   1000 ticks
Assert: Agent 2 knows stone_knife
Why:    Validates tick-zero founding group fix
```

**Test 2.3: Agent Does Not Experiment For Known Settlement Recipe**
```
Setup: 2 agents, settlement exists, settlement knows stone_knife and fire
       Agent 2 knows stone_knife but NOT fire (fire mid-propagation)
Run:   1000 ticks
Assert: Agent 2 never attempted Experiment for fire
Assert: Agent 2 knows fire (received via propagation)
Why:    Validates experiment filter checks settlement knowledge
```

**Test 2.4: Knowledge Lost If Explorer Dies Before Returning**
```
Setup: 2 agents, settlement with shelter. Agent 1 at home. 
       Agent 2 placed 100 tiles away, granted unique recipe "test_recipe",
       Agent 2 health set to 1 (about to die)
Run:   100 ticks (agent 2 dies)
Assert: Agent 1 does NOT know test_recipe
Assert: Settlement does NOT know test_recipe
Why:    Geographic knowledge friction — explorer must return to share
```

#### Category 3: Home-Pull Tests

**Test 3.1: Agent Stays Near Home**
```
Setup: 2 agents with shelter at (16, 16), resources nearby
Run:   5000 ticks
Track: Agent position every 100 ticks
Assert: Agent within 15 tiles of HomeTile for > 80% of samples
Why:    Core home-pull behavior
```

**Test 3.2: Agent Does Not Build Far From Home**
```
Setup: 1 agent with HomeTile at (16, 16), knows lean_to, has 10 wood
       Agent placed at (40, 40) — far from home
Run:   2000 ticks
Assert: No new shelter built at or near (40, 40)
Assert: Agent moved toward HomeTile
Why:    Build suppression when far from home
```

**Test 3.3: First Shelter Can Be Built Anywhere**
```
Setup: 1 agent, NO HomeTile, knows lean_to, has 5 wood
Run:   1000 ticks
Assert: A lean_to was built somewhere
Assert: Agent now has a HomeTile
Why:    Exception for first-ever shelter (no home to return to)
```

#### Category 4: Parent-Child Tests

**Test 4.1: Parent Feeds Infant**
```
Setup: 2 adults + 1 infant at shared HomeTile, 10 food in home storage,
       parent-child relationships set, infant hunger at 40
Run:   200 ticks
Assert: Infant hunger increased (was fed)
Assert: Infant did NOT die
Why:    Core parent-child feeding behavior
```

**Test 4.2: Parent Returns Home When Child Is Hungry**
```
Setup: 1 adult parent at position (30, 16), HomeTile at (16, 16),
       1 infant at HomeTile, infant hunger at 35, parent has 5 food
Run:   1000 ticks
Assert: Parent's position moved toward HomeTile
Assert: Parent reached HomeTile within 800 ticks
Why:    Parent-child pull across distance
```

**Test 4.3: Infant Does Not Starve With Stocked Home**
```
Setup: 1 infant at HomeTile, 20 food in home storage, no adults present
Run:   2000 ticks
Assert: Infant hunger never hit 0
Assert: Infant is alive
Assert: Home storage decreased (food was consumed)
Why:    Infant auto-feeding from home storage
```

**Test 4.4: Both Parents Do Not Abandon Infant**
```
Setup: 2 adults + 1 infant, shelter, 15 food in home storage
Run:   5000 ticks
Track: Distance of each parent from HomeTile every 100 ticks
Assert: At least one parent within 10 tiles of home for > 90% of samples
Assert: Infant is alive at end
Why:    Parents don't both wander off simultaneously
```

#### Category 5: Decision Quality Tests

**Test 5.1: No Oscillation**
```
Setup: 2 agents, shelter, adequate food nearby
Run:   5000 ticks
Track: Every Move action, record direction
Assert: Direction reversals (N→S, E→W within 5 ticks) < 3% of total moves
Why:    Goal commitment system working
```

**Test 5.2: Night Rest Occurs**
```
Setup: 2 agents, shelter, adequate food
Run:   2400 ticks (5 sim-days)
Track: Rest actions per agent
Assert: Each agent rested at least 3 times in 5 days
Assert: Rest actions predominantly occur during night window (tick 360-480 of sim-day)
Why:    Day/night cycle working
```

**Test 5.3: Content Agent Is Productive**
```
Setup: 2 agents, shelter, 20 food in home storage, hunger 90, health 90
Run:   1000 ticks
Track: All actions taken
Assert: Explore/Move actions < 20% of total actions
Assert: Gather/Experiment/Rest/Build actions > 60% of total actions
Why:    Idle cascade producing useful behavior, not wandering
```

**Test 5.4: Discoveries Change Behavior**
```
Setup: 1 agent, shelter, resources nearby, does NOT know stone_knife
Track: Average gather duration over 5 gather actions
Grant: stone_knife knowledge at tick 1000
Track: Average gather duration over next 5 gather actions
Assert: Post-discovery gather duration < pre-discovery gather duration
Why:    Tech tree multipliers actually applied and observable
```

#### Category 6: Reproduction Tests

**Test 6.1: Reproduction Requires Male-Female Pair**
```
Setup: 2 female agents, shelter, abundant food, hunger 90, health 90
Run:   10000 ticks
Assert: Population still 2 (no reproduction)
Why:    Gender gate enforced
```

**Test 6.2: Reproduction Requires Shelter**
```
Setup: 1 male + 1 female, NO shelter, abundant food, both age 16+
Run:   5000 ticks
Assert: Population still 2
Why:    Shelter hard gate enforced
```

**Test 6.3: Unstable Conditions Suppress Reproduction**
```
Setup: 1 male + 1 female, lean-to shelter, minimal food (hunger 40),
       health 60, both age 16+
Run:   5000 ticks
Assert: Population still 2 (stability score too low)
Why:    Reproduction stability system working
```

**Test 6.4: Stable Conditions Enable Reproduction**
```
Setup: 1 male + 1 female, improved shelter, 30 food in home storage,
       hunger 90, health 95, both age 16+, no existing children
Run:   10000 ticks
Assert: Population > 2 (child born)
Why:    Reproduction happens when conditions are genuinely good
```

### Running the Tests

```bash
# Run all tests
dotnet test CivSim.Tests

# Run a specific category
dotnet test CivSim.Tests --filter "Category=HomePull"

# Run a single test
dotnet test CivSim.Tests --filter "ExposedAgentBuildsShelter"
```

### Test Maintenance Rules

- **Every playtest bug gets a regression test.** If we find a bug in playtesting, a test is written to catch it before the fix is implemented. The test fails, the fix is applied, the test passes. It never regresses.
- **Tests must be fast.** Each test should complete in under 2 seconds. Use small grids (32×32), few agents, and targeted tick counts.
- **Tests must be deterministic.** Use fixed seeds. A test that passes sometimes and fails sometimes is worse than no test.
- **Tests must not depend on rendering.** All tests run headless.

---

## Layer 3: Run Analysis Skill (Ready)

A dedicated analysis skill has been created at `/mnt/skills/user/civsim-run-analyzer/`. It contains:

- **SKILL.md** — Instructions for analyzing a run log CSV and producing a structured diagnostic report
- **behavioral-expectations.md** — 25 behavioral rules organized by category (survival, home behavior, knowledge, parent-child, decision quality, reproduction, technology, timing), each with detection criteria and severity ratings

**How it works:** The user drops a run log CSV into any fresh Claude conversation. The skill reads the log, checks every behavioral rule, and produces a report with: health score, critical issues, behavioral anomalies, positive observations, timeline of key events, per-agent behavior profiles, and prioritized recommendations.

**Coder note:** The skill expects the exact CSV format specified in Layer 1 above. If the log format changes (columns added, renamed, or reordered), the skill's SKILL.md should be updated to match. The behavioral expectations file should also be updated whenever new behavioral rules are added to the simulation (e.g., new priority tiers, new action types, new systems).

**The skill is ready to use immediately** once the run logger is implemented. No further setup needed — just provide the CSV file.

---

## Implementation Order

1. **Run Logger** — Implement the RunLogger class, wire it into decision points and system events. This is the foundation.
2. **Test Project Setup** — Create CivSim.Tests project, add TestSimBuilder helper, verify headless simulation works.
3. **Survival Priority Tests (Category 1)** — These catch the most common failures we've seen.
4. **Knowledge Propagation Tests (Category 2)** — Core v1.8 system validation.
5. **Home-Pull Tests (Category 3)** — Validates the systemic fix just implemented.
6. **Parent-Child Tests (Category 4)** — Validates infant survival.
7. **Decision Quality Tests (Category 5)** — Oscillation, rest cycle, productivity.
8. **Reproduction Tests (Category 6)** — Gate enforcement.

Categories 1-4 are critical. Categories 5-6 are important but slightly lower priority. All should be in place before the next major playtest.

---

*This spec was produced by the senior team member, February 2026. The testing infrastructure is foundational — once in place, every future change is validated automatically. The run logger enables data-driven debugging instead of observation-based guesswork.*
