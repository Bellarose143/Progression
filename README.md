# CivSim

An emergent civilization simulator where two people are dropped into a procedural world with nothing. They gather resources, discover technology, reproduce, form communities, and build toward civilization across generations. There are no scripted tech trees, no predetermined outcomes — each run is unique.

The simulation answers one question: **if people started from absolute zero, what would happen?**

![.NET 7.0](https://img.shields.io/badge/.NET-7.0-purple)
![Raylib-cs](https://img.shields.io/badge/Raylib--cs-7.0.2-green)
![xUnit](https://img.shields.io/badge/Tests-xUnit-blue)

---

## Table of Contents

- [Getting Started](#getting-started)
- [Controls](#controls)
- [How It Works](#how-it-works)
- [Project Structure](#project-structure)
- [Architecture](#architecture)
- [The World](#the-world)
- [Agents & Behavior](#agents--behavior)
- [Technology & Discovery](#technology--discovery)
- [Settlements & Knowledge](#settlements--knowledge)
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

Or use the batch file:

```bash
run.bat
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
| **Left Click** | Select agent or tile |
| **P** / **Space** | Pause / Resume |
| **1–5** | Speed presets (0.8x, 2x, 5x, 10x, 20x) |
| **+** / **-** | Fine speed adjustment |
| **T** | Toggle tech tree view |

---

## How It Works

Two agents — one male, one female — are placed in a procedurally generated world. From there, everything is emergent:

1. **Survival** — Agents forage for berries, gather wood and stone, find shelter from the elements
2. **Discovery** — Through experimentation, agents discover crafting recipes: stone tools, fire, cooking, pottery
3. **Growth** — When conditions are stable enough (food surplus, shelter, manageable workload), agents may choose to have children
4. **Generational Knowledge** — Discoveries belong to the settlement. Children inherit communal knowledge. An explorer who finds a copper vein must return home alive to share the location
5. **Civilization** — Over time, settlements form, technologies compound, and something recognizable as a civilization emerges — or doesn't. A community stuck in the bronze age that trades and builds monuments is a valid outcome

---

## Project Structure

```
CivSim/
├── CivSim.sln                    # Solution file
│
├── CivSim.Core/                   # Simulation engine (library)
│   ├── Agent.cs                   # Agent state: vitals, inventory, knowledge
│   ├── AgentAI.cs                 # Utility-based decision engine
│   ├── BehaviorMode.cs            # Six behavioral modes
│   ├── ModeTransitionManager.cs   # Mode evaluation and switching
│   ├── UtilityScorer.cs           # Action desirability scoring
│   ├── Simulation.cs              # Main simulation controller
│   ├── World.cs                   # 256x256 grid, terrain generation
│   ├── Tile.cs                    # Per-cell resources, structures, biome
│   ├── Recipe.cs                  # 44 discoverable technologies
│   ├── Settlement.cs              # Detected settlement clusters
│   ├── SettlementKnowledge.cs     # Communal knowledge propagation
│   ├── Events/                    # Event bus architecture
│   │   ├── EventBus.cs            # Ring buffer event dispatch
│   │   ├── SimEvent.cs            # Typed simulation events
│   │   └── EventCategory.cs       # Critical → Notable → Routine → Trace
│   └── Data/                      # Curated name lists (100+ each gender)
│
├── CivSim.Raylib/                 # Visual renderer (executable)
│   ├── Program.cs                 # Window init, main loop
│   ├── RaylibRenderer.cs          # Orchestrator: camera, selection, atlas
│   ├── Rendering/
│   │   ├── SpriteAtlas.cs         # Spritesheet + atlas.json loader
│   │   ├── WorldRenderer.cs       # Biome tiles, resources (frustum culled + LOD)
│   │   ├── AgentRenderer.cs       # 12-color tinted agent sprites
│   │   ├── UIRenderer.cs          # Stats panel, selection detail, event log
│   │   ├── TechTreeRenderer.cs    # Browsable tech tree visualization
│   │   ├── NotificationManager.cs # Discovery banner announcements
│   │   ├── VisualEffectManager.cs # In-world particle effects
│   │   └── ProceduralSprites.cs   # Fallback shapes when atlas unavailable
│   └── Assets/                    # Sprites: biomes, agents, structures, icons
│
├── CivSim.Diagnostics/            # Headless diagnostic runner
│   ├── DiagnosticRunner.cs        # Run simulations without graphics
│   ├── BatchRunner.cs             # Multi-seed batch analysis
│   ├── HtmlDashboardGenerator.cs  # HTML report generation
│   └── DashboardServer.cs         # Live web dashboard (localhost:5000)
│
├── CivSim.Tests/                  # Behavioral test suite (xUnit)
│   ├── BehavioralModeTests.cs     # Mode transitions and constraints
│   ├── SurvivalTests.cs           # Hunger, health, starvation
│   ├── ReproductionTests.cs       # Breeding mechanics and conditions
│   ├── KnowledgeTests.cs          # Discovery and propagation
│   ├── CaretakerModeTests.cs      # Child care logic
│   ├── ExploreEntryTests.cs       # Exploration prerequisites
│   ├── HomePullTests.cs           # Settlement attraction behavior
│   └── ...                        # 13+ test files total
│
├── run.bat                        # Launch interactive simulation
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
| **Rendering** | LOD + frustum culling | Three detail tiers based on zoom level for smooth performance |

### LOD Rendering

| Zoom Level | Detail |
|------------|--------|
| < 0.35 | Flat colored rectangles only |
| 0.35 – 1.0 | Biome colors + resource dots + structures |
| ≥ 1.0 | Full textures + sprites + agents + action icons |

---

## The World

The world is a **256x256 tile grid** (~3.8 km across at ~15m per tile) generated with Perlin noise using elevation and moisture maps.

### Biomes

| Biome | Movement | Gathering | Capacity | Character |
|-------|----------|-----------|----------|-----------|
| **Forest** | 1.5x | 1.3x | 20 | Dense wood, berries, animals |
| **Plains** | 1.0x | 1.0x | 15 | Open ground, farming potential |
| **Mountain** | 2.5x | 0.7x | 25 | Stone, ore veins, caves |
| **Desert** | 1.2x | 0.4x | 5 | Sparse and harsh |
| **Water** | Impassable | — | 10 | Fish along shores |

### Resources

Wood, Stone, Berries, Grain, Animals, Ore, Fish, and Preserved Food. Resources exist in natural formations and patches — not uniformly distributed. Depletion is real and drives exploration outward from home.

---

## Agents & Behavior

Each agent has an identity (name from curated human name lists), vital stats (hunger, health), an inventory, known recipes, and a development stage (Infant → Youth → Adult).

### Behavioral Modes

Agents operate in one of **six exclusive behavioral modes** that constrain available actions:

| Mode | When | What They Do |
|------|------|-------------|
| **Home** | At settlement, stable | Eat, rest, craft, experiment, socialize, deposit resources |
| **Forage** | Need resources away from home | Travel to target, gather, return home |
| **Build** | Construction in progress | Build structures, gather materials if short |
| **Explore** | Stable + fed + rested + carrying food | Scout in a committed direction, opportunistic gathering |
| **Caretaker** | Young children nearby | Home tasks + short-range foraging + feeding children |
| **Urgent** | Hunger or health critical | Drop everything — find food, find shelter, rest |

Mode transitions are evaluated **before** goal continuation each tick, preventing stale goals from overriding situational changes.

### Decision Making

Agents use **utility-based AI** with priority levels:

1. **Critical** — Eat if starving, feed a nearby hungry child
2. **Emergency** — Seek food if hungry with none in inventory, rest if injured
3. **Utility-scored** — Tend farms, build, reproduce, experiment, explore, socialize

Reproduction is a **survival calculus**: agents evaluate food surplus, shelter quality, existing dependents, and workload before deciding to have a child. Two hungry, homeless agents will not reproduce.

---

## Technology & Discovery

**44 recipes** across 5 branches and 8 eras, from innate knowledge to early civilization:

### Branches

- **Tools & Materials** — Stone knife → hafted axe → copper tools → bronze
- **Fire & Heat** — Firemaking → cooking → kilns → smelting
- **Food & Agriculture** — Foraging → drying racks → farming → granaries
- **Shelter & Construction** — Lean-to → wattle walls → stone foundations
- **Knowledge & Culture** — Oral tradition → pigments → monuments → writing

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

## Diagnostics & Testing

### Headless Diagnostics

Run simulations without the renderer for analysis:

```bash
dotnet run --project CivSim.Diagnostics -- --ticks 8640 --seed 42 --verbosity summary
```

Outputs CSV logs (agent actions, decisions), HTML reports (population graphs, discovery timelines), and detailed event logs in the `diagnostics/` folder.

### Batch Analysis

Run multiple seeds to identify patterns:

```bash
dotnet run --project CivSim.Diagnostics -- --seed 1001 --ticks 35040 --verbosity summary
```

### Web Dashboard

```bash
dotnet run --project CivSim.Diagnostics -- --dashboard
```

Opens a live dashboard at `localhost:5000` with run comparisons and metrics.

### Test Suite

13+ test files covering behavioral modes, survival mechanics, reproduction logic, knowledge propagation, caretaker behavior, exploration prerequisites, and decision quality:

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
5. **The tech tree is deep** — Stone knife → lashed axe → copper → bronze. Each step matters.
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
