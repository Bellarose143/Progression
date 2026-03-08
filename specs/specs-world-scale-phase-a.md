# Spec: World Scale Migration — Phase A

**Feature:** Grid scale change from 64×64 to 350×350 with full constant recalibration
**Status:** Approved
**Design Doc:** CivSim-World-Scale-Settlement-Design.md (covers all 4 phases)
**This Phase:** Phase A — Grid scale, movement, camera, resource distribution, perception

---

## Context

CivSim's 64×64 grid at ~300ft per tile makes settlements unworkable — structures can't coexist on a single tile. The migration to 350×350 at ~15ft per tile gives settlements spatial footprint while keeping the world large enough for meaningful exploration (~1 mile across, full day's journey to traverse).

## Time Model (Unchanged)

- 1 tick = ~2 sim-minutes
- 480 ticks = 1 sim-day
- 13,440 ticks = 1 season (28 days)
- 53,760 ticks = 1 sim-year
- Agent moves 1 tile/tick on Plains. 480 tiles/day pure walking = ~7,200ft = 1.4 miles.

## Movement (Unchanged Mechanically)

Terrain cost multipliers stay the same: Plains 1.0x, Forest 1.5x, Desert 1.2x, Mountain 2.5x, Water impassable. Agents still move 1 tile per N ticks. Tiles are just smaller.

## Terrain-Dependent Perception (New)

Perception radius varies by the biome the agent stands in:

| Biome | Radius (tiles) | Real Distance |
|---|---|---|
| Plains | 30 | ~450ft |
| Desert | 35 | ~525ft |
| Forest | 12 | ~180ft |
| Mountain (ground) | 20 | ~300ft |
| Mountain (high) | 40 | ~600ft |
| Water edge | 25 | ~375ft |

Implementation: Agent.Perceive() looks up current tile biome and uses biome-specific radius instead of flat SimConfig.PerceptionRadius. Same system applies to animal detection ranges.

## Resource Distribution at New Scale

Total world resources stay roughly similar to current. Per-tile density drops since there are ~30x more tiles.

| Resource | Old Per-Tile Cap | New Per-Tile Cap | Notes |
|---|---|---|---|
| Wood (Forest) | 20 | 3-4 | 1 tile ≈ 1 tree footprint |
| Berries (patch center) | 8-10 | 2-3 | Patches are 15-25 tiles across |
| Berries (patch edge) | 3-5 | 1 | Lower density at edges |
| Grain (Plains) | 15 | 2-3 | Spread across more tiles |
| Stone (Mountain) | 15-40 | 2-5 | Varied deposits |
| Stone (Plains/Forest scatter) | 2-3 | 1 | Loose rocks |
| Ore (Mountain, 40% of tiles) | 8-15 | 1-3 | Rare veins |
| Fish (Water) | 3-8 | 1-2 | More water tiles |

Berry patches at new scale: clusters of 15-25 forest tiles. Animal herds maintain current sizes but territory radii scale up.

## Distance Constant Recalibration

All values are CALIBRATE LATER. Starting points based on design conversation:

### Settlement & Home
| Constant | Old | New |
|---|---|---|
| Spawn cluster radius | 5 | 12 |
| Structure build range | 5 | 15 |
| Shelter nearby (repro) | 5 | 15 |
| Caretaker range | 2-3 | 10 |
| Home mode gather range | 2 | 8 |
| Food surplus check | 5 | 15 |

### Exploration
| Constant | Old | New |
|---|---|---|
| Explore budget | 300-500 ticks | 300-500 (unchanged) |
| Return-path check | 5 Chebyshev | 15 Chebyshev |
| Stuck detection | 3 tiles/10 ticks | 8 tiles/10 ticks |
| Max distance before return | ~30 | 120 |
| Blacklist radius | 3 | 8 |

### Foraging
| Constant | Old | New |
|---|---|---|
| Forage commitment distance | 15-20 | 60-80 |
| Forage give-up distance | ~25 | 100 |

### Animals
| Constant | Old | New |
|---|---|---|
| Rabbit territory | 3 | 8 |
| Deer territory | 5 | 15 |
| Cow territory | 4 | 12 |
| Sheep territory | 4 | 10 |
| Boar territory | 4 | 12 |
| Wolf territory | 6 | 25 |
| Boar charge range | 2 | 5 |
| Wolf aggression range | 3 | 8 |
| Wolf pack convergence | 5 | 15 |
| Settlement deterrent | 3 | 10 |
| Flee detection (deer) | 5 | 15 |
| Flee detection (rabbit) | 3 | 6 |

### Combat
| Constant | Old | New |
|---|---|---|
| Disengage flee distance | 2 | 5 |
| All damage/duration/health | tick-based | unchanged |

### Trapping
| Constant | Old | New |
|---|---|---|
| Trap placement range | 8 | 25 |
| Trap catch radius | 1 | 2-3 |

### Pathfinding
| Constant | Old | New |
|---|---|---|
| A* simple budget | 8,000 | 40,000 |
| A* expensive budget | 8,000 | 80,000 |

### Camera
| Constant | Old | New |
|---|---|---|
| Tile pixel size | 64px | 64px (unchanged) |
| Default zoom | 1.0x | 0.3x |
| Min zoom | 0.25x | 0.08x |
| Max zoom | 4.0x | 4.0x (unchanged) |

## Migration Safety

- WorldWidth and WorldHeight must be SimConfig parameters, not hardcoded
- Old 64×64 config kept as fallback (change SimConfig values to switch)
- Simulation must remain deterministic at new scale
- Death baselines from 64×64 are invalid at 350×350 — establish new baselines after migration
- Performance must stay above 30 FPS with 122,500 tiles

## What Phase A Does NOT Include

- Settlement entity system (Phase B)
- Structure placement intelligence (Phase C)
- Terrain perception polish / minimap (Phase D)
- Any new features — this is purely a scale migration
