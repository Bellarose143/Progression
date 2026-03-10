# PRD: World Scale Migration — Phase A

**Feature:** Migrate world grid from 64×64 to 350×350 (~15ft per tile)
**Branch:** feature/world-scale-350
**Spec:** specs/world-scale-phase-a.md
**Priority:** High

---

## Background

CivSim's 64×64 grid at ~300ft per tile makes settlements unworkable. Structures can't coexist on a single tile. This migration changes the grid to 350×350 at ~15ft per tile, giving settlements spatial footprint while keeping the world large enough for exploration (~1 mile across).

The time model, movement mechanics, and action durations are UNCHANGED. Only distance-based constants and world generation scale change.

**Reference the spec file `specs/world-scale-phase-a.md` for all constant values and design rationale.**

---

## User Stories

### Story 1: SimConfig Grid Parameters
**Priority:** 1 (must be first — everything depends on this)

Make WorldWidth and WorldHeight configurable SimConfig parameters if not already. Change defaults from 64 to 350. Verify that all systems reference SimConfig.WorldWidth/WorldHeight instead of hardcoded 64. Grep the entire codebase for hardcoded 64 that refers to grid dimensions and replace with SimConfig references.

**Acceptance Criteria:**
- SimConfig.WorldWidth = 350, SimConfig.WorldHeight = 350
- Zero hardcoded "64" values that refer to grid size remain in codebase
- Changing SimConfig back to 64 produces a working 64×64 world (fallback works)
- Project compiles with zero errors

---

### Story 2: Biome Generation at New Scale
**Priority:** 2

Adjust Perlin/simplex noise frequency so biome generation produces natural-looking biome regions at 350×350. At 64×64, the current noise frequency produces ~4-8 biome regions. At 350×350 with the same frequency, biomes would be enormous. Scale the noise frequency so the world has proportionally similar biome variety — roughly 15-30 distinct biome regions across the map.

**Acceptance Criteria:**
- Generate seed 16001 at 350×350 — world has multiple distinct biome regions (not one giant biome)
- Biome distribution is roughly similar proportionally to 64×64 (mix of Forest, Plains, Mountain, Water, Desert)
- Generate same seed twice — identical biome map (deterministic)
- Visual inspection at min zoom shows recognizable biome patches, not noise

---

### Story 3: Resource Density Recalibration
**Priority:** 3

Recalibrate per-tile resource capacities for the new scale. Total world resources should stay roughly similar to the 64×64 world, but spread across ~30x more tiles at lower per-tile density. See spec for exact values.

Key changes: Wood per forest tile 20→3-4, Berries patch center 8-10→2-3, Grain per plains tile 15→2-3, Mountain stone 15-40→2-5, Ore 8-15→1-3.

Berry patch clustering (from D23) should scale up — patches are now 15-25 tiles across instead of 3-6.

**Acceptance Criteria:**
- Generate seed 16001 at 350×350 — total Wood within ±20% of 64×64 baseline total
- Total Berries, Grain, Stone, Ore all within ±20% of baseline totals
- Berry patches are visibly clustered (not every forest tile has berries)
- No single tile exceeds its biome's new capacity limits
- Resource census logged for validation

---

### Story 4: Animal Spawn and Territory Recalibration
**Priority:** 4

Recalibrate animal territory radii, spawn density, and detection ranges for the new tile scale. See spec for exact values. Herds maintain current sizes but territory radii increase (e.g., deer territory 5→15 tiles, wolf territory 6→25 tiles).

Spawn density should produce roughly similar total animal counts as the 64×64 world.

**Acceptance Criteria:**
- Animals spawn on 350×350 map with species-appropriate biome placement
- Territory radii updated per spec values
- Flee detection ranges updated per spec values
- Total animal count within ±25% of 64×64 baseline
- Herds move within their scaled territory without getting stuck
- Boar/wolf aggression ranges updated per spec

---

### Story 5: Pathfinding Budget Increase
**Priority:** 5

Increase A* node budgets for the larger grid. Simple budget 8,000→40,000, expensive budget 8,000→80,000. The escalation thresholds (MoveFailCount 5/10/15/20) stay unchanged since they're tick-based.

**Acceptance Criteria:**
- Agents pathfind successfully across the 350×350 map
- No pathfinding failures on paths that would have succeeded at 64×64 scale
- A* doesn't cause frame drops (profile if concerned — but 40K nodes should be fine)

---

### Story 6: Camera Zoom and LOD Recalibration
**Priority:** 6

Update camera defaults for the larger world. Default zoom 1.0→0.3x so the player sees a reasonable area on first load. Min zoom 0.25→0.08x so the full 350×350 map is visible. Max zoom stays at 4.0x.

Recalibrate LOD thresholds for the new zoom range — sprites should render at higher zoom levels, simplified dots/shapes at lower. Adjust so the visual quality transition feels natural across the new zoom range.

**Acceptance Criteria:**
- Default zoom shows a meaningful area (settlement + surrounding terrain)
- Min zoom shows the full 350×350 world
- Max zoom shows close-up detail same as before
- LOD transitions are smooth — no abrupt pop-in of sprites
- Agent names/labels readable at appropriate zoom levels

---

### Story 7: Settlement and Home Distance Constants
**Priority:** 7

Recalibrate all settlement and home-related distance constants per the spec. This includes: spawn cluster radius 5→12, structure build range 5→15, shelter nearby for reproduction 5→15, caretaker range 2-3→10, home mode gather range 2→8, food surplus check 5→15.

Also recalibrate home pull strength — the quadratic falloff formula stays the same but the distance thresholds need updating so pull is strong within ~50 tiles and fades by ~150 tiles.

**Acceptance Criteria:**
- Agents spawn within 12 tiles of each other
- Agents build structures within 15 tiles of home
- Caretaker parent stays within 10 tiles of child
- Home mode gathering works within 8 tiles
- Home pull feels natural — agents return reliably but aren't yanked home from short trips

---

### Story 8: Exploration Distance Constants
**Priority:** 8

Recalibrate exploration constants per spec. Return-path check 5→15 Chebyshev, stuck detection 3→8 tiles over 10 ticks, max distance before return pressure ~30→120, blacklist radius 3→8.

Explore budget (300-500 ticks) stays unchanged — it's time-based.

**Acceptance Criteria:**
- Agents explore outward from home across the larger map
- Return-path check fires at appropriate intervals (every 15 Chebyshev tiles)
- Stuck detection catches oscillation at new scale
- Agents feel return pressure at ~120 tiles from home
- No explore corner-trapping (D24 fix still works at new scale)

---

### Story 9: Foraging Distance Constants
**Priority:** 9

Recalibrate foraging constants per spec. Forage commitment distance 15-20→60-80, forage give-up distance ~25→100.

**Acceptance Criteria:**
- Agents forage within reasonable range from home
- Agents don't forage across the entire map
- Foraging trips feel proportional to the new world size
- Agents find food within foraging range on most seeds

---

### Story 10: Combat and Trapping Distance Constants
**Priority:** 10

Recalibrate combat constants: boar charge range 2→5, wolf aggression range 3→8, wolf pack convergence 5→15, settlement deterrent 3→10, disengage flee distance 2→5.

Recalibrate trapping constants: trap placement range 8→25, trap catch radius 1→2-3.

All damage/duration/health values are tick-based and stay unchanged.

**Acceptance Criteria:**
- Boar charges at 5 tiles (not 2)
- Wolf aggression triggers at 8 tiles
- Wolf pack converges from within 15 tiles
- Settlement deterrent keeps animals passive within 10 tiles of structures
- Traps can be placed within 25 tiles of home
- Combat damage and duration unchanged

---

### Story 11: Terrain-Dependent Perception
**Priority:** 11

Replace the flat perception radius with biome-dependent radius. Agent.Perceive() checks the agent's current tile biome and uses the appropriate radius: Plains 30, Desert 35, Forest 12, Mountain ground 20, Mountain high 40, Water edge 25.

Same system applies to animal detection ranges — animals in forests detect agents from closer range than animals on plains.

**Acceptance Criteria:**
- Agent on Plains tile perceives 30 tiles radius
- Agent on Forest tile perceives 12 tiles radius
- Agent moving from Forest to Plains gains perception immediately
- Animal detection ranges scale with their biome
- Verify with diagnostic: agent perception events logged with radius used

---

### Story 12: Survival Validation at New Scale
**Priority:** 12 (must be last — validates everything)

Run all 5 standard seeds (42, 1337, 16001, 55555, 99999) at 50,000 ticks on the 350×350 world. Establish new baselines. Agents must survive, discover recipes, reproduce, hunt, and build shelter at the new scale.

**Acceptance Criteria:**
- All 5 seeds run without crashes
- Zero starvation deaths on at least 3/5 seeds (new world may be harder — some deaths acceptable while calibrating)
- At least 1 shelter built on each seed
- At least 5 discoveries per seed
- At least 1 hunt event per seed
- Resource census shows reasonable totals (not depleted, not overflowing)
- Performance: 30+ FPS at default zoom with 122,500 tiles
- Determinism: same seed twice produces identical output
- New baselines saved to progress.txt for future reference

---

### Story 13: Performance Profiling
**Priority:** 13

Profile tick execution time at 350×350 with ~100 animals and 3 agents. Identify any bottlenecks from the 30x tile increase. Key systems to profile: A* pathfinding, perception scanning, animal AI tick, resource regeneration, rendering at various zoom levels.

If any system causes frame drops below 30 FPS, optimize it. Common suspects: perception scanning O(tiles in radius) with larger radius, A* with higher node budget, rendering 122K tiles at low zoom.

**Acceptance Criteria:**
- Tick time profiled and logged
- 30+ FPS maintained at all zoom levels
- Any optimizations documented in progress.txt
- If optimization needed, no behavioral changes — only performance improvements
