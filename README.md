# CivSim

An emergent civilization simulator where two people are dropped into a procedural world with nothing. They gather resources, discover technology, reproduce, form communities, and build toward civilization across generations. There are no scripted tech trees, no predetermined outcomes — each run is unique.

The simulation answers one question: **if people started from absolute zero, what would happen?**

![.NET 7.0](https://img.shields.io/badge/.NET-7.0-purple)
![Raylib-cs](https://img.shields.io/badge/Raylib--cs-7.0.2-green)
![xUnit](https://img.shields.io/badge/Tests-335-blue)

---

## Table of Contents

- [Getting Started](#getting-started)
- [Controls](#controls)
- [How It Works](#how-it-works)
- [Project Structure](#project-structure)
- [Architecture](#architecture)
- [The World](#the-world)
- [Agents & Behavior](#agents--behavior)
- [Animals & Ecology](#animals--ecology)
- [Technology & Discovery](#technology--discovery)
- [Settlements & Knowledge](#settlements--knowledge)
- [Art & Sprites](#art--sprites)
- [Diagnostics & Testing](#diagnostics--testing)
- [Design Philosophy](#design-philosophy)

---

## Getting Started

### Prerequisites

- [.NET 7.0 SDK](https://dotnet.microsoft.com/download/dotnet/7.0) or later

### Run the Simulation

```bash
dotnet run --project CivSim.Raylib
```

Or use the batch files:

```bash
run.bat          # Interactive — prompts for seed
test.bat         # Quick launch with seed 16001
```

This opens a 1600x900 window with two agents spawned into a procedurally generated world. Watch them survive, discover, build, and grow across generations.

### Run Tests

```bash
dotnet test
```

### Run Headless Diagnostics

```bash
# Single run (1 sim-year, seed 42)
diag.bat

# Batch run (5 seeds, 4 sim-years each)
batch-diag.bat

# Web dashboard on localhost:5000
dashboard.bat
```

---

## Controls

| Key | Action |
|-----|--------|
| **WASD** / **Arrow Keys** | Pan camera |
| **Scroll Wheel** | Zoom in/out |
| **Left Click** | Select agent, animal, or tile |
| **P** / **Space** | Pause / Resume |
| **1-5** | Speed presets (0.8x, 2x, 5x, 10x, 20x) |
| **+** / **-** | Fine speed adjustment |
| **T** | Toggle tech tree view |

---

## How It Works

Two agents — one male, one female — are placed in a procedurally generated world. From there, everything is emergent:

1. **Survival** — Agents forage for berries, gather wood and stone, find shelter from the elements
2. **Discovery** — Through experimentation, agents discover crafting recipes: stone tools, fire, cooking, pottery
3. **Hunting & Animals** — Wild animals roam the world with their own AI. Agents learn to hunt for meat and hide, set traps, and eventually domesticate livestock
4. **Growth** — When conditions are stable enough (food surplus, shelter, manageable workload), agents may choose to have children
5. **Generational Knowledge** — Discoveries belong to the settlement. Children inherit communal knowledge. An explorer who finds a copper vein must return home alive to share the location
6. **Civilization** — Over time, settlements form, technologies compound, and something recognizable as a civilization emerges — or doesn't. A community stuck in the bronze age that trades and builds monuments is a valid outcome

---

## Project Structure

```
CivSim/
├── CivSim.sln                    # Solution file
│
├── CivSim.Core/                   # Simulation engine (library)
│   ├── Agent.cs                   # Agent state: vitals, inventory, knowledge
│   ├── AgentAI.cs                 # Utility-based decision engine
│   ├── Animal.cs                  # Animal entities with state machines
│   ├── AnimalAI.cs                # Animal behavior: wander, flee, graze, sleep
│   ├── AnimalSpecies.cs           # Species definitions and animal states
│   ├── Carcass.cs                 # Harvestable remains from hunting
│   ├── Pen.cs                     # Domestication enclosures
│   ├── Trap.cs                    # Passive animal trapping
│   ├── BehaviorMode.cs            # Six behavioral modes
│   ├── ModeTransitionManager.cs   # Mode evaluation and switching
│   ├── UtilityScorer.cs           # Action desirability scoring
│   ├── Simulation.cs              # Main simulation controller
│   ├── World.cs                   # 64x64 grid, terrain generation, ecology
│   ├── Tile.cs                    # Per-cell resources, structures, biome
│   ├── Recipe.cs                  # 47 discoverable technologies
│   ├── SimplePathfinder.cs        # A* pathfinding with terrain costs
│   ├── Settlement.cs              # Detected settlement clusters
│   ├── SettlementKnowledge.cs     # Communal knowledge propagation
│   ├── SimConfig.cs               # All tuning constants in one place
│   └── Data/                      # Curated name lists (100+ each gender)
│
├── CivSim.Raylib/                 # Visual renderer (executable)
│   ├── Program.cs                 # Window init, main loop
│   ├── RaylibRenderer.cs          # Orchestrator: camera, selection, registries
│   ├── Rendering/
│   │   ├── SpriteAtlas.cs         # Spritesheet + atlas.json loader
│   │   ├── WorldRenderer.cs       # Biome tiles, resources, structures (LOD + frustum culled)
│   │   ├── AgentRenderer.cs       # 12-color tinted agent sprites with move interpolation
│   │   ├── AnimalRenderer.cs      # Animal spritesheets with directional animation
│   │   ├── ResourceSpriteRegistry.cs  # Data-driven resource sprite loading
│   │   ├── StructureRegistry.cs   # Data-driven structure sprite loading
│   │   ├── SpriteBatch.cs         # Batched draw calls for performance
│   │   ├── UIRenderer.cs          # Stats panel, selection detail, event log
│   │   ├── TechTreeRenderer.cs    # Browsable tech tree visualization
│   │   ├── NotificationManager.cs # Discovery banner announcements
│   │   ├── VisualEffectManager.cs # In-world particle effects
│   │   └── ProceduralSprites.cs   # Fallback shapes when sprites unavailable
│   └── Assets/
│       └── Sprites/
│           ├── Animals/           # Per-species directional spritesheets + animals.json
│           ├── Resources/         # Resource sprites + resources.json
│           └── Structures/        # Structure sprites + structures.json
│
├── CivSim.Diagnostics/            # Headless diagnostic runner
│   ├── DiagnosticRunner.cs        # Run simulations without graphics
│   └── ...                        # Specialized diagnostic tools
│
├── CivSim.Tests/                  # Behavioral test suite (335 tests, 47 files)
│   ├── DeathRegressionIntegrationTests.cs  # 5-seed death baseline
│   ├── BehavioralModeTests.cs     # Mode transitions and constraints
│   ├── ReproductionTests.cs       # Breeding mechanics and conditions
│   ├── AnimalEntityTests.cs       # Animal AI and state machines
│   ├── D25cCombatTests.cs         # Hunting and combat
│   ├── D25dDomesticationTests.cs  # Pens, breeding, slaughter
│   └── ...                        # 40+ additional test files
│
├── run.bat                        # Launch interactive simulation (prompts for seed)
├── test.bat                       # Quick launch with seed 16001
├── diag.bat                       # Single diagnostic run
├── batch-diag.bat                 # Batch diagnostic (5 seeds)
└── dashboard.bat                  # Start web dashboard
```

---

## Architecture

### Core Technical Decisions

| System | Approach | Why |
|--------|----------|-----|
| **Event System** | Ring buffer event bus (1024 capacity) | Decouples all systems — no direct calls between subsystems |
| **Spatial Queries** | Incremental spatial index | Add/Remove/Update per tick, never full rebuilds |
| **Agent Perception** | Tiered model | Immediate (1-2 tiles) every tick, full scan periodic, memory fills gaps |
| **Resource Regen** | Event-driven per-tile | Each tile tracks its own timing, no global polling |
| **Pathfinding** | A* with terrain cost weights | Mountains cost 5x, plains 1x — cost map evolves with technology |
| **Knowledge** | Communal per-settlement | No visible agent-to-agent teaching; discoveries propagate organically |
| **Rendering** | LOD + frustum culling + sprite batching | Three detail tiers based on zoom level for smooth performance |
| **Animal AI** | State machine per entity | Wander, Flee, Graze, Sleep — separate RNG stream to avoid cascade |
| **Sprite System** | Data-driven registries | Drop a PNG + add a JSON entry = no code changes needed |

### LOD Rendering

| Zoom Level | Detail |
|------------|--------|
| < 0.35 | Flat colored rectangles only |
| 0.35 - 1.0 | Biome colors + resource dots + animal dots + structures |
| >= 1.0 | Full textures + directional animal sprites + agents + tool icons + health bars |

---

## The World

The world is a **64x64 tile grid** generated with Perlin noise using elevation and moisture maps.

### Biomes

| Biome | Movement | Gathering | Capacity | Character |
|-------|----------|-----------|----------|-----------|
| **Forest** | 1.5x | 1.3x | 20 | Dense wood, berries, animals |
| **Plains** | 1.0x | 1.0x | 15 | Open ground, farming potential, grazing herds |
| **Mountain** | 2.5x | 0.7x | 25 | Stone, ore veins, caves |
| **Desert** | 1.2x | 0.4x | 5 | Sparse and harsh |
| **Water** | Impassable | — | 10 | Fish along shores |

### Resources

| Resource | Source | Notes |
|----------|--------|-------|
| **Wood** | Forest, plains | Building material, fuel |
| **Stone** | Mountain, forest | Tools, construction (non-renewable) |
| **Berries** | Forest, plains | Forageable, edible raw |
| **Grain** | Plains | Requires farming; edible raw, better cooked |
| **Ore** | Mountain | Smelting input — unlocked at higher tech tiers (non-renewable) |
| **Fish** | Water-adjacent | Edible raw or cooked |
| **Meat** | Hunting | Produced by hunting animals |
| **Hide** | Hunting | Crafting material from larger animals |
| **Bone** | Hunting | Crafting material from larger animals |
| **Preserved Food** | Crafting | Dried meat, smoked fish — long-lasting |

Resources exist in natural formations and patches — not uniformly distributed. Depletion is real and drives exploration outward from home. Farm tiles only regenerate grain; non-grain resources are cleared when a farm is established.

---

## Agents & Behavior

Each agent has an identity (name from curated human name lists), vital stats (hunger, health), an inventory, known recipes, and a development stage (Infant -> Youth -> Adult).

### Behavioral Modes

Agents operate in one of **six exclusive behavioral modes** that constrain available actions:

| Mode | When | What They Do |
|------|------|-------------|
| **Home** | At settlement, stable | Eat, rest, craft, experiment, socialize, deposit resources |
| **Forage** | Need resources away from home | Travel to target, gather, return home |
| **Build** | Construction in progress | Build structures, gather materials if short |
| **Explore** | Stable + fed + rested + carrying food | Scout in a committed direction, discover new territory |
| **Caretaker** | Young children nearby | Home tasks + short-range foraging + feeding children |
| **Urgent** | Hunger or health critical | Drop everything — find food, find shelter, rest |

Mode transitions are evaluated **before** goal continuation each tick, preventing stale goals from overriding situational changes.

### Decision Making

Agents use **utility-based AI** with priority levels:

1. **Critical** — Eat if starving, feed a nearby hungry child
2. **Emergency** — Seek food if hungry with none in inventory, rest if injured
3. **Utility-scored** — Tend farms, build, reproduce, experiment, explore, socialize, hunt

Reproduction is a **survival calculus**: agents evaluate food surplus, shelter quality, existing dependents, and workload before deciding to have a child. Two hungry, homeless agents will not reproduce.

---

## Animals & Ecology

Six animal species roam the world with independent AI:

| Species | Biome | Behavior | Hunting Yields |
|---------|-------|----------|---------------|
| **Rabbit** | Forest, plains | Flee (fast) | Meat |
| **Deer** | Forest | Flee | Meat, Hide, Bone |
| **Boar** | Forest | Aggressive when cornered | Meat, Hide, Bone |
| **Wolf** | Forest | Pack predator, aggressive | Hide, Bone |
| **Cow** | Plains | Flee (slow), domesticable | Meat, Hide, Bone |
| **Sheep** | Plains | Flee, domesticable | Meat, Hide |

### Animal AI States

Animals cycle through **Wander** (random movement), **Graze** (eat at current tile), **Flee** (run from threats), and **Sleep** (nighttime rest). Each species has tuned speed, HP, and flee behavior.

### Hunting & Combat

Agents discover hunting through the tech tree. Combat uses weapon-based damage (fists < knife < spear < bow). Animals leave **carcasses** that can be harvested for meat, hide, and bone.

### Domestication

Advanced agents can build **animal pens**, lure animals in, feed them grain, and breed livestock. Domesticated animals provide a renewable food source. Wolf pups can be tamed into dogs that guard the settlement.

### Trapping

Agents can craft and place **traps** that passively catch small animals (rabbits), providing a low-effort food source.

---

## Technology & Discovery

**47 recipes** across 5 branches and 8 eras, from innate knowledge to early civilization:

### Branches

- **Tools & Materials** — Stone knife -> hafted axe -> spear -> bow -> copper tools -> bronze
- **Fire & Heat** — Firemaking -> cooking -> kilns -> smelting
- **Food & Agriculture** — Foraging -> drying racks -> farming -> granaries -> animal domestication
- **Shelter & Construction** — Lean-to -> wattle walls -> stone foundations -> animal pens
- **Knowledge & Culture** — Oral tradition -> pigments -> monuments -> writing

### Discovery Flow

Agents **experiment** at home, combining known knowledge with available resources. Discovery probability depends on prerequisites (what they already know) and available materials. Each step feels earned — fire doesn't lead straight to smelting. Your fire isn't hot enough yet. You need a kiln. Then you need to find the ore. Then you need to figure out what to do with it.

The tech tree is visible and browsable (press **T**). Discovered nodes light up. You can always see where the civilization stands and what's possible next.

---

## Settlements & Knowledge

### Settlement Detection

When 3+ shelters cluster together, a **settlement** is detected and named. Settlements are re-evaluated every 100 ticks.

### Knowledge Propagation

- **Within a settlement**: Communal. When someone discovers something, the settlement knows. No agent-by-agent teaching messages.
- **Oral tradition**: Pre-writing discoveries take 1-2 sim-days to propagate through the settlement.
- **Writing**: Once discovered, knowledge propagates near-instantly and is permanently preserved.
- **Geographic lore**: An explorer who finds a copper vein must **return home alive** to share the location. If they die on the trip, the knowledge is lost. Settlements remember locations across generations — "grandpa told us about the shiny rock in the eastern cliffs."

---

## Art & Sprites

CivSim uses a **data-driven sprite system**. Adding new visuals requires no code changes:

### Adding Sprites

1. Drop a PNG into the appropriate `Assets/Sprites/` subfolder
2. Add an entry to the corresponding JSON registry file
3. The renderer picks it up automatically

### Registries

| Registry | JSON File | Supports |
|----------|-----------|----------|
| **Resources** | `Sprites/Resources/resources.json` | Size, anchor point |
| **Structures** | `Sprites/Structures/structures.json` | Size, anchor point |
| **Animals** | `Sprites/Animals/animals.json` | Directional spritesheets (3 frames x 4 directions), weighted variants |

### Animal Spritesheets

Animal PNGs are directional spritesheets: 3 columns (animation frames) x 4 rows (down, left, right, up). Frame size is auto-detected from image dimensions. Variants (e.g., different deer coat patterns) are weighted randomly per animal instance using deterministic hashing.

### Fallbacks

Every visual element has a **procedural fallback** — geometric shapes and colored dots render when sprites are missing. The simulation is fully playable without any art assets.

---

## Diagnostics & Testing

### Headless Diagnostics

Run simulations without the renderer for analysis:

```bash
dotnet run --project CivSim.Diagnostics -- --ticks 8640 --seed 42 --verbosity summary
```

Outputs CSV logs (agent actions, decisions), HTML reports (population graphs, discovery timelines), and detailed event logs in the `diagnostics/` folder.

### Death Regression Baseline

Five seeds are tracked for death count stability. Any code change that increases deaths on any seed is investigated before merging:

```
Seeds: 42, 1337, 16001, 55555, 99999 (50K ticks each)
```

### Test Suite

335 tests across 47 files covering behavioral modes, survival mechanics, reproduction logic, knowledge propagation, animal AI, combat, domestication, pathfinding, and death regression:

```bash
dotnet test
```

---

## Design Philosophy

CivSim is built around **generational progression** — watching a lineage struggle, learn, build, and pass things forward across lifetimes. It's a contemplative, watchable experience where you form attachment to characters.

**The journey is the content.** The time between "discovered fire" and "figured out clay hardening" is not dead air to skip past — it's 30 minutes of watching agents live their lives, store food, raise children, and slowly push the boundaries of what they know.

A satisfying run looks like: *"I watched for 30 minutes. They found shelter, started storing food, figured out crude tools. I paused and came back the next evening. Their kids are grown now, someone discovered pottery. I'm invested in these people."*

### Core Principles

1. **Agents are settlers, not random walkers** — They have a home. They go out and come back.
2. **Reproduction is earned** — Children come from stability, not timers.
3. **The world has geography** — Rivers shape settlement, mountains are obstacles, ore exists before anyone knows what to do with it.
4. **Knowledge is communal** — Settlements share discoveries. Lost explorers take secrets to their grave.
5. **The tech tree is deep** — Stone knife -> lashed axe -> copper -> bronze. Each step matters.
6. **Stalled civilizations are interesting** — A bronze-age community that builds monuments and trades is a valid history.
7. **Quality over breadth** — Fewer things done well beats many things done poorly.

---

## Time Scale

| Unit | Ticks | Real Meaning |
|------|-------|-------------|
| 1 tick | 1 | ~2 sim-minutes |
| 1 sim-day | 480 | Day/night cycle |
| 1 season | 13,440 | 28 sim-days |
| 1 sim-year | 53,760 | 4 seasons |

---

## License

This is a personal hobby project. All rights reserved.
