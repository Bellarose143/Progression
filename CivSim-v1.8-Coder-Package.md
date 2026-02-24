# CivSim v1.8 — Coder Implementation Package

**Version:** 1.8  
**Date:** February 2026  
**Status:** All changes approved by project creator

---

## How to Use This Document

This is your implementation reference for v1.8. It contains every system specification that changed, written as complete replacement text — not diffs, not summaries.

**Before touching code, read these project files (they're short):**

1. `CLAUDE.md` — The binding contract. Anti-patterns, design principles, implementation rules.
2. `CivSim-Vision-Summary.md` — The WHY behind everything. 58 lines. Read it.

**Then use this document system-by-system.** Each section is self-contained. When implementing the Knowledge System, read Section 2. When implementing Reproduction, read Section 3. You don't need the whole document in your head at once.

**Values marked `[RECALIBRATE]`** need math work in a dedicated calibration pass. Implement systems with placeholder constants that can be tuned later. Section 10 lists every `[RECALIBRATE]` item.

**If something is ambiguous, ask.** Do not invent design decisions. See CLAUDE.md's Communication Protocol.

---

## Section 0: What's Unchanged

These systems carry forward from the current codebase as-is. Do not modify them unless a specific section below says otherwise.

- **World generation** — Grid, biomes, terrain types, water placement. (Resource _distribution_ changes — see Section 6 — but the biome system is unchanged.)
- **Agent decision AI** — Utility scoring system, priority tiers (P0-P4), need-based action selection. (Individual utility formulas change where noted, but the architecture is the same.)
- **Pathfinding** — A\* with terrain cost weights. Unchanged.
- **Social bonds** — InteractionCount, bond decay, bond thresholds. Unchanged for now. (Note: the Teach interaction no longer exists, so bond formation from teaching stops. Bonds form from: resting adjacently, gathering on the same tile, sharing food. A future pass will revisit Social Bonds holistically.)
- **Ecology** — Overgrazing, animal populations, resource regeneration mechanics. The _rates_ need recalibration to sim-days (see [RECALIBRATE] tracker), but the systems are unchanged.
- **Personality traits** — All traits unchanged EXCEPT the Social trait's utility multipliers (see Section 2).
- **Child development stages** — Infant/Youth/Adult progression. The _age thresholds_ were updated to sim-days in Section 1, but the behavioral rules (what infants can/can't do, youth capabilities) are unchanged.
- **Discovery Steps 1-3** — Experimentation trigger, combination selection, discovery roll. All unchanged except the collaboration bonus modifier (see Section 2).

---

## Section 1: Time Model

**This is foundational. Everything else depends on it. Implement this first.**

### Calendar Structure

| Unit             | Definition                                                                           |
| ---------------- | ------------------------------------------------------------------------------------ |
| 1 sim-day        | The basic observable calendar unit. Contains enough time for multiple agent actions. |
| 1 season         | 7 sim-days                                                                           |
| 1 year           | 4 seasons = 28 sim-days                                                              |
| 1 generation     | ~25 years = ~700 sim-days                                                            |
| Average lifetime | ~65 years = ~1,820 sim-days                                                          |

Seasons are numbered 1–4. No named seasons, no mechanical seasonal effects. Seasonal variation is a candidate for future development but is **not in scope for v1.8.**

### The Tick

The tick is an internal simulation heartbeat — the smallest unit of time the engine processes. Its rate is an implementation detail tuned for smooth animation and acceptable performance. **It is invisible to the observer and must never appear in the UI, event log, or any player-facing output.** The observer sees days, seasons, and years — never ticks.

### Physical Scale

The world represents a local area — a valley, river basin, or stretch of habitable land roughly 10–20 km across. Each tile is a distinct region: a forest clearing, a stretch of riverbank, a rocky hillside. Moving between adjacent tiles takes a few hours across open plains to a full day's march through mountains. The 64×64 grid is one community's origin story, from nothing to civilization.

### What a Sim-Day Feels Like

At 1x speed, one sim-day takes approximately 10 minutes of real time. During that time, the observer should see an agent perform a recognizable sequence: waking, eating, traveling to a worksite, performing a task, returning home, eating again, resting. Not every day will contain all of these — a day spent building might be mostly construction with a meal break — but the day should feel inhabited, not instantaneous.

### Action Durations

All durations in sim-hours or sim-days, not ticks.

| Action                   | Sim-Time      | Real-Time at 1x   | Notes                               |
| ------------------------ | ------------- | ----------------- | ----------------------------------- |
| Eat a meal               | ~10–15 min    | ~1 sec            | Brief, interruptible.               |
| Cook food                | ~30 min       | ~3 sec            | Visible smoke/fire indicator.       |
| Gather (nearby)          | ~2 hours      | ~20 sec           | Travel + collection + return.       |
| Build lean-to            | ~4 hours      | ~40 sec           | An afternoon's work.                |
| Travel 1 tile (plains)   | ~2–4 hours    | ~20–40 sec        | Baseline terrain.                   |
| Travel 1 tile (forest)   | ~3–6 hours    | ~30–60 sec        | Moderate difficulty.                |
| Travel 1 tile (desert)   | ~3–5 hours    | ~25–50 sec        | Slightly taxing.                    |
| Travel 1 tile (mountain) | ~6–10 hours   | ~1–1.5 min        | May consume a full day.             |
| Craft simple item        | ~1–3 hours    | ~10–30 sec        | [RECALIBRATE] per recipe.           |
| Craft complex item       | ~4–8 hours    | ~40 sec – 1.5 min | [RECALIBRATE] per recipe.           |
| Build improved shelter   | ~3–5 days     | ~30–50 min        | Multi-day. Progress stored on tile. |
| Build granary            | ~4–5 days     | ~40–50 min        | Multi-day.                          |
| Build walls              | [RECALIBRATE] | [RECALIBRATE]     | Multi-day.                          |
| Build communal building  | [RECALIBRATE] | [RECALIBRATE]     | Multi-day.                          |
| Build monument           | ~30–40 days   | ~5–7 hours        | Long-term communal project.         |
| Experiment               | ~4–8 hours    | ~40 sec – 1.5 min | One attempt per action.             |
| Rest                     | ~6–8 hours    | ~1–1.5 min        | Overnight. Health recovery.         |
| Share food               | ~5–10 min     | ~1 sec            | Brief transfer.                     |
| Preserve food            | ~2–4 hours    | ~20–40 sec        | [RECALIBRATE]                       |
| Tend farm                | ~2–4 hours    | ~20–40 sec        | [RECALIBRATE]                       |

These are design targets. The calibration pass derives exact values.

### Lifespan and Development

| Life Stage            | Age Range  | Sim-Days | Notes                                                                                                  |
| --------------------- | ---------- | -------- | ------------------------------------------------------------------------------------------------------ |
| Infant                | 0–5 years  | 0–140    | Cannot gather, build, experiment, move independently. Must be fed by adjacent adult. Strong home pull. |
| Youth                 | 5–12 years | 140–336  | Can eat independently, gather at 0.5× efficiency, move independently. Cannot build or experiment.      |
| Adult                 | 12+ years  | 336+     | Full capabilities.                                                                                     |
| Reproduction eligible | 16+ years  | 448+     | Minimum age hard gate.                                                                                 |
| Old age onset         | 55 years   | 1,540    | Natural death probability begins.                                                                      |
| Maximum lifespan      | 80 years   | 2,240    | Guaranteed natural death.                                                                              |

Average lifespan under good conditions: ~65–70 years (~1,820–1,960 sim-days).

### Natural Death

- **Starvation:** Health reaches 0 from starvation damage ([RECALIBRATE] per sim-day at hunger 0). Target: ~2 sim-days from full health with no food.
- **Old age:** After 55 years (1,540 sim-days), daily probability ramps linearly from 0% to 100% at 80 years (2,240 sim-days).
- **On death:** Agent remains selectable for [RECALIBRATE] sim-days (target ~5). Inventory drops to tile. Knowledge handled per Section 2. Death Report shows cause, final stats, last 8 actions, inventory, knowledge.

### Viewing Speed

| Speed        | Real-Time per Sim-Day | Use Case                                      |
| ------------ | --------------------- | --------------------------------------------- |
| 1x (default) | ~10 minutes           | Watching agents live. Contemplative viewing.  |
| 2x           | ~5 minutes            | Mild speedup for stable periods.              |
| 5x           | ~2 minutes            | Cruising speed between milestones.            |
| 10x          | ~1 minute             | Fast-forward through uneventful stretches.    |
| 20x          | ~30 seconds           | Overnight running, skipping large time spans. |

Keyboard: 1/2/3/4/5 for the five speeds. Changes take effect immediately.

### Real-Time Session Estimates

| Milestone                   | At 1x      | At 5x      | At 20x        |
| --------------------------- | ---------- | ---------- | ------------- |
| First child to youth (5y)   | ~23 hours  | ~4.7 hours | ~1.2 hours    |
| First child to adult (12y)  | ~56 hours  | ~11 hours  | ~2.8 hours    |
| One generation (~25y)       | ~117 hours | ~23 hours  | ~6 hours      |
| Full lifetime (~65y)        | ~303 hours | ~60 hours  | ~15 hours     |
| Overnight run (8hrs at 20x) | —          | —          | ~34 sim-years |

Multi-session viewing across days and weeks is the intended experience. "Go to bed with the founders, wake up to grandchildren" is a valid use case at 20x overnight.

### Auto-Save (Required)

Running overnight at 20x is an intended use case. Auto-save is mandatory before long runs.

- Serialize complete world state + all agent state to JSON (or equivalent) at configurable intervals. Default: every 7 sim-days (1 season).
- Crash recovery: reload from last auto-save.
- Save includes: simulation date (day/season/year), world seed, all tile states, all agent states, settlement knowledge stores, discovery history, configurable constants in effect.
- Manual save/load via keyboard shortcut (suggested: F5 save, F9 load).

---

## Section 2: Knowledge System

**This is the biggest structural change in v1.8. It touches many systems. Read this entire section before implementing any part of it.**

### What Was Removed

- **The Teach action.** Removed from the action list, utility scoring, and all references. It does not exist in v1.8.
- **Teaching speed bonuses.** Gone.
- **"Teaching as active cost"** from the emergence sources section. Replaced (see below).
- **All "Agent X taught Agent Y" log messages.** Gone.

### What Replaced It: Communal Knowledge

Knowledge is communal within a settlement and geographic between settlements. There is no agent-to-agent teaching action.

#### Within a Settlement

When an agent makes a discovery while within their settlement radius, the knowledge enters settlement propagation:

**Pre-writing era (default):** The discoverer shares through demonstration and oral retelling. Propagation takes 1-2 sim-days (reduced to 1 sim-day if the settlement has discovered oral_tradition). During this window, all settlement residents gradually acquire the knowledge. If the discoverer dies before propagation completes, knowledge may be partially lost -- each resident's chance of retaining it is proportional to how far through the window the discoverer survived. [RECALIBRATE] exact probability curve.

**Post-writing era (after writing recipe discovered):** Propagation is near-instant and durable. The discovery is "recorded" -- it persists in the settlement knowledge base permanently, even if the discoverer and all witnesses die. Writing fundamentally changes knowledge resilience. This is a civilization-defining breakthrough.

#### Away From Settlement -- Explorer Knowledge

An agent who discovers something outside their settlement radius carries it as personal knowledge only. They must physically return to share it. Upon return, normal propagation begins (oral or written depending on settlement tech).

If the explorer dies before returning, the knowledge is lost entirely. This creates dramatic tension around exploration.

#### Between Settlements

No automatic transfer. Future mechanics (trade, migration) will enable it. For now, each settlement is an independent knowledge pool.

### Knowledge Events

| Event                            | Category | Description                                                                  |
| -------------------------------- | -------- | ---------------------------------------------------------------------------- |
| Discovery made                   | Critical | Agent discovers a new recipe.                                                |
| Explorer returned with discovery | Notable  | Agent with personal knowledge arrives home. Propagation begins.              |
| Settlement learned [recipe]      | Notable  | Propagation completes. All residents have access.                            |
| Knowledge lost -- explorer died  | Critical | Agent died before returning. Knowledge gone.                                 |
| Knowledge partially lost         | Notable  | Discoverer died mid-propagation. Some residents absorbed it, others did not. |

### Ripple Effects Across Other Systems

**Agent Actions table:** Remove Teach row. Actions are: Move, Gather, Eat, Cook, Preserve Food, ShareFood, Rest, Craft, Build, Experiment, Tend Farm, Reproduce.

**Utility Scoring table:** Remove Teach row.

**Discovery Roll -- Collaboration Bonus (reframed):**

Old: +3% per adjacent agent who already knows this recipe.
New: **+2% per adjacent agent, capped at +10%.** Working near others provides help and suggestions. Rewards settlement clustering. Simple, clean.

**Emergence Sources -- replace "Teaching as active cost" with:**

"Geographic knowledge friction: Discoveries made away from home must survive the journey back. An explorer who dies carrying new knowledge creates a genuine setback -- that recipe may not be rediscovered for generations. Distance between settlements creates independent development paths. The discovery of writing is a civilization-defining breakthrough that makes knowledge permanent and resilient."

**Social Trait -- updated multipliers:**

Old: 1.4x Teach, 1.4x Socialize, 1.2x Reproduce
New: **1.4x Knowledge Propagation Speed**, 1.4x Socialize, 1.2x Reproduce

A Social agent in the settlement reduces the oral propagation window -- they are the person who talks to everyone and makes sure nobody missed the news. Settlements with Social agents absorb discoveries faster.

**Social Awareness subsection -- updated references:**

Old bullet: "Teaching requires adjacency -- agents who can see a knowledgeable neighbor will move toward them."
New: "Knowledge propagation happens within the settlement -- agents who live near each other share discoveries naturally through the communal propagation system."

Old summary paragraph references "teaching, reproduction, collaboration" as reasons for proximity.
New: references "reproduction, collaboration, food sharing" instead. "The clustering is emergent, not forced."

**oral_tradition recipe (Branch 5) -- repurposed:**

Old trigger: 5 successful teach actions. Old effect: Teaching range +1, knowledge spreads faster.
New trigger: **Settlement has propagated 5 discoveries through oral system.** New effect: **Oral propagation window reduced from 2 days to 1 day. Partial knowledge loss probability on discoverer death during propagation is reduced.** Remains prerequisite for communal_building. Auto-triggers (no resource cost, no probability roll). "The community formalizes its storytelling and knowledge-sharing traditions."

**Era Gate System note -- updated:**

Old: "Teaching bypasses era gates."
New: "Communal knowledge bypasses era gates. Era gates only constrain experimentation (discovery). If Agent A meets all gates and discovers bronze_working, that knowledge propagates to all settlement residents regardless of their individual era breadth. The era gate represents the intellectual breadth needed to independently invent something, not the ability to benefit from someone else's invention."

**monument recipe effect -- updated:**

Old: "Teaching 2x, experiment +5% within 3 tiles."
New: **"Knowledge propagation speed 2x, experiment +5% within 3 tiles."**

**writing recipe effect -- expanded:**

Old: "Teaching speed 2x. Knowledge persists in structures."
New: **"Knowledge propagation becomes near-instant and permanent. Discoveries are recorded -- they persist in the settlement knowledge base even if all agents who knew them die. Eliminates knowledge loss from death. Civilization-defining breakthrough."**

**Event Categories table -- updated:**

Notable events: Remove "teaching completed." Add: explorer returned with discovery, settlement learned recipe, knowledge partially lost.
Critical events: Add: knowledge lost -- explorer died before returning. (Alongside existing: agent death, birth, discovery made, settlement founded, extinction.)

**Knowledge Spread Chart diagnostic -- reframed:**

Old: "Steep curves mean teaching is working well. Flat curves mean knowledge is stuck with the discoverer."
New: "Steep curves mean the communal propagation system is working well. Flat curves mean knowledge is trapped with a lone explorer who hasn't returned home. Curves that drop to zero mean a recipe was lost to death before reaching the settlement -- a dramatic setback, not a bug."

**Knowledge Tracking diagnostic bullets -- updated:**

Old: "Who taught whom (teaching graph)." and "Average time from first discovery to 50% population knowing a recipe."
New: "Knowledge propagation events (discovery -> return -> settlement absorption, or discovery -> explorer death -> loss)." and "Average time from first discovery to settlement-wide knowledge (propagation rate)."

**Child knowledge inheritance -- updated:**

Children inherit settlement knowledge, not individual parent knowledge. Child attributes on birth include: all current settlement knowledge (plus {clothing} innate).

## Section 3: Reproduction

**Reproduction requires infrastructure.** This is not a population simulator — it's a civilization simulator. Population growth is a consequence of technological progress, not a parallel track. Agents cannot breed their way to 200 with crude tools; they need shelter, food surplus, and stability first.

### Hard Gates (Non-Negotiable Physical Requirements)

These are binary checks. If any fails, reproduction cannot occur regardless of other conditions.

| Requirement | Specification                                                      | Rationale                                                          |
| ----------- | ------------------------------------------------------------------ | ------------------------------------------------------------------ |
| Proximity   | Both agents on the same or adjacent tiles                          | Physical proximity required.                                       |
| Minimum age | Both agents age ≥ 16 years (448+ sim-days)                         | Must be adults.                                                    |
| Shelter     | Shelter within 5 tiles of both agents                              | Must have solved exposure. No breeding in the wild.                |
| Cooldown    | Neither agent has reproduced within the last 2 years (56 sim-days) | Prevents population explosion. One child per parent every 2 years. |

### Stability Assessment (Replaces Fixed Thresholds)

The old system used fixed thresholds: hunger ≥ 60, health ≥ 50, food surplus ≥ 8. That's gone. Reproduction now uses a composite **stability score** — a continuous value from 0.0 to 1.0.

**Stability Score** = weighted average of:

| Factor              | Weight | What It Measures                                                             | How It Scores                                                                                                                                                                                  |
| ------------------- | ------ | ---------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Food security       | 0.4    | Food trend over last 7 sim-days. Are reserves growing, stable, or depleting? | Considers: personal inventory, home storage contents, granary access, nearby forageable resources. Growing reserves score high. Depleting reserves score near zero. [RECALIBRATE] exact curve. |
| Shelter quality     | 0.2    | Quality of available shelter.                                                | Lean-to = base score (~0.4). Improved shelter = good (~0.7). Improved shelter + walls = high (~0.9). [RECALIBRATE] exact values.                                                               |
| Existing dependents | 0.2    | Number of current infant and youth children.                                 | 0 dependents = 1.0. Each existing infant or youth reduces the score significantly. 3+ young children → score approaches 0. [RECALIBRATE] exact reduction per dependent.                        |
| Health trend        | 0.2    | Both agents' health over last 7 sim-days.                                    | Stable or improving = high. Declining = low. Either agent below 50 health → score drops sharply. [RECALIBRATE] exact curve.                                                                    |

**Design note:** There is no hard floor on health. The old health ≥ 50 gate is gone. An agent at health 35 with an improving trend (recovering from an injury) could still score okay on health — the stability score captures "overall trajectory," which is the right lens. A one-off injury in an otherwise stable situation shouldn't block reproduction.

### Updated Reproduce Utility Score

| Action    | Base Utility Formula                   | Key Inputs                                                                                                                                                                                                                                                                            |
| --------- | -------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Reproduce | 0.3 × partnerAdjacent × stabilityScore | Hard gates (age, shelter, cooldown) must pass first. stabilityScore is the 0.0–1.0 composite. A thriving pair scores ~0.25 (0.3 × 0.83). A struggling pair scores ~0.03 (0.3 × 0.1). Reproduction is opportunistic when conditions are good, effectively impossible when they're not. |

### Improved Shelter Bonus — Folded In

The old separate "+20% reproduction chance with improved shelter" modifier is removed. Improved shelter is now captured within the shelter quality factor of the stability score (improved shelter scores ~0.7 vs lean-to at ~0.4). The effect is preserved but integrated into the continuous system rather than being a bolt-on.

### Reproduction Outcome

- **Success chance:** [RECALIBRATE]% base, modified by shelter quality. Configurable.
- **Cost:** Consumes [RECALIBRATE] food from the parents' combined inventory and/or home storage.
- **Child attributes:** Hunger 80, Health 100, Age 0, Knowledge: {clothing} (innate) plus all current settlement knowledge, empty Inventory, same Location as parents, inherits parent HomeTile.
- **Child traits:** 2 traits drawn from parent pool (4 total) with 15% mutation chance per slot. See Personality Traits.
- **Child bonds:** Parent-child bonds start at InteractionCount = 10.

### What This Achieves (Use as Sanity Checks)

- **Bad seed** (poor spawn, scarce resources): Agents may not reproduce for many sim-years because stability never gets high enough. Two adults barely feeding themselves will not have children — this is responsible behavior, not a bug.
- **Good seed** (abundant start, solid shelter): Earlier reproduction because conditions genuinely support it. A well-fed pair in an improved shelter with stored food and no existing children will reproduce sooner.
- **"Baby Making Simulator" is structurally impossible:** Each child reduces the stability score for the next one through the existing dependents factor. A pair with two infants has a near-zero stability score even if food is abundant — they're already at capacity.
- **Technology enables population growth:** Better shelter quality improves the score. Home storage and granaries improve food security. Farming stabilizes food trends. Population growth is a consequence of technological progress, exactly as designed.

---

## Section 4: Agent Names

This is a small, surgical change.

### Agent Attributes Table — Name Row

Old: `Name | String | Auto-generated | Procedural name for logging and identification.`

New:

| Attribute | Type   | Range             | Description                                                                                                                                                                                                                                                                                                                   |
| --------- | ------ | ----------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Name      | String | From curated list | Real human first name drawn from curated data files (minimum 100 male, 100 female). Names are drawn without replacement from the living population when possible. If a name would duplicate a living agent, a generation numeral is appended (e.g., "Mary II", "James III"). Dead agents' names return to the available pool. |

### Name Data Files

Agent names are stored in external data files (JSON or plain text), **not hardcoded**. This allows easy expansion and customization without code changes.

The name lists are a discrete curation task — compilation of real human first names. No design work required.

### No Downstream Effects

This change is purely cosmetic/identification. No systems depend on how names are generated.

---

## Section 5: Agent Memory

### Three-Layer Model

Agents operate on a combination of what they currently perceive, what they personally remember, and what their settlement collectively knows. This three-layer model mirrors how humans actually navigate: you see what's in front of you, you remember where you saw berries last week, and you know the river is to the east because everyone in your village knows that.

### Layer 1: Transient Memory (Per-Agent)

Short-term, decaying, individual. Each agent maintains a personal buffer of recent observations. This is what an agent saw recently but may no longer be able to see.

- **What gets remembered:** Resource locations (tile coordinates + resource type + approximate quantity at time of observation), agent sightings (last known position of other agents). Transient memory records things that move, deplete, or change.
- **When memories form:** Every tick, anything within the agent's perception radius is automatically recorded or updated. This is not an action — it happens passively as part of the Perceive step.
- **Decay:** Each memory has a timestamp. Memories older than MEMORY_DECAY_DURATION (target: ~30 sim-days / approximately 1 month) are purged. An agent remembers where they saw berries for about a month, then forgets. [RECALIBRATE] exact duration.
- **Staleness:** Memories can be wrong. An agent remembers "8 berries at (12,5) on Day 340" but by Day 360, another agent may have gathered them. Acting on stale memory and finding the resource gone clears the memory immediately.
- **Priority:** Current perception always overrides memory. Live data replaces stored memory for visible tiles.
- **Capacity:** MEMORY_MAX_ENTRIES (default: 30). When full, the oldest memory is evicted. Structure/settlement knowledge does not count against this cap — it's stored separately.

### Layer 2: Permanent Knowledge (Per-Settlement)

Long-term, persistent, communal. This is settlement lore — the collective knowledge of a community that persists across generations. **Stored on the settlement, not on individual agents.** All settlement residents have access.

**What gets stored:**

- Discovered resource deposits, especially finite ones (ore veins, stone quarries, cave locations)
- Named landmarks ("the eastern cliffs," "the river fork")
- Large resource patches (significant berry patches, animal herding grounds)
- Home location and settlement boundaries
- Routes to known locations (implicit: knowledge of where things are enables pathfinding to them)

**What does NOT get stored:**

- Not every berry bush or wood tile. Only significant finds — resources worth remembering and telling stories about. Routine nearby foraging spots are covered by transient memory.

**How it's created:**

- When an explorer returns to the settlement after discovering a notable resource or feature, it becomes settlement lore automatically (during the knowledge propagation window described in Section 2).
- "Notable" for permanent knowledge purposes means: ore/metal deposits, stone quarries, caves, large resource patches, other settlements, and any point feature (see Section 6). Routine renewable resources on nearby tiles do not qualify.

**How it's used:**

- Any settlement resident can reference permanent knowledge when making decisions — pathfinding, resource gathering, expedition planning.
- "Grandpa told us about the copper vein in the eastern cliffs" = an agent deciding to go mine copper at a location they've never personally visited, because the settlement knows about it.
- Permanent knowledge entries never decay. The settlement remembers the copper vein forever (or until a future mechanic like settlement destruction removes it).
- If a resource deposit is confirmed depleted, the settlement lore is updated accordingly.

**Important scope note:** Permanent knowledge is per-settlement. An agent from Settlement A who visits Settlement B's structures has those in _transient_ memory (which decays), not permanent lore. It only becomes permanent settlement lore when the agent returns home and the information enters the propagation system. If the visiting agent dies before returning, Settlement A never learns about Settlement B's structures. That's geographic knowledge friction working as designed.

**Future enhancement:** With writing technology, permanent knowledge could become transferable between settlements (written records, maps, trade of information). Not in scope for v1.8.

### Layer 3: Current Perception

What the agent can see right now within their perception radius. Always takes priority over memory.

### Memory in Decision-Making

When evaluating actions, agents consider their combined knowledge: current perception (what they see now) + transient memory (what they recently saw) + permanent settlement knowledge (what the community knows). Concrete effects:

- An agent who gets hungry 7 tiles away from a berry bush they walked past 20 days ago will move back toward it instead of wandering randomly.
- An agent who remembers seeing another agent nearby will move toward their last known position, even if that agent has since moved.
- An agent always knows where their home shelter and the nearest granary are, regardless of distance. ReturnHome utility references HomeTile directly; granary withdrawal references the settlement's known structures.
- An agent can travel to a distant ore vein they've never personally visited, because the settlement's permanent knowledge includes its location.
- If the agent arrives and a remembered resource is gone, the memory clears immediately. No infinite loops chasing ghosts. For permanent settlement knowledge, if a resource deposit is confirmed depleted, the settlement lore is updated accordingly.

**Transient memory does not make agents smart. It makes them not stupid.** A 30-sim-day decay window and 30-entry cap keeps agents operating on recent, local information. Permanent settlement knowledge means they stop looking stupid by forgetting landmarks and significant resources that any real human community would remember.

## Section 6: Resource Distribution

_This is additive — it sits on top of the existing resource type tables and regeneration rates, which are unchanged. The distribution model defines which tiles get populated during world generation. The existing tables define what a tile can hold and how resources behave._

### Three-Tier Distribution Model

Resources are placed during world generation using a three-tier system. The goal is a world that feels natural — not every forest tile is identical, berry bushes grow in patches, and ore veins are rare discoveries worth remembering.

### Tier 1: Primary Resources

Appear on most tiles of the appropriate biome, but at **varying quantities**. A forest tile might have 15–20 wood, or only 5–8, depending on generation rolls. Plains tiles vary in grass and wild grain density. Mountain tiles vary in exposed stone. This creates richer and poorer areas within the same biome type, giving agents reasons to prefer some tiles over others.

- Forest: Wood (varying quantity per tile)
- Plains: Grass, sparse wild grain (varying quantity per tile)
- Mountain: Stone (varying quantity per tile)
- Desert: Sand, sparse scrub (varying quantity per tile)

### Tier 2: Secondary Resources

Appear in **patches** — clusters of 3–7 tiles placed during world generation, spaced apart within the biome. An agent needs to _find_ the berry patch, not just walk to any forest tile. Patches create named locations worth remembering ("the berry patch to the north") and drive exploration beyond the immediate settlement area.

- Berry patches in forests (3–7 tile clusters)
- Animal herds on plains and forest edges (3–5 tile clusters)
- Fish schools along water-adjacent coastlines (2–4 tile stretches)
- Wild grain concentrations on plains (3–5 tile clusters)

[RECALIBRATE] patch frequency, spacing, and quantities per biome. Target behavior: an agent within 5 tiles of their settlement should be able to find _some_ food, but not necessarily abundant food. The really good berry patches and hunting grounds require exploration.

### Tier 3: Point Features

Single tiles with rare or special resources. Placed sparingly during world generation — **1–3 per biome region** — and represent significant geographic features worth permanent settlement knowledge when discovered.

- Cave entrances in mountains (shelter potential, future mechanics)
- Ore veins exposed in cliff faces (mountain tiles — critical for Era 5+ metalworking)
- Natural springs (water access away from rivers/coastline)
- Unusually rich stone deposits (mountain tiles — large quarries)

Point features are the resources that drive expeditions and shape long-term settlement strategy. The copper vein in the eastern mountains is a real place that matters.

### What This Changes for World Generation

World generation needs a resource placement pass after biome generation:

1. Lay down primary resources with variance across all biome-appropriate tiles
2. Place secondary patches with spacing rules (clusters of 3–7 tiles, spaced apart)
3. Scatter point features sparingly (1–3 per biome region)

Key behavioral changes:

- Not every forest tile has berries — only tiles within berry patches
- Ore and stone exist in specific mountain tiles, not uniformly across all mountains
- Animals exist in herds (patches), not spread uniformly across every plains/forest tile

### Interaction with Existing Systems

- **Viability Report:** The pre-simulation viability report already scans resources within 5 tiles of spawn. The distribution model makes this scan more meaningful — a spawn near a berry patch and a stone deposit is genuinely better than one surrounded by barren forest tiles.
- **Permanent Settlement Knowledge (Section 5):** When an agent discovers a point feature or significant patch, it becomes settlement lore. "The copper vein in the eastern cliffs" is exactly the kind of knowledge that persists across generations.
- **Exploration Pressure:** Uneven distribution creates natural exploration incentive. Local resources deplete or are sparse; richer patches and rare deposits exist further out. This ties directly into the home-pull vs. explore tension in the utility system.

---

## Section 7: Home Storage

_This creates the bridge mechanic between "food in pockets" and the Era 4 granary. It's what makes the "go out, gather, bring food home" loop work in early game._

### Core Mechanic

Any shelter tile provides basic storage capacity. This requires no technology — it is the natural behavior of putting food inside your shelter to keep it safe. Home storage gives agents a reason to come home beyond just sleeping: their food is there.

### Storage Tiers

| Storage Type     | Capacity      | Decay Rate                                                 | Requires               |
| ---------------- | ------------- | ---------------------------------------------------------- | ---------------------- |
| Lean-to          | 10 food units | 1 unit per 20 sim-days (slow spoilage, minimal protection) | Any lean-to shelter    |
| Improved shelter | 20 food units | 1 unit per 30 sim-days (better insulation, less exposure)  | Improved shelter       |
| Granary          | 50 food units | No decay                                                   | Granary recipe (Era 4) |

[RECALIBRATE] decay rates against the new time model. Target behavior: food stored in a lean-to lasts long enough to be useful (weeks, not days) but slowly spoils without preservation technology. An improved shelter is noticeably better. A granary eliminates decay entirely — that's the technological payoff.

### Inventory Food Decay

Food carried in personal inventory decays at a rate of [RECALIBRATE] units per sim-day. This creates natural pressure to bring food home rather than hoarding it in pockets. The full storage progression:

| Storage Location         | Decay Rate                       | Protection Level           |
| ------------------------ | -------------------------------- | -------------------------- |
| Personal inventory       | Fastest (exposed, no protection) | None                       |
| Lean-to storage          | Slow                             | Basic shelter              |
| Improved shelter storage | Slower                           | Better insulation          |
| Granary                  | No decay                         | Purpose-built preservation |

Each tier of storage technology is a meaningful upgrade in food preservation. The exact rates are a calibration task — the target behavior is that an agent carrying raw food should feel mild pressure to return home within a day or two, not that food rots in their hands mid-gathering-trip.

### Storage Rules

- **Home storage is individual** — it belongs to the shelter's resident(s). Only agents whose HomeTile matches the shelter can deposit and withdraw.
- **Granary storage is communal** — any settlement resident can deposit or withdraw. Unchanged from the current granary specification.
- Agents deposit excess food when they return home and their inventory exceeds what they need for immediate consumption. This is not a separate action — it happens as part of the ReturnHome behavior.
- Agents eat from home storage before going out to forage. If food is available at home, there's no need to gather.
- Food in home storage is visible to the observer (small food icon or count on the shelter tile).
- Storage capacity is per-shelter, not per-agent. Two agents sharing a shelter share its storage capacity.

### New Utility Action: DepositHome

Add alongside existing DepositGranary:

| Action         | Base Utility Formula                                              | Key Inputs                                                                                                                                 |
| -------------- | ----------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------ |
| DepositHome    | 0.4 × (food > [RECALIBRATE]) × atHomeTile × (1 − homeStorageFull) | Agents at home with surplus food deposit into home storage. Lower threshold than granary deposit — agents store food at home more readily. |
| DepositGranary | 0.4 × (food > [RECALIBRATE]) × granaryInRange × (1 − granaryFull) | Unchanged from current spec. Granary preferred over home storage when both available and home storage is full.                             |

### Food-Seeking Cascade

When an agent is hungry, the food-seeking priority is:

1. Eat from personal inventory (immediate, no movement needed)
2. Eat from home storage (if at home tile)
3. Withdraw from granary (if granary in range)
4. Forage from the world (gather action)

This cascade doesn't need new utility rows — it's an extension of the existing P1 (critical hunger) and GatherFood logic. When checking for available food, the system checks inventory → home storage → granary → nearby tiles, in that order.

### What This Enables

The "go out, gather, bring food home, store it" loop that defines early settler behavior. This is the core daily routine the observer watches: an agent wakes up, eats from home storage, goes out to gather, returns with food, deposits the surplus, rests. Without home storage, agents carry everything in their pockets or rely on an Era 4 granary — there's no middle ground, and the early-game "settler with a home base" fantasy doesn't work.

Home storage also improves the food security factor in the reproduction stability score (Section 3). An agent with 8 food units stored at home has meaningfully better food security than one carrying 8 food units in inventory — stored food represents planning and surplus, not just "hasn't eaten yet."

### What Is NOT Changed

- Granary mechanics unchanged — still Era 4 tech, still communal, still 50 capacity, still no decay
- Existing DepositGranary and WithdrawGranary utility formulas preserved, just supplemented
- No shelter construction mechanics changed — home storage is automatic when shelter exists, not a separate build action

---

## Section 8: Tech Tree Visualization

_This is purely an observation/UI feature. No new mechanics. This is one of the showcase elements of CivSim — one of the things you'd screen-record to show friends. It should look good, not just be functional._

### Toggle and Display

Toggle with the **T** key. A full-screen overlay on top of the world view (world remains visible but dimmed underneath). Scrollable and zoomable.

### Layout

- **Horizontal axis:** Technology branches (Tools, Fire, Food, Shelter, Knowledge)
- **Vertical axis:** Era progression (Era 0 at top → Era 7 at bottom)
- **Nodes:** Rectangle or rounded box per recipe, showing recipe name and era number
- **Edges:** Lines connecting prerequisites to dependents. Cross-branch prerequisites (e.g., metalworking requiring fire branch + tools branch) shown as lines crossing between columns
- **Visual reference:** KSP's tech tree (interconnected node graph) combined with Rimworld's research tree (clean, readable, browsable)

### Node States

| State      | Visual Treatment                                                                                                         | Meaning                                                                                                        |
| ---------- | ------------------------------------------------------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------------- |
| Discovered | Fully lit, colored by branch. Distinct color per branch so the observer can see at a glance which domains are developed. | The settlement has this knowledge.                                                                             |
| Available  | Dimmed but visible. Slightly brighter than locked — a subtle "you could get this next" glow.                             | All prerequisites met. Could be discovered next through experimentation.                                       |
| Locked     | Dimmed, gray.                                                                                                            | Prerequisites not yet met. Still visible so the observer can see the full tree and understand what's possible. |

**The full tree is always visible.** There is no fog of war. The observer sees the entire progression from Era 0 to Era 7, with discovered nodes lighting up as the civilization advances. Watching the tree gradually illuminate is a core part of the observation experience.

### Interaction

- **Click a node:** Shows a detail panel with recipe name, inputs, prerequisites, mechanical effects, era, branch, and discovery history (who discovered it, when, which settlement). For undiscovered nodes, shows inputs and prerequisites so the observer can understand what's needed.
- **Scroll / drag:** Pan the tree view.
- **Mouse wheel:** Zoom in/out within the tree.
- **Escape or T:** Close the tree view and return to world view.

### Multi-Settlement Display

If multiple settlements exist on the map, the tree view shows the combined discovered state — a node is lit if _any_ settlement has discovered it. Future enhancement: per-settlement tree views toggled by clicking a settlement name. For v1.8, the combined view is sufficient.

### Updated Keyboard Controls

Add to the existing keyboard controls table:

| Key               | Action                                      |
| ----------------- | ------------------------------------------- |
| T                 | Toggle tech tree view (full-screen overlay) |
| 1 / 2 / 3 / 4 / 5 | Simulation speed: 1x / 2x / 5x / 10x / 20x  |

_(Speed keys updated from the old 3-tier system to the new 5-tier system from Section 1.)_

## Section 9: Milestone Discovery Announcements

_Modifies the event/notification system. Additive — no existing mechanics change._

### Discovery Announcement Levels

Each recipe in the technology tree has an **announcementLevel** field that determines how the observer is notified when it's discovered. This is set during tech tree design (a separate task) and stored as a field on the recipe definition.

| Level     | Visual Treatment                                                       | Duration                                                                  | Use                                                                                               |
| --------- | ---------------------------------------------------------------------- | ------------------------------------------------------------------------- | ------------------------------------------------------------------------------------------------- |
| MILESTONE | Large pop-up overlay, centered on screen. Visual and/or audio fanfare. | Persists for several seconds, fades out. Does not auto-dismiss instantly. | Era-defining discoveries. The moments that make you say "they finally figured it out."            |
| STANDARD  | Toast notification at top of screen.                                   | Auto-dismisses after 3–4 seconds.                                         | All other discoveries. Still visible, still logged, but doesn't interrupt the viewing experience. |

### Milestone Tagging Guidelines

Milestones are **manually tagged during tech tree construction**, not derived from a formula. The following is a guideline, not a rule — the tech tree design thread determines final assignments:

- Roughly 1 milestone per era: the discovery that defines that era's identity
- Recipes that are prerequisites for 3+ other recipes tend to be milestones (they're structural nodes in the tree)
- Examples of likely milestones: fire, farming, smelting/copper_working, writing, iron_working
- Examples of non-milestones: bone_tools, crop_rotation, weaving

**Milestone announcements should feel like events.** When a civilization that's been stuck in the stone age for 200 sim-years finally discovers smelting, the observer should feel it. The large overlay, the brief pause in the viewing rhythm, the visual emphasis — this is the payoff for hours of watching.

### Recipe Schema Update

Add an announcementLevel field to the recipe schema:

| Field             | Type                               | Description                                                                                       |
| ----------------- | ---------------------------------- | ------------------------------------------------------------------------------------------------- |
| announcementLevel | string ("MILESTONE" or "STANDARD") | Determines notification treatment on discovery. Default: "STANDARD". Set during tech tree design. |

This is additive to the existing recipe schema. All other recipe fields are unchanged.

---

## Section 10: Minor Updates

_Small changes scattered across the document. Each is self-contained._

### 10a: Version Headers

All page/section headers update from "v1.6.2" or "v1.7.2" to **v1.8**. Cover page updates from "Version 1.7.2" to "Version 1.8". Global find-and-replace.

### 10b: Emergence Sources

"Teaching as active cost" was already fully replaced in Section 2 with "Geographic knowledge friction." No additional work needed here — just confirming it's covered.

### 10c: Oral Tradition / Writing Mechanic

Already fully addressed in Section 2. oral_tradition repurposed, writing expanded. No additional work.

### 10d: Build Plan Status

Update the Phased Build Plan table to reflect actual project status:

| Phase | Deliverable                                                             | Status      |
| ----- | ----------------------------------------------------------------------- | ----------- |
| 1     | The World — Grid, biomes, resources, Raylib rendering, viability report | COMPLETE    |
| 2     | The Agents — Needs, actions, decisions, reproduction, death, logging    | COMPLETE    |
| 3     | Tech / Discovery — Recipes, experimentation, knowledge, progression     | IN PROGRESS |
| 4     | Communities — Groups, roles, shared goals, territory                    | PLANNED     |
| 5     | Events — Disasters, plagues, windfalls, environmental shifts            | FUTURE      |
| 6     | Visual Layer — GUI or Unity viewer for real-time observation            | FUTURE      |

### 10e: Success Metrics

Update the Success Metrics table with vision-aligned checks (bolded additions sit alongside existing technical checks):

| Phase           | It Works When...                                                                                                                                                                                                                                                                                                                                                                 |
| --------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Phase 1: World  | You generate a world, see it rendered via Raylib with distinct biome regions, read a viability report, and resource counts match biome expectations. **Resources are distributed in natural patterns — not every tile is identical.**                                                                                                                                            |
| Phase 2: Agents | Agents spawn, survive by gathering food, reproduce, and eventually some die. Population fluctuates. Extinction runs and growth runs both occur. **Agents exhibit home-base behavior: go out, come back, store food. Reproduction correlates with stability, not just proximity.**                                                                                                |
| Phase 3: Tech   | Agents discover basic tools and fire through experimentation. Knowledge spreads through communal settlement propagation. **New discoveries visibly change agent behavior (faster gathering, cooking, building). Discovery announcements create genuine moments of excitement.**                                                                                                  |
| Full Loop       | You start a run, walk away, come back, and read a summary that tells an interesting story of a civilization's rise, struggle, or fall. Two runs with different seeds produce different stories. **Watching a run for 10 minutes feels like watching people live, not numbers optimize. A new viewer can glance at the screen and understand what's happening (10-second test).** |

### 10f: Social Bonds — Deferred Cleanup

The Social Bonds system references Teach interactions for bond formation (InteractionCount increments "when two agents teach each other, rest adjacently, or gather on the same tile"). With Teach removed, bond formation now comes from: resting adjacently, gathering on the same tile, and sharing food. This is noted for awareness but **a full Social Bonds redesign is deferred to a future pass** — the system as a whole will benefit from holistic rethinking rather than piecemeal fixes.

---

## RECALIBRATE Tracker

**Every value below needs math work in a dedicated calibration pass.** These are placeholders with target behaviors described. The calibration task derives exact numbers that produce the intended feel at 1x viewing speed.

### Time Model Constants

| Value                            | Target Behavior                                          | Section |
| -------------------------------- | -------------------------------------------------------- | ------- |
| Ticks per sim-hour (internal)    | Enough ticks for smooth animation; invisible to observer | 1       |
| Starvation damage per sim-day    | ~2 sim-days from full health to death at hunger 0        | 1       |
| Death report duration            | ~5 sim-days visible after death                          | 1       |
| Craft simple item duration       | ~1–3 hours per recipe, varies by complexity              | 1       |
| Craft complex item duration      | ~4–8 hours per recipe                                    | 1       |
| Build walls duration             | Multi-day project, TBD                                   | 1       |
| Build communal building duration | Multi-day project, TBD                                   | 1       |
| Preserve food duration           | ~2–4 hours                                               | 1       |
| Tend farm duration               | ~2–4 hours                                               | 1       |

### Knowledge System Constants

| Value                                      | Target Behavior                                    | Section |
| ------------------------------------------ | -------------------------------------------------- | ------- |
| Oral propagation probability curve         | Proportional to window completion; exact shape TBD | 2       |
| Partial loss reduction from oral_tradition | Meaningful improvement over base; exact value TBD  | 2       |

### Reproduction Constants

| Value                              | Target Behavior                                                 | Section |
| ---------------------------------- | --------------------------------------------------------------- | ------- |
| Food security scoring curve        | Growing reserves = high, depleting = near zero                  | 3       |
| Shelter quality exact scores       | Lean-to ~0.4, improved ~0.7, improved + walls ~0.9              | 3       |
| Dependent reduction per child      | 3+ young children → score approaches 0                          | 3       |
| Health trend scoring curve         | Stable/improving = high, declining = low, below 50 = sharp drop | 3       |
| Reproduction success chance base % | Configurable, modified by shelter quality                       | 3       |
| Reproduction food cost             | Consumed from parents' combined inventory/storage               | 3       |

### Memory Constants

| Value                 | Target Behavior                                            | Section |
| --------------------- | ---------------------------------------------------------- | ------- |
| MEMORY_DECAY_DURATION | ~30 sim-days (~1 month); agent forgets old berry sightings | 5       |

### Resource Distribution Constants

| Value                              | Target Behavior                                                          | Section |
| ---------------------------------- | ------------------------------------------------------------------------ | ------- |
| Primary resource quantity variance | Meaningful range per biome (e.g., 5–20 wood per forest tile)             | 6       |
| Secondary patch frequency/spacing  | Some food within 5 tiles of settlement; good patches require exploration | 6       |
| Secondary patch size ranges        | Berry: 3–7 tiles, herds: 3–5, fish: 2–4, grain: 3–5                      | 6       |
| Point feature frequency            | 1–3 per biome region; sparingly placed                                   | 6       |

### Home Storage Constants

| Value                         | Target Behavior                                               | Section |
| ----------------------------- | ------------------------------------------------------------- | ------- |
| Lean-to decay rate            | ~1 unit per 20 sim-days; food lasts weeks, not days           | 7       |
| Improved shelter decay rate   | ~1 unit per 30 sim-days; noticeably better than lean-to       | 7       |
| Inventory food decay rate     | Mild pressure to return home within 1–2 days; not instant rot | 7       |
| DepositHome food threshold    | Lower than granary deposit; agents store food readily         | 7       |
| DepositGranary food threshold | Current value, recalibrated to new time model                 | 7       |

### Ecology Constants (Carry-Forward Recalibration)

| Value                           | Target Behavior                           | Section                    |
| ------------------------------- | ----------------------------------------- | -------------------------- |
| All resource regeneration rates | Convert from tick-based to sim-day-based  | Ecology (unchanged system) |
| Overgrazing recovery time       | Convert from ticks to sim-days            | Ecology (unchanged system) |
| Exposure damage rate            | Convert from ticks to sim-days            | Ecology (unchanged system) |
| Health regeneration rate        | Convert from ticks to sim-days            | Ecology (unchanged system) |
| Hunger drain rate               | Convert from ticks to sim-days            | Ecology (unchanged system) |
| Food restore values             | Recalibrate against new hunger drain rate | Ecology (unchanged system) |

### Diagnostics Constants

| Value                        | Target Behavior                          | Section                        |
| ---------------------------- | ---------------------------------------- | ------------------------------ |
| DIAGNOSTIC_SAMPLE_INTERVAL   | Convert from ticks to sim-day equivalent | Diagnostics (unchanged system) |
| Pressure map update interval | Convert from ticks to sim-day equivalent | Diagnostics (unchanged system) |

---

## Implementation Order

Recommended order based on dependencies:

1. **Time Model (Section 1)** — Everything depends on this. Calendar, action durations, lifespan tables, speed tiers, auto-save.
2. **Knowledge System (Section 2)** — Remove Teach, implement communal propagation, update all ripple effects.
3. **Reproduction (Section 3)** — Stability score system. Depends on home storage existing for food security scoring, so coordinate with Section 7.
4. **Agent Memory (Section 5)** — Three-layer model. Permanent settlement knowledge connects to Knowledge System.
5. **Resource Distribution (Section 6)** — World generation changes. Connects to permanent knowledge (point features become settlement lore).
6. **Home Storage (Section 7)** — Storage tiers, deposit/withdrawal, food cascade. Connects to reproduction stability scoring.
7. **Agent Names (Section 4)** — Independent, can be done anytime. Quick task.
8. **Tech Tree Visualization (Section 8)** — UI work, independent of simulation logic.
9. **Milestone Announcements (Section 9)** — Depends on tech tree design thread tagging recipes. Can implement the system with placeholder tags.
10. **Minor Updates (Section 10)** — Headers, build plan, success metrics. Do last.
11. **Calibration Pass** — After all systems are implemented with placeholders, run the math to derive exact constants. Use the RECALIBRATE Tracker above as the task list.

---

_This completes the v1.8 Coder Implementation Package. All content was produced from approved design decisions made during the Vision Reconciliation sessions of February 2026. If anything in this document conflicts with CLAUDE.md, CLAUDE.md wins._
