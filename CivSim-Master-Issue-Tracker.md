# CivSim — Master Issue Tracker Update (Post-D22)

**Proposed changes to CivSim-Master-Issue-Tracker.md**
**Date:** March 4, 2026
**Current build:** Post-D22 (Code Path Centralization)
**Tests:** 194/194 passing

---

## Header Update

```
Last updated: March 4, 2026 (post-D22 code path centralization, 5-seed behavioral equivalence confirmed)
Current build: Post-D22 (D21 + Hotfixes + Code Path Centralization)
Tests: 194/194 passing
```

---

## "What Works" Section — Add These

- **Movement interpolation** — smooth agent movement via lerp between previous and current position. Works at all sim speeds. Could use polish but functionally resolved.
- **Code path centralization (D22)** — TryAddToInventory, TryStartGather, TryEatFromHomeStorage centralized. Future inventory/gather/eat guards require changing 1 function, not 11-16. All 5 seeds produce identical output post-refactor.
- **Death regression integration test** — automated test runs seeds 42, 1337, 55555, 99999 at 50K ticks, asserts baseline death counts. Catches starvation regressions in CI (~54s).
- **SimConfig constants for non-food thresholds** — NonFoodPickupCap, NonFoodGatherFullScoreCap, NonFoodGatherReducedCap, NonFoodInventoryHardCap, NonFoodGatherReducedMultiplier, NonFoodPickupHungerGate. Tuning is now a single-line change.

---

## Category 1 Updates

### 1A. Idle Dominance — MEDIUM → LOW (Mostly Resolved)

**Was:** Joshua 24%, Lily 30%.
**Post-D21:** Joshua 14.8%, Lily 15.3%. Most agents under 11%.
**Post-D19 (restlessness/rest scoring):** Believed to have further improved, exact numbers to be verified on next playtest.
**Remaining:** Seed 99999 still shows elevated Idle from Explore stagnation. This is a late-game Explore motivation issue, not a scoring or materials problem.
**Status:** Downgrade to LOW. The Idle problem is now limited to late-game resource exhaustion on specific seeds, not a systemic issue.

---

## Category 2 Updates

### 2A. Movement Interpolation — RESOLVED

Move to Category 5:
- **Movement interpolation** — RESOLVED. Smooth lerp between previous and current position. Functional at all sim speeds. Minor polish pass possible later but not blocking.

---

## Category 3 Updates

### 3C. Multi-Path Code Duplication — RESOLVED (D22)

Move to Category 5:
- **Multi-path code duplication** — RESOLVED (D22). TryStartGather centralizes 22+ gather-start paths. TryEatFromHomeStorage centralizes 6 eat-from-storage paths (found 1 extra beyond the 5 originally documented). Code-path-risks.md updated.

### 3D. Inventory Add Not Centralized — RESOLVED (D22)

Move to Category 5:
- **Inventory add centralization** — RESOLVED (D22). Agent.TryAddToInventory enforces capacity limit, non-food cap, and hunger gate in one function. 3 paths preserved as direct manipulation (home child feeding, TryDispatchBuild withdrawal, CompletePreserve) with documented reasoning.

### 3E. Magic Numbers for Non-Food Caps — RESOLVED (D22)

Move to Category 5:
- **Non-food cap magic numbers** — RESOLVED (D22). 6 thresholds extracted to named SimConfig constants.

### 3F. Death Regression Integration Test — RESOLVED (D22)

Move to Category 5:
- **Death regression integration test** — RESOLVED (D22). 4-seed automated test at 50K ticks, gated behind Integration trait. ~54s runtime.

### 3G. CompleteClearLand Unguarded Inventory Path — RESOLVED (D22)

Move to Category 5:
- **CompleteClearLand unguarded** — RESOLVED (D22). Now routes through TryAddToInventory.

---

## Category 3 Remaining

### 3A. Settlement Founding Date Wrong — LOW (Unchanged)
### 3B. Sub-Tick Movement Durations Are Dead Math — INFO (Unchanged, verify if addressed by movement work)

---

## Priority Recommendation Update

**Tier 1 — Makes watching believable:**
1. Resource clustering in world gen (2E) — biome-aware placement, herds spread across tiles
2. Settlement structure rendering at proper scale (2D) — lean-to shouldn't be smaller than an agent's leg
3. Path line showing actual A* route (2B)

**Tier 2 — Makes the simulation deeper:**
4. Explore motivation for late-game stagnation (remaining 1A/1B) — agents need to venture further for higher-tier resources
5. Resource memory system for foraging (4C/1C) — purposeful trips to remembered food sources
6. "Gather animal" meaning (4A) — hunting vs domestication

**Tier 3 — Design decisions:**
7. Settlement growth visualization (2G)
8. Need-driven experimentation (4B)

**Tier 4 — Polish and atmosphere:**
9. Day/night lighting (2F)
10. Path line flicker fix (2C)
11. Cooking requires campfire? (1D)
12. Settlement founding date bug (3A)
13. Fish sprite (2H)
14. Sleeping away from home (1E)

**Rationale for new Tier 1:** With movement interpolation resolved and the codebase centralized, the biggest gap between "simulation that works" and "simulation that's compelling to watch" is visual. Resource clustering would make the world look natural instead of randomly scattered. Structure scale would make settlements look like settlements instead of ant-sized props. These are the visual changes that turn "technically correct" into "I want to keep watching."
