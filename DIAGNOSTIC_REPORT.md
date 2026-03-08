# CivSim Diagnostic Report — Visual, Simulation & Scale Issues

**Date:** Feb 2026
**Scope:** Full audit of rendering pipeline, agent AI behavior, and simulation balance
**Triggered by:** Playtesting session (seed -2065723033, run to ~Day 1300, 200 pop, 1 FPS)

---

## PART A: VISUAL / RENDERING ISSUES

### A1. Resource Sprites Are Misassigned (Critical — Unchanged from Prior Report)

6 of 9 resource PNGs show the wrong art. This is the root cause of stumps, "turnips on water," trees on mountains, etc:

| File | What It Shows | What It Should Be |
|---|---|---|
| `berry_bush.png` | Tree stump (brown log cross-section) | Berry bush with berries |
| `stone.png` | Green bushy tree | Gray boulder |
| `rock.png` | Green bushy tree | Rock variant |
| `ore.png` | Green bushy tree | Metallic ore deposit |
| `grain.png` | Gray pebbles | Wheat/grain sheaf |
| `fish.png` | **Green plant/radish** ("turnips") | Fish |

**The "turnips on water"**: Every water tile spawns Fish resources. The `fish.png` sprite is a green plant/radish shape. At zoom 1.0+, water tiles are covered in up to 2 green radish sprites per tile = the "turnip ocean" in the screenshot.

**Designer action:** Replace all 6 PNGs with correct art. See prior handoff document for specs.

---

### A2. Agent Heads Cut Off / Partial Sprites

**Root cause confirmed:** `SpriteAtlas.DrawCentered()` centers sprites on their **geometric center**, not the foot/bottom. For 32×32 humanoid sprites where the head is in the top ~10 pixels:

```
DrawCentered(name, cx, cy) → draws sprite from (cx-16, cy-16) to (cx+16, cy+16)
```

The agent layout places agents at Y-offsets of `0.35` to `0.70` within a 64px tile. At `0.35 × 64 = 22px`, the top of the sprite is at `22 - 16 = 6px` from the tile top — close to the boundary. For multi-agent layouts where agents are placed at upper positions, the head extends **above the tile's draw area** and can appear clipped by adjacent tile rendering.

Additionally, the `biomes_forest` background tile is drawn per-tile with `DrawStretched()` which paints over the full tile area. If Tile A draws its biome background after Tile B (above) rendered its agents, Tile A's background covers the top of agents on Tile B whose heads extend into Tile A's space.

**Recommendation (code):** Change agent sprite anchor from geometric center to **bottom-center**. Replace `DrawCentered` with a new `DrawBottomCentered(name, cx, bottomY, scale)` that positions the sprite's bottom edge at `bottomY` and centers horizontally. This matches how the procedural humanoid drawing already works (comment says "center bottom of figure").

**Recommendation (rendering order):** Ensure all biome backgrounds render before any agents. Currently the single-pass loop (biome → resources → structures per tile) means Tile A's background can paint over Tile B's agents when agents straddle tile boundaries.

---

### A3. Agents Teleport Between Tiles (No Movement Interpolation)

**Root cause confirmed:** Agent positions are stored as **integer tile coordinates** (`int X, int Y` in Agent.cs). Movement is a multi-tick action — the agent stays at their current tile for the entire movement duration, then snaps to the destination when the action completes.

```csharp
// Agent.cs
public int X { get; set; }  // Integer tile position
public int Y { get; set; }

// AgentAI.cs - CompleteMove: instant position snap
agent.MoveTo(tx, ty, world);  // X = newX, Y = newY in one frame
```

There is **zero interpolation** between tiles. At high zoom, this is visually jarring — agents blink from one tile to the next.

**Recommendation (code):** Add a visual-only interpolation layer in `AgentRenderer`:
- Store `(prevX, prevY)` and `(currentX, currentY)` per agent
- Track a `moveProgress` float (0.0 → 1.0) over the move duration
- Lerp the render position: `renderPos = lerp(prev, current, moveProgress)`
- This is purely visual — simulation logic stays integer-based

**Alternative (simpler):** Add a `float VisualX, VisualY` to Agent that smoothly approaches `X, Y` each frame using exponential decay. AgentRenderer reads VisualX/Y instead of X/Y.

---

### A4. Biome Tile Art Quality

Unchanged from prior report. Forest tile is flat green, all biome tiles are minimal. The art gap between NPC agents (high quality Vectoraith pack) and environment tiles (placeholder-grade) is jarring.

**Designer action:** Enrich all 5 biome tiles (see prior handoff for specs).

---

## PART B: SIMULATION / AI BUGS

### B1. Settlement Names Change Every 100 Ticks (Bug)

**Root cause found:** `SettlementDetector.Detect()` is called every 100 ticks and creates **entirely new** `Settlement` objects with **freshly generated names** each time:

```csharp
// SettlementDetector.cs line 96
Name = SettlementNameGenerator.Generate(random)  // NEW name each detection
```

The `random` object's state advances between detections, so the same shelter cluster gets a different name every 100 ticks. The `_knownSettlementNames` HashSet only prevents duplicate *notifications* — it doesn't stabilize the names shown in the UI.

**Result:** Your 3 original settlements ("Thornstead", "Ashcrest", etc.) get renamed to "Cedarwick", "Elderstead", "Clayhaven" etc. on the next detection cycle. The UI always shows the current `Settlements` list, which has new names.

**Fix needed (code):** Settlements should have **persistent identity** based on their shelter cluster center position. If a detection finds a cluster at roughly the same location as an existing settlement, reuse the existing name instead of generating a new one.

---

### B2. Farming Dominates All Other Actions After Discovery (~Day 600+)

**Root cause: a cascade of soft biases that compound into total dominance.**

1. **Score imbalance:** Farming base score = **0.5**. Experimentation base score = **0.25**, reduced by 0.3× when agent is even moderately hungry (≤50) = effective **0.075**. That's a **6.7× advantage** for farming.

2. **Infinite farm opportunities:** Farms need re-tending every 30 ticks (`FarmTendedGracePeriod = 30`). With `IsFarmable` returning true for ALL Plains and Forest tiles (~50% of the 64×64 world = ~2,000 tiles), there is always a farm nearby that needs tending.

3. **No saturation mechanism:** There is no check like "we have enough food, reduce farming priority." Farming score is constant at 0.5 regardless of food abundance. Agents will farm even when food is labeled "Abundant (2816)."

4. **Experimentation prerequisites gate progress:** Later discoveries (Tier 5-6) require multiple prior discoveries + specific resources. If agents never experiment because they're farming, they never unlock prerequisites for harder discoveries, creating a **dead-end spiral**.

5. **Action dampening is insufficient:** The dampening factor (0.9×) only slightly reduces repeated action scores. Farming at 0.5 × 0.9 = 0.45 still crushes experimentation at 0.075.

**Result:** After farming is discovered, all agents farm forever. Zero new discoveries from ~Day 600 to Day 1300.

**Recommendation (GDD):**
- Add a food saturation curve that reduces farming priority when food is abundant
- Boost experimentation score when food is secure (inverse relationship)
- Add a "curiosity cooldown" that periodically forces experimentation attempts
- Consider making some discoveries triggered by farming itself (agriculture → irrigation → crop rotation)

---

### B3. Babies Starve Despite "Food: Abundant" Label

**Root cause: The food label is a global metric disconnected from per-agent access.**

The "Food: Abundant" calculation:
```csharp
int totalFood = sum of ALL food on ALL tiles in the entire world
foodHealth = totalFood / (aliveAgents × hungerDrainPerTick)
// > 40 = "Abundant"
```

This counts grain sitting on farmed tiles 50 tiles away from any baby. It's a **theoretical** metric, not an **accessibility** metric.

**Why babies specifically die:**

1. **Children cannot gather, farm, or eat independently.** After the P1-P3 survival checks, children are forced into `Explore` with no other option:
   ```csharp
   if (isChild) {
       StartExplore(agent, world, bus, currentTick, trace);
       return;  // No farming, gathering, cooking, or self-feeding
   }
   ```

2. **Children depend on adults being adjacent with food in inventory** (P1.5 Feed Child). The adult must: (a) have food items in personal inventory, (b) be on the same or adjacent tile as the hungry child, (c) detect the child's hunger < 40.

3. **Adults are farming on distant tiles.** Since farming dominates (B2), adults spend all their time on remote farm tiles, far from the settlement where children explore. They're never adjacent to feed children.

4. **No "bring food home" behavior.** There is no priority that says "I have food and children at home are hungry, go home." ReturnHome is utility-scored with weak quadratic falloff and loses to farming every time.

**Result:** World has 2,800 food units (Abundant). Adults farm endlessly. Babies wander the settlement exploring. No adult is ever adjacent to feed them. Babies starve to death.

**Recommendation (GDD):**
- Children should be able to eat from tile resources or granary independently (at reduced efficiency)
- Adults with children should have boosted ReturnHome priority when carrying food
- Feed Child (P1.5) should extend beyond adjacent tiles — adults should *seek out* hungry children
- The food abundance label should factor in accessibility (per-settlement food, not global)

---

### B4. Agents Don't Use Their Shelters

**Root cause: ReturnHome is weak in the utility scoring.**

ReturnHome score = `HomePullStrength / (1 + distance²)` — quadratic falloff. At distance 5, this is `HomePullStrength / 26`. Farming at 0.5 wins every time unless `HomePullStrength` is very high.

Agents build shelters (because Build has decent utility), set their HomeTile, then walk away to farm and never return. The shelters provide no ongoing pull strong enough to compete with farming.

Additionally, building a new shelter **always overwrites HomeTile** — so agents who build a second shelter elsewhere lose their original home connection.

**Recommendation (GDD):**
- Increase HomePullStrength or add time-of-day/fatigue cycles that force agents home
- ReturnHome should interrupt farming when health < 70% or hunger < 60%
- Add a "daily routine" concept: agents should return home periodically, not just when nearly dead
- Building a shelter should only update HomeTile if the new shelter is closer

---

### B5. Performance Collapse at High Population

Population reached ~200 agents at Day 1300, dropping to 1 FPS.

**Likely causes (not fully profiled):**
- Agent AI runs `UtilityScorer` for every agent every tick — O(agents × action_types × nearby_scan)
- `GetNearbyAgents()` scans all agents on current + adjacent tiles every tick
- Settlement detection every 100 ticks does full world scan + flood fill
- Rendering 200 agents with sprites + activity icons at zoom 1.0+

**Recommendation (code):** Profile first, but likely candidates:
- Spatial hashing for agent lookups
- Utility scoring every N ticks instead of every tick (with cached decisions)
- Agent LOD for AI: distant agents make simpler decisions

---

## PART C: SCALE & IDENTITY (From Prior Discussion)

Confirmed direction: **Option C (Hybrid LOD)** — smooth zoom transitions from world-map to local detail.

The simulation issues (B2-B4) actually compound the scale confusion: when zoomed out, you see a "world" with continents. When zoomed in, you see 200 people all farming every tile in sight while their babies die in the settlement. The behavioral issues make the "local area" experience feel broken, which amplifies the scale dissonance.

Fixing the AI balance (farming saturation, child feeding, return-home pull) will make the zoomed-in experience feel like a functioning settlement. Fixing the LOD transitions will make the zoomed-out experience feel like a functioning world. Both are needed.

---

## SUMMARY: Action Items by Owner

### Designer (Art)
| # | Item | Priority |
|---|------|----------|
| D1 | Replace 6 misassigned resource PNGs (berry, stone, rock, ore, grain, fish) | Critical |
| D2 | Improve tree2.png (stump-heavy) | High |
| D3 | Enrich 5 biome tiles (especially forest) | High |
| D4 | Create medium-LOD cluster sprites (for Option C hybrid zoom) | Medium |

### GDD / Design (Balance)
| # | Item | Priority |
|---|------|----------|
| G1 | Farming saturation curve (reduce priority when food abundant) | Critical |
| G2 | Child feeding overhaul (self-feeding, adult seek-child, range extension) | Critical |
| G3 | Experimentation boost when food secure | High |
| G4 | ReturnHome strength / daily routine cycle | High |
| G5 | Food abundance label → per-settlement accessibility metric | Medium |
| G6 | HomeTile overwrite logic (only update if closer) | Low |

### Code (Engineering) — Awaiting GDD Update
| # | Item | Priority |
|---|------|----------|
| C1 | Settlement name persistence (reuse name for same cluster location) | High |
| C2 | Agent sprite anchor: geometric center → bottom-center | High |
| C3 | Agent movement interpolation (visual lerp between tiles) | High |
| C4 | Render order: all biome backgrounds before any agents | Medium |
| C5 | Performance profiling at 200+ agents | Medium |
| C6 | Intermediate LOD tiers (when designer provides cluster sprites) | Medium |

### Already Applied (This Session)
| # | Item | Status |
|---|------|--------|
| ✅ | Fixed Animals sprite mapping bug (was rendering as stone/tree) | Done |
| ✅ | Added Animals to ResourceDotColors for medium zoom | Done |
| ✅ | Reduced MaxSpritesPerResource 3 → 2 | Done |
| ✅ | Added sub-region staggering (resources separated by quadrant) | Done |
| ✅ | Lowered biome texture LOD threshold 1.0 → 0.6 | Done |
| ✅ | Increased resource dot size for better mid-zoom visibility | Done |
