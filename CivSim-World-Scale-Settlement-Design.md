# CivSim — World Scale Migration & Settlement System Design Document

**Version:** 1.0
**Date:** March 8, 2026
**Status:** Design complete, awaiting review
**Scope:** World scale change (64×64 → 350×350), settlement entity system, structure placement intelligence, full constant recalibration

---

## Overview

The simulation outgrew its original scale. At 64×64 tiles with each tile representing a "city block" (~300ft), settlements are cramped abstractions where a lean-to and campfire can't coexist on the same tile. Structures are tiny sprites smaller than agents. Farms, pens, and homes all fight for the same single tile.

This design migrates to **350×350 tiles at ~15ft per tile.** A tile is now roughly one tree's footprint, one person's standing space, one section of a fence. Settlements spread across dozens of tiles with visible spatial layout — the lean-to is *here*, the campfire is *there*, the farm is *over there*. Movement within the settlement is visible. The world is about 1 mile across — a full day's journey to traverse.

This also introduces the **Settlement** as a first-class entity in the simulation. Settlements own structures, track members, manage communal shelter quality, and organize spatially through emergent zone placement. Agents live in settlements, not on tiles.

---

## Part 1: World Scale Migration

### The Change

| Property | Current | New |
|---|---|---|
| Grid size | 64×64 (4,096 tiles) | 350×350 (122,500 tiles) |
| Tile real-world size | ~300ft ("city block") | ~15ft (one tree, one person, one fence section) |
| World span | ~3.6 miles | ~1 mile |
| Full map traversal | ~64 ticks (minutes of sim time) | ~350 ticks (~14 hours of sim time, a full day's journey) |

### Time Model (Unchanged)

The existing time model works perfectly at the new scale:

| Unit | Ticks | Unchanged |
|---|---|---|
| 1 tick | 1 | ~2 sim-minutes |
| 1 sim-day | 480 | Day/night cycle |
| 1 season | 13,440 | 28 sim-days |
| 1 sim-year | 53,760 | 4 seasons |

At 1 tile per tick on Plains, an agent covers 480 tiles per full day of walking = ~7,200ft = ~1.4 miles. In practice, agents spend most ticks doing non-movement actions, so realistic daily movement is 100-200 tiles (~1,500-3,000ft). This is natural for a settler working the land.

### Movement (Unchanged Mechanically)

Movement costs stay as ratios. The system doesn't change — agents still move 1 tile per N ticks based on terrain. Since tiles are smaller, the same movement speed covers less real distance per tick, which is correct. Walking through a 15ft forest tile takes the same 1.5 ticks as before; it just represents less ground.

| Terrain | Cost Multiplier | Unchanged |
|---|---|---|
| Plains | 1.0x | Baseline |
| Forest | 1.5x | Trees slow you down |
| Desert | 1.2x | Sand is harder |
| Mountain | 2.5x | Steep terrain |
| Water | Impassable | No change |

### Action Durations (Unchanged)

All action durations are tick-based, not distance-based. Gather (4-8 ticks), Build (24-120 ticks), Experiment (12-24 ticks), Hunt pursuit (2-5 ticks depending on species) — all unchanged.

### What Changes: Distance Constants

Every SimConfig constant that references a tile count as a distance must be recalibrated. The general principle: distances that represented real-world spans need to map to the same real-world span at the new tile size. A "nearby" range of 5 tiles at 300ft/tile = 1,500ft. At 15ft/tile, the equivalent is ~100 tiles. But many ranges should be set to *realistic* distances rather than blind scaling, since the old values were often unrealistically large.

All new values are marked **CALIBRATE LATER** — playtesting will refine them.

---

## Part 2: Terrain-Dependent Perception

### New System

Perception radius now varies by the biome the agent is standing in. Open terrain lets you see further. Dense terrain limits sightlines. This makes biome choice tactically meaningful — plains give you early warning of threats, forests hide you but also hide threats from you.

| Agent Standing In | Perception Radius | Real Distance | Rationale |
|---|---|---|---|
| Plains | 30 tiles | ~450ft | Open land, far sight |
| Desert | 35 tiles | ~525ft | Flat, no obstructions |
| Forest | 12 tiles | ~180ft | Trees block vision |
| Mountain (ground level) | 20 tiles | ~300ft | Rocky terrain limits |
| Mountain (high elevation) | 40 tiles | ~600ft | Elevation advantage — CALIBRATE LATER |
| Water edge | 25 tiles | ~375ft | Open view across water |

### Implementation

When an agent runs Perceive(), the radius is determined by the biome of the agent's current tile. This replaces the flat `SimConfig.PerceptionRadius` with a biome lookup.

For animals, the same system applies — animal detection ranges scale with their current biome. A deer in open plains detects agents from further away than a deer deep in a forest.

### Gameplay Impact

- Exploring through forest is riskier — you can't see wolves until they're close
- Plains give long sightlines — you spot animal herds from far away, plan your hunt
- Mountains reward climbing with extended vision for scouting
- Settlements in plains have natural early warning of approaching threats
- Settlements in forest are hidden but also blind

---

## Part 3: Settlement as an Entity

### What Changes

Currently a "settlement" is a name, a founding date, and not much else. Agents have a home tile but no concept of belonging to a community with shared infrastructure.

The Settlement becomes a first-class simulation entity that owns structures, tracks members, and manages communal resources.

### Settlement Data Model

```
Settlement
├── Id: int
├── Name: string
├── FoundedTick: int
├── CenterTile: (int,int)              // Where the first shelter was built
├── Members: List<int>                  // Agent IDs who live here
├── Structures: List<Structure>         // All structures in this settlement
├── ShelterQuality: ShelterTier         // Best shelter tier (communal)
├── Territory: HashSet<(int,int)>       // Tiles claimed by this settlement
├── Zones: SettlementZones              // Emergent zone tracking
├── SharedKnowledge: HashSet<string>    // Communal knowledge pool
└── Pens: List<Pen>                     // Already exists, moves under Settlement
```

### Communal Shelter

**Core principle: the settlement has shelter, not individual agents.** When someone builds an improved shelter, every member benefits. No one sleeps in a lean-to while a proper house exists in the same settlement.

`ShelterQuality` tracks the best shelter tier built anywhere in the settlement. Reproduction checks, exposure protection, and comfort all reference the settlement's shelter quality, not individual structures.

**Shelter upgrade behavior:**

1. New shelter built on **same tile** as existing shelter → old shelter is **replaced** (demolished, new one takes its place)
2. New shelter built on **different tile**, some agents still assigned to old → old **stays active** as secondary housing
3. New shelter built on **different tile**, all agents in better shelter → old becomes **repurposable** (future: convert to storage, workshop, or demolish for partial material return)

### Settlement Territory

Territory is the set of tiles the settlement claims. It grows organically:

- Every tile containing a settlement structure is territory
- A buffer of tiles around each structure is territory (buffer radius: 3 tiles — CALIBRATE LATER)
- Farm tiles and cleared land are territory
- Territory is recalculated when structures are added or removed

**Territory effects:**
- Animal aggression suppressed within territory (replaces the per-structure deterrent with settlement-wide deterrent)
- Future: other settlements can't build within your territory
- Togglable visual overlay showing territory boundary (off by default, toggled with a hotkey)

### Settlement Membership

Simple for the current scale of the simulation:
- Starting agents found a settlement when they build the first shelter
- Children born in a settlement are automatically members
- Agents can only belong to one settlement
- Multi-settlement dynamics (founding splinter settlements, migration) are future scope but the data model supports them

### Founding Trigger

A settlement is founded when the first shelter structure is completed. `FoundedTick` = the tick the shelter finishes building. This fixes the recurring bug where settlement founding date shows the last tick of the run instead of when the shelter was actually built.

---

## Part 4: Structure Placement Intelligence

### The Problem

At 15ft tiles, a settlement occupies dozens of tiles. Without placement logic, agents will scatter structures randomly across those tiles — farms on top of homes, pens in the middle of fields, shelters built far from campfires. The result would be a bigger mess, not a better settlement.

### Emergent Zone System

Zones are NOT pre-planned. They emerge from placement. When the first farm tile is placed, that area becomes the agricultural zone. Subsequent farms extend from it. The system tracks where things *are*, and new structures prefer to cluster with their type.

```
SettlementZones
├── ResidentialCenter: (int,int)    // Average position of all shelters/campfires
├── AgriculturalCenter: (int,int)   // Average position of all farm tiles
├── AnimalCenter: (int,int)         // Average position of all pens
├── StorageCenter: (int,int)        // Average position of all granaries
└── Recalculate()                   // Update centers when structures change
```

Zone centers are recalculated whenever a structure is added or removed. They're weighted averages, not fixed points.

### Placement Scoring

When an agent decides to build a structure, candidate tiles within build range are scored. The highest-scoring valid tile is selected. Each structure type has placement preferences expressed as scoring bonuses and penalties.

#### Shelter / Improved Shelter
- **+3.0** per tile closer to ResidentialCenter (strong clustering)
- **+1.0** adjacent to existing shelter
- **-2.0** on farm tiles (don't build homes on farms)
- **-2.0** on pen tiles
- **+0.5** Plains biome (prefer flat ground)
- **-0.5** Mountain biome (hard to build on)

#### Campfire
- **+3.0** within 2 tiles of ResidentialCenter
- **-2.0** on farm tiles
- **+1.0** adjacent to shelter

#### Farm
- **+3.0** adjacent to existing farm tiles (extend the block)
- **+2.0** within 5 tiles of AgriculturalCenter
- **-3.0** within 5 tiles of ResidentialCenter (farms away from homes)
- **+1.0** Plains biome
- **-1.0** Forest biome (harder to farm in forest)
- **-5.0** on existing structure tiles (never farm on top of a building)

#### Pen
- **+2.0** adjacent to existing pens
- **+1.5** within 5 tiles of AnimalCenter
- **+1.0** between AgriculturalCenter and ResidentialCenter
- **-3.0** on farm tiles
- **-2.0** on shelter tiles

#### Granary
- **+2.0** between AgriculturalCenter and ResidentialCenter
- **+1.5** within 5 tiles of ResidentialCenter
- **-2.0** on farm tiles

#### Walls (future)
- **+3.0** on settlement territory perimeter tiles
- **+1.0** continuous with existing wall segments

### First Farm Placement

The first farm tile defines where the entire agricultural zone grows. It must be placed intelligently.

**Algorithm:**
1. From the ResidentialCenter, scan 8 directions
2. For each direction, count Plains tiles within 15 tiles
3. Select the direction with the most suitable farmland
4. Place the first farm 8-10 tiles from ResidentialCenter in that direction — CALIBRATE LATER
5. All subsequent farms extend from this first tile via the adjacency scoring

This ensures the farm zone develops in the direction with the best land, naturally separated from the residential area.

### Placement Build Range

Agents can build within **20 tiles** of the settlement center (CALIBRATE LATER). This defines the maximum settlement footprint at ~300ft across. Large enough for a proper homestead with separated zones, small enough that everything is a short walk.

---

## Part 5: Full Constant Recalibration

All values marked **CALIBRATE LATER.** Starting values are reasoned estimates — playtesting will refine them.

### Agent Perception and Awareness

| Constant | Current | New | Notes |
|---|---|---|---|
| Perception radius (Plains) | 8 | 30 | Open terrain, far sight |
| Perception radius (Forest) | 8 | 12 | Trees block vision |
| Perception radius (Mountain ground) | 8 | 20 | Rocky terrain |
| Perception radius (Mountain high) | 8 | 40 | Elevation advantage |
| Perception radius (Desert) | 8 | 35 | Flat, open |
| Perception radius (Water edge) | 8 | 25 | Open across water |
| Memory decay ticks | 50 | 50 | Time-based, unchanged |
| Memory max entries | 30 | 30 | Count-based, unchanged |

### Home and Settlement

| Constant | Current | New | Notes |
|---|---|---|---|
| Spawn cluster radius | 5 | 12 | ~180ft, find each other quickly |
| Structure build range | 5 | 20 | Settlement footprint |
| Shelter nearby (reproduction) | 5 | 15 | Uses settlement membership instead |
| Caretaker range | 2-3 | 10 | Parent watches across homestead |
| Home mode local gather range | 2 | 8 | Grab stuff within settlement |
| Food surplus check radius | 5 | 15 | Settlement area |
| Settlement territory buffer | N/A (new) | 3 | Tiles around structures that count as territory |
| Settlement max build range | N/A (new) | 20 | Max distance from center for new structures |

### Home Pull Strength

| Constant | Current | New | Notes |
|---|---|---|---|
| Home pull formula | HOME_PULL / (1 + dist²) | Same formula | Quadratic falloff unchanged |
| Strong pull range | ~5 tiles | ~50 tiles | Within settlement area |
| Negligible pull range | ~30 tiles | ~150 tiles | Far from home |

### Exploration

| Constant | Current | New | Notes |
|---|---|---|---|
| Explore budget | 300-500 ticks | 300-500 ticks | Time-based, unchanged |
| Explore return-path check | 5 Chebyshev | 15 Chebyshev | More frequent relative to world |
| Explore stuck detection | 3 tiles / 10 ticks | 8 tiles / 10 ticks | Wider for smaller tiles |
| Max distance before return pressure | ~30 | 120 | ~1,800ft, about 1/3 of map |
| Blacklist radius | 3 | 8 | Blacklist an area |
| Direction cooldown multiplier | 0.35x | 0.35x | Unchanged, works well |
| Recent direction memory | 3 trips | 3 trips | Unchanged |

### Foraging

| Constant | Current | New | Notes |
|---|---|---|---|
| Forage commitment distance | 15-20 | 60-80 | Same real-world distance |
| Forage spiral step | 1 tile | 1 tile | Still works |
| Forage give-up distance | ~25 | 100 | Don't forage across entire map |

### Animals

| Constant | Current | New | Notes |
|---|---|---|---|
| Rabbit territory radius | 3 | 8 | ~120ft |
| Deer territory radius | 5 | 15 | ~225ft |
| Cow territory radius | 4 | 12 | ~180ft |
| Sheep territory radius | 4 | 10 | ~150ft |
| Boar territory radius | 4 | 12 | ~180ft |
| Wolf territory radius | 6 | 25 | ~375ft, large patrol area |
| Boar charge range | 2 | 5 | ~75ft |
| Wolf aggression range | 3 | 8 | ~120ft |
| Wolf pack convergence | 5 | 15 | ~225ft |
| Structure/settlement deterrent | 3 | 10 | Settlement is a safe zone |
| Flee detection (deer) | 5 | 15 | ~225ft, bolt early |
| Flee detection (rabbit) | 3 | 6 | ~90ft, notice late |
| Flee detection (cow) | 4 | 10 | ~150ft |
| Flee detection (sheep) | 4 | 12 | ~180ft, flocks are alert |

### Combat

| Constant | Current | New | Notes |
|---|---|---|---|
| Disengage flee distance | 2 | 5 | Get clear of animal |
| All other combat constants | Tick-based | Unchanged | Damage, duration, health — all tick-based |

### Trapping

| Constant | Current | New | Notes |
|---|---|---|---|
| Trap placement range | 8 | 25 | Near settlement but in wild |
| Trap catch radius | 1 (adjacent) | 2-3 | Small area coverage |
| Max traps per agent | 3 | 3 | Count-based, unchanged |
| Trap decay | 200 ticks | 200 ticks | Time-based, unchanged |

### World Gen Resources

Total world resources stay roughly similar. Per-tile density drops proportionally to the tile count increase (~30x more tiles).

| Resource | Current Per-Tile | New Per-Tile | Distribution Notes |
|---|---|---|---|
| Wood (Forest) | 20 | 3-4 | 1 tile ≈ 1 tree. Dense forest = tree on almost every tile. |
| Berries (Forest patch) | 8-10 center, 3-5 edge | 2-3 center, 1 edge | Patches are 15-25 tiles across instead of 3-6 |
| Grain (Plains) | 15 | 2-3 | Spread across more tiles |
| Stone (Mountain) | 15-40 | 2-5 | Varied deposits |
| Stone (Plains/Forest) | 2-3 | 1 | Scattered loose rocks |
| Ore (Mountain, 40%) | 8-15 | 1-3 | Rare veins |
| Fish (Water) | 3-8 | 1-2 | More water tiles at new scale |
| Meat (Cluster tiles) | Per D25b | 1-2 | From hunting, not tile resources |

**Berry patches** at the new scale: clusters of 15-25 forest tiles with berries. Physically larger area (~225-375ft) but lower per-tile density. Visually reads as a spread of berry bushes through a section of forest.

**Animal herds** maintain current herd sizes but territory radii are scaled up. A deer herd roaming a 15-tile radius covers ~225ft of forest — a realistic grazing range.

### Pathfinding

| Constant | Current | New | Notes |
|---|---|---|---|
| A* node budget (simple) | 8,000 | 40,000 | Conservative 5x for 30x more tiles |
| A* node budget (expensive) | 8,000 | 80,000 | Long-distance paths |
| Move fail escalation | 5/10/15/20 | 5/10/15/20 | Tick-based, unchanged |

### Camera and Rendering

| Constant | Current | New | Notes |
|---|---|---|---|
| Tile pixel size | 64px | 64px | Unchanged |
| Default zoom | 1.0x | 0.3x | See more of world at new scale |
| Min zoom | 0.25x | 0.08x | Full 350×350 map visible |
| Max zoom | 4.0x | 4.0x | Close inspection unchanged |
| LOD thresholds | 0.35 / 1.0 / 1.2 | Recalibrate for new zoom range | Sprites at higher zoom, simplified at lower |

---

## Part 6: Implementation Phasing

This is too large for a single directive. Recommended phasing:

### Phase A: Grid Scale + Movement + Camera

**Scope:**
- World grid changes from 64×64 to 350×350
- Biome generation scales to new grid (Perlin noise frequency adjusted)
- Resource distribution recalibrated for new density
- Animal territory and spawn recalibrated
- Camera zoom range and default updated
- LOD thresholds recalibrated
- Movement works at new scale (unchanged mechanically, just more tiles)
- Pathfinding budget increased
- All distance constants in SimConfig updated

**Does NOT include:** Settlement entity, placement scoring, perception changes, zone system.

**Validation:** Agents survive, explore, hunt, reproduce on the larger map. World looks reasonable at new zoom levels. Performance acceptable with 122,500 tiles.

### Phase B: Settlement Entity + Communal Systems

**Scope:**
- Settlement class as first-class entity
- Settlement membership (agents belong to a settlement)
- Communal shelter quality (best shelter = everyone's shelter)
- Settlement territory (tiles claimed, buffer around structures)
- Togglable territory overlay
- Settlement founding trigger (first shelter completion)
- Shelter upgrade logic (replace on same tile, keep if occupied, repurposable if abandoned)
- Reproduction checks settlement shelter instead of raw tile proximity

**Does NOT include:** Placement scoring, zone system.

**Validation:** Settlement entity tracks structures and members correctly. Shelter upgrades are communal. Territory overlay works. Reproduction uses settlement shelter.

### Phase C: Structure Placement Intelligence

**Scope:**
- SettlementZones system (residential, agricultural, animal, storage centers)
- Placement scoring per structure type
- First farm directional placement algorithm
- Farm contiguous extension
- Pen placement relative to farm and residential zones
- Granary placement between farm and home
- Build range from settlement center (20 tiles)

**Validation:** Structures cluster by type. Farms grow as contiguous blocks away from homes. Pens are near farms but not on them. Settlement layout looks organized without scripted blueprints.

### Phase D: Terrain Perception + Polish

**Scope:**
- Biome-dependent perception radius
- Performance profiling and optimization for 122,500 tiles
- Camera improvements for the larger world (minimap? settlement tracking?)
- Any recalibration from Phases A-C playtesting

**Validation:** Agents see further on plains, less in forest. Performance holds at 30+ FPS. Camera navigation feels good on the larger map.

---

## Part 7: Migration Strategy (Avoiding Regression Hell)

### The Risk

Changing the grid from 4,096 to 122,500 tiles touches every system in the simulation. This is the highest-risk change in the project's history — higher than the animal system, higher than D21. Every distance constant, every spatial query, every rendering calculation, every world gen algorithm needs to work at the new scale.

### The Strategy: Run Both Scales

During Phase A implementation, keep the old 64×64 configuration as a fallback. The grid size should be a SimConfig parameter, not hardcoded. If the new scale breaks something, you can switch back to 64×64, verify the regression is scale-related, and fix it without losing all progress.

**SimConfig.WorldWidth** and **SimConfig.WorldHeight** already exist (or should). Change them from 64 to 350. All systems that reference grid dimensions should already use these constants, not hardcoded 64.

### Death Baseline

Standard protocol: capture 5-seed baselines at 64×64 before any changes. After migration to 350×350, the baselines are invalid (completely different world), so the comparison is different — you're not checking "same output" but "agents survive, discover things, and don't die from scale-related bugs." New baselines are established at 350×350 after Phase A.

### Determinism

The simulation must remain deterministic at the new scale. Same seed + same WorldWidth/WorldHeight = identical output. This means all the recalibrated constants must be deterministic (no runtime randomness in distance calculations).

---

## Part 8: Design Principles (Carry Forward)

1. **Tiles are space, not containers.** At 15ft, a tile is a patch of ground — one tree, one fence post, one person standing. Structures *occupy* tiles, they don't *share* tiles. If something doesn't fit, it goes on the next tile.

2. **Settlements are communities, not coordinates.** The settlement entity owns structures, tracks members, and provides communal benefits. An agent lives in a settlement, not on a tile.

3. **Shelter is communal.** The settlement's shelter quality is the best shelter built. Everyone benefits. No grandma in a lean-to while grandkids have a house.

4. **Placement is emergent, not planned.** Zones emerge from where structures are built, guided by proximity scoring. Different seeds produce different settlement layouts because terrain and existing placement affect scoring differently. But every settlement looks organized because same-type structures cluster.

5. **The world is big enough to matter.** A mile-wide map means exploration is a real journey. Resources far from home are genuinely far. The copper deposit on the mountain is a day's walk. That distance creates drama.

6. **Perception is physical.** You see further on plains, less in forest. Biome choice has tactical meaning beyond just resource availability.

7. **Scale doesn't change time.** The tick model, action durations, hunger drain, aging — all unchanged. Only distances change. This minimizes the blast radius of the migration.

---

## Constants Summary (All CALIBRATE LATER)

A full table of every recalibrated constant is in Part 5. All starting values are reasoned estimates. The most sensitive constants (those most likely to need tuning after playtesting) are:

| Constant | Starting Value | Why It's Sensitive |
|---|---|---|
| Perception radius (Plains) | 30 | Too high = agents see everything, too low = blind |
| Perception radius (Forest) | 12 | Defines how dangerous forests feel |
| Home mode local gather range | 8 | Too small = agents starve at home, too big = never forage |
| First farm distance from home | 8-10 tiles | Too close = farms on doorstep, too far = tedious tending |
| Settlement build range | 20 | Defines max settlement size |
| A* node budget | 40,000 / 80,000 | Performance vs pathfinding quality |
| Default camera zoom | 0.3x | Defines first impression of the world |
