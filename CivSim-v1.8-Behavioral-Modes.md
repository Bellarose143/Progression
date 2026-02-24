# CivSim v1.8 — Decision System Overhaul: Behavioral Modes

**Date:** February 2026  
**Status:** Approved by project creator and senior team member  
**Supersedes:** All previous utility scoring patches, Playtest Directive #2 systemic fixes (home-pull, idle cascade, parent-child feeding). Those directives described the correct behaviors — this document provides the structural solution that produces them.

---

## Why This Change

Two playtests and dozens of scorer patches proved that open-field utility scoring (15+ scorers competing every tick) does not produce settler behavior. Specific failures:

- **Oscillation:** Agents flip between actions every tick because two scorers produce near-identical values
- **Coin-flip decisions:** 22% of choices had margins under 0.05 — effectively random
- **Cascading patches:** Fixing one scorer (boost Build when exposed) destabilizes another (agents stop gathering materials). Every fix creates a new edge case.
- **No behavioral continuity:** Agents have no concept of "I'm on a gathering trip" or "I'm working on a building project." Each tick is an isolated decision with no memory of intent.

The underlying problem is structural. Agents need intent, not just scores.

## The Core Idea

Replace tick-by-tick open scoring with a **mode-based decision system.** An agent is always in exactly one mode. Each mode defines:

- Which actions are **available** (everything else is suppressed entirely, not just scored low)
- What **triggers entry** into the mode
- What **triggers exit** from the mode (and which mode to transition to)

Within a mode, utility scoring still operates — choosing which food to gather, which recipe to experiment with, which direction to walk. But the mode constrains the *category* of behavior. The agent in Forage mode never considers experimenting. The agent in Home mode never considers exploring. This eliminates cross-category score competition, which eliminates oscillation.

Mode transitions are **explicit and testable.** "Exit Forage when inventory food > 8 or hunger < 45" is a clear rule you can write a unit test for. "Agent happened to score ReturnHome 0.02 higher than Gather" is not.

---

## Mode Definitions

### Mode: Home

**Identity:** "I'm at my settlement, doing settlement things."

**Available actions:** Eat, Rest, Experiment, Craft, Deposit (food/materials into home storage), Socialize, small maintenance tasks.

**Entry conditions:**
- Agent arrives at HomeTile (from any other mode's return trip)
- Simulation start (default mode before first shelter exists — see "Pre-Shelter" note below)

**Exit conditions:**

| Condition | Transitions To | Logic |
|-----------|---------------|-------|
| Food inventory below threshold | Forage | Agent needs to go gather. Threshold: personal food < 3 AND home storage food < 5. [RECALIBRATE] |
| Materials needed for a known project | Forage | Agent knows a recipe to build/craft but lacks materials. Forage with specific resource target. |
| Agent decides to start a Build project | Build | Triggered by utility scoring within Home mode. Build scores high when: agent has materials, shelter needs improvement, settlement needs structures. |
| Agent has infant/youth dependent | Caretaker | Automatic on BIRTH event for both parents. See Caretaker mode. |
| Exploration conditions met | Explore | Sheltered, hunger > 80, health > 80, surplus food (inventory food > 6), no dependents in Caretaker range, no urgent build projects. Explorer trait lowers hunger/health thresholds to 70. |
| Survival crisis | Urgent | Hunger < 25 or health < 20. |

**Within-mode scoring:** Experiment competes with Rest, Socialize, Deposit, Craft. Curious trait boosts Experiment. Social trait boosts Socialize. Builder trait boosts small maintenance tasks. Night hours boost Rest and suppress Experiment. This is where the "idle cascade" from Directive #2 lives — it's just the natural scoring within Home mode.

**Key behavior:** Agent stays at settlement. Productive but localized. This is what the observer sees most of the time — agents living at home, tinkering, resting, chatting, experimenting between meals. The daily rhythm.

---

### Mode: Forage

**Identity:** "I'm on a trip to gather something specific and bring it home."

**Available actions:** Move (toward target), Gather, Eat (from inventory only, emergency).

**Entry conditions:**
- Exiting Home mode due to food or material need
- Exiting Caretaker mode for a short supply run (tighter range — see Caretaker)

**On entry, the agent commits to:**
- A **target resource type** (food, wood, stone, ore)
- A **target location** (known resource tile from memory/perception, or nearest tile with the right resource)
- A **return threshold** (inventory quantity that triggers return — e.g., "gather until I have 6 food, then go home")

**Exit conditions:**

| Condition | Transitions To | Logic |
|-----------|---------------|-------|
| Return threshold met | Home (via return trip) | Got enough. Walk home. |
| Inventory full | Home (via return trip) | Can't carry more. Walk home. |
| Target resource depleted | Home (via return trip) or pick new target | Arrived but resource is gone. Check memory for alternatives. If none in reasonable range, go home. |
| Hunger < 45 | Home (via return trip) | Getting hungry. Eat from inventory if possible, but head home regardless. |
| Survival crisis (hunger < 25 or health < 20) | Urgent | Drop everything. |
| Forage duration exceeds budget | Home (via return trip) | Safety valve. If agent has been in Forage for 200+ ticks (~7 sim-hours) without returning, force return. Prevents infinite gather-wander. [RECALIBRATE] |

**Within-mode scoring:** Which specific tile to gather from (closest, richest, on the path home). Whether to grab opportunistic resources on the way (walking past stone while heading to berries — grab it if you have room).

**Key behavior:** The go-out-and-come-back loop. Agent leaves home with a purpose, walks to the target, gathers, walks home. Every forage trip has a destination and a return. This is the core behavioral loop the observer watches — and it was impossible under open scoring because ReturnHome kept losing to Gather by 0.02.

**Note on target selection:** When entering Forage, the agent should prefer resource locations in directions it hasn't recently visited (anti-backtrack from previous directives still applies within mode). Resources closer to home are preferred over distant ones (home-pull from Directive #2 is now structural — Forage trips radiate from home and return).

---

### Mode: Build

**Identity:** "I'm working on a construction project until it's done."

**Available actions:** Build (the specific project), Gather materials (if ran out mid-project), Eat, Rest (at night).

**Entry conditions:**
- Agent in Home mode decides to start a multi-tick Build project (shelter, walls, granary, monument, etc.)
- Agent has sufficient materials to begin (or most of them — can gather remainder mid-project)

**On entry, the agent commits to:**
- A **specific project** (recipe ID and target tile)
- The project persists until complete — progress is stored on the tile

**Exit conditions:**

| Condition | Transitions To | Logic |
|-----------|---------------|-------|
| Project complete | Home | Structure built. Celebrate (figuratively). |
| Night time | Home (temporary) | Go home, rest, eat. Resume Build mode in the morning — project persists on the tile. |
| Out of materials | Forage (with specific material target) | Need 3 more stone. Go get stone. Come back and resume. |
| Hunger < 45 | Home or Forage | Eat, resupply, then resume Build. |
| Survival crisis | Urgent | Drop tools. |

**Within-mode scoring:** Minimal. The agent is committed to the project. The only real decisions are "keep building" vs "I need to eat/rest/get materials." This produces the behavior the observer wants to see: an agent working on a shelter all afternoon, taking a break to eat, sleeping, then getting back to it in the morning. Not placing one block, wandering off, coming back 3 sim-hours later.

**Key behavior:** Sustained, visible construction. Multi-day projects (walls, granary) span several Home→Build cycles. The observer can track progress on the tile.

**Build location constraint (from Directive #2):** Build mode can only target tiles at or adjacent to the agent's HomeTile. Exception: agent has no HomeTile (first-ever shelter — build anywhere). An agent 30 tiles from home does NOT enter Build mode. They go home first.

---

### Mode: Explore

**Identity:** "I'm scouting in a committed direction to see what's out there."

**Available actions:** Move (sustained direction), Gather (opportunistic only — grab berries you walk past, don't detour for them), Eat (from inventory).

**Entry conditions (ALL must be true):**
- Agent has a HomeTile (can't explore if you don't have a home to return to)
- Hunger > 80 (well-fed)
- Health > 80 (healthy)
- Inventory food > 6 (packed lunch for the trip)
- No dependents in need (no children in Caretaker range, or co-parent is in Caretaker mode covering duties)
- No urgent build projects at settlement
- Explorer trait lowers hunger/health thresholds to 70 and food threshold to 4

**On entry, the agent commits to:**
- A **direction** (chosen from unexplored or least-recently-visited directions)
- A **tick budget** (how long to explore before mandatory return). Default: 300 ticks (~10 sim-hours, roughly a full day's travel). Explorer trait extends to 500 ticks. [RECALIBRATE]

**Exit conditions:**

| Condition | Transitions To | Logic |
|-----------|---------------|-------|
| Tick budget expired | Home (via return trip) | Time's up. Head home. |
| Found something significant | Home (via return trip) | Discovered an ore vein, new biome edge, another settlement, or a point feature. Return to share knowledge. |
| Hunger < 50 | Home (via return trip) | Running low. Head back before it becomes urgent. |
| Health < 50 | Home (via return trip) | Injured or sick. Go home. |
| Survival crisis | Urgent | Emergency. |

**Within-mode scoring:** Direction preference (unexplored > least-recent > toward different biome). Opportunistic gathering (grab things on the path, don't detour).

**Key behavior:** Purposeful scouting, not aimless wandering. The agent picks a direction and commits. They walk outward for hours, observe what they find, and come home. The tick budget guarantees return. The entry conditions guarantee they only do this when it's safe.

**Critical constraint:** Explore mode does NOT allow building. An explorer does not build a lean-to 200 tiles from home. If they need shelter, they go home. This directly prevents the "chain of lean-tos across the desert" anti-pattern from playtest 2.

**Return trip knowledge:** When the agent transitions back to Home, they carry all observations from the trip. Upon arriving home, new resource locations and point features enter settlement permanent knowledge via the propagation system. "Grandpa told us about the copper vein in the eastern cliffs" — this is how that happens.

---

### Mode: Caretaker

**Identity:** "I have young children. Everything I do is shaped by that."

**Available actions:** Everything available in Home mode, PLUS: Feed Child, short-range Forage. MODIFIED: Forage range is reduced, return thresholds are tighter, check-on-child triggers are frequent.

**Entry conditions:**
- Agent has an infant or youth child (via Relationships dictionary)
- Automatic on BIRTH event for both parents

**Exit conditions:**

| Condition | Transitions To | Logic |
|-----------|---------------|-------|
| Youngest dependent ages past care threshold | Home | Kids are old enough. Resume normal life. Care threshold: age 4 sim-years (~112 sim-days). [RECALIBRATE] |
| All dependents dead | Home | Tragic but possible. |
| Survival crisis (own) | Urgent | Self-preservation. |

**How Caretaker modifies behavior:**

Caretaker is not a completely separate behavior set — it's a **lens** over other modes. A caretaker-mode agent still forages, rests, and can experiment. But:

- **Forage range is halved.** Maximum forage distance from home: 8 tiles (vs normal 15-20). The parent doesn't go on long gathering trips. They gather nearby and come back quickly. [RECALIBRATE]
- **Return threshold is lower.** Come home with 4 food instead of waiting for 8. Shorter trips, more frequent returns.
- **Child hunger check every 30 ticks.** The agent periodically checks their child's hunger. If child hunger < 50, transition to "feed child" behavior immediately — go home if not there, feed the child from inventory or home storage.
- **Cannot enter Explore mode.** Parents don't go on scouting expeditions while they have young children. Period. (Unless co-parent is also in Caretaker mode and is currently at home — then ONE parent can explore. They coordinate implicitly by the co-parent's presence at home.)
- **Cannot enter Build mode for distant projects.** Build only at/adjacent to HomeTile. No construction field trips.

**Infant auto-feeding from home storage:** Independent of Caretaker mode — if an infant is at HomeTile and home storage has food, the infant eats automatically (1 unit per feeding, triggers when infant hunger < 40). This means a well-stocked home buys time even if the caretaker parent is on a brief forage trip.

**Two-parent coordination:** When both parents are in Caretaker mode, only one needs to be at home at any given time. If Parent A is at home with the child, Parent B can forage normally (within Caretaker range limits). If Parent A leaves, Parent B should return. This doesn't need to be explicitly coordinated — the child hunger check naturally pulls whichever parent is further away back toward home when needed.

**Key behavior:** The observer sees parents staying close to home, making short trips, frequently returning to feed the baby. It looks like parenting. The child grows up safely. This was impossible under open scoring — both parents would wander off and the baby would starve.

---

### Mode: Urgent

**Identity:** "I'm in immediate danger. Everything else stops."

**Available actions:** Eat (from inventory), Seek Food (if starving), Seek Shelter (if exposed and health critical), Rest (if health critical and sheltered).

**Entry conditions (from ANY other mode):**
- Hunger < 25 (starving)
- Health < 20 (near death)
- Other future triggers: predator in perception range, environmental hazard

**Exit conditions:**

| Condition | Transitions To | Logic |
|-----------|---------------|-------|
| Hunger > 40 AND health > 30 | Previous mode | Crisis resolved. Resume what you were doing. |
| All food sources exhausted in range | Explore (desperation) | Nothing nearby. Wander outward looking for anything. This is the "dying agent searches for food" behavior — they don't freeze in place. |

**Key behavior:** Pure survival. The agent drops everything and addresses the crisis. This is the old P0/P1 priority tier, but now it's a mode with clear entry/exit rather than a score that competes with 15 other scores.

**Urgent overrides all other modes unconditionally.** A building agent who hits hunger 25 drops their tools. A foraging agent who hits health 20 stops gathering. No score competition. No coin flips.

---

## Pre-Shelter Special Case

At the very start of a run, agents have no shelter and no HomeTile. The mode system needs to handle this gracefully.

**Starting mode:** Home (even though there's no physical home yet).

**Modified Home behavior when no shelter exists:**
- Build is the top-priority action within Home mode
- Experiment is available (they need to discover lean_to if they don't know it)
- Forage for food and building materials
- The "settlement area" is wherever the founding pair is standing — they orbit their starting position

**Once the first shelter is built:**
- Both agents' HomeTile is set to the shelter location
- Normal mode transitions begin
- The settlement exists

This handles the bootstrapping problem without needing a separate "Founding" mode.

---

## Mode Transition Hysteresis

**Critical implementation detail.** Mode transitions must have buffer zones between entry and exit thresholds to prevent mode oscillation.

Example without hysteresis:
- Forage exit: hunger < 45 → transition to Home
- Forage entry: food inventory < 3 → transition to Forage  
- Agent's hunger hits 44 → exits Forage → arrives Home → eats → hunger goes to 55 → but home storage is empty → food inventory still < 3 → enters Forage → hunger drops to 44 → exits Forage → loop

Example with hysteresis:
- Forage exit: hunger < 45 → transition to Home
- Forage entry: food inventory < 3 AND hunger > 55 → transition to Forage
- The 10-point gap (45 to 55) prevents the bounce

**Every entry/exit threshold pair needs a buffer zone.** The exact values are in the [RECALIBRATE] pass, but the architecture must support per-transition hysteresis from the start.

---

## Mode Transition Diagram

```
                    ┌─────────────┐
         ┌─────────│   URGENT    │──────────┐
         │         └──────┬──────┘          │
         │                │ crisis resolved  │
         │                ▼                  │
         │         (previous mode)           │
         │                                   │
    hunger<25     ┌──────────────┐     hunger<25
    health<20     │              │     health<20
         │        │     HOME     │          │
         │        │              │          │
         ├────────┤  experiment  ├──────────┤
         │        │  rest, eat   │          │
         │        │  socialize   │          │
         │        │  craft       │          │
         │        └──┬──┬──┬──┬─┘          │
         │           │  │  │  │             │
         │    food   │  │  │  │ explore     │
         │    low    │  │  │  │ conditions  │
         │     ▼     │  │  │  ▼             │
    ┌────┴────┐     │  │  │  ┌──────────┐  │
    │ FORAGE  │     │  │  │  │ EXPLORE  │──┘
    │         │     │  │  │  │          │
    │ gather  │     │  │  │  │ scout    │
    │ move    │     │  │  │  │ move     │
    │ return  │     │  │  │  │ return   │
    └────┬────┘     │  │  │  └─────┬────┘
         │          │  │  │        │
         │ target   │  │  │ budget │
         │ met      │  │  │ expired│
         └──────────┘  │  └────────┘
                       │
              build    │  has
              project  │  dependents
                 ▼     ▼
           ┌────────┐ ┌───────────┐
           │ BUILD  │ │ CARETAKER │
           │        │ │           │
           │ build  │ │ home+     │
           │ eat    │ │ short     │
           │ gather │ │ forage    │
           │ rest   │ │ feed      │
           └───┬────┘ └─────┬─────┘
               │             │
            project       youngest
            complete      ages out
               │             │
               └──────┬──────┘
                      ▼
                    HOME
```

---

## What This Replaces

- **All 15+ independent utility scorers competing in open field.** Scorers still exist but only run within their mode's action set.
- **All per-scorer patches** (explore suppression, build boost when exposed, anti-backtrack dampening, night suppression, home-pull scoring). These behaviors are now structural consequences of the mode system.
- **ReturnHome as a competing action.** Return is now a mode transition, not a score.
- **Priority cascade (P0-P4).** Replaced by Urgent mode + mode-specific action availability.
- **The idle cascade from Directive #2.** That cascade IS Home mode's internal scoring.
- **Home-pull scoring from Directive #2.** Home-pull IS the Forage→Home transition and the Caretaker range limit.
- **Parent-child feeding from Directive #2.** That IS Caretaker mode.
- **Day/night rest from Directive #2.** Night triggers Home mode (from Build) and boosts Rest scoring within Home mode.

The directives described the right behaviors. This document provides the architecture that produces them reliably.

## What Stays the Same

- Utility scoring within each mode (choosing which food, which recipe, which direction)
- Goal commitment system for multi-tile movement (still needed within Forage/Explore)
- All game mechanics (building, experimenting, cooking, crafting, reproduction)
- Knowledge propagation system
- Reproduction stability scoring
- World generation, rendering, resource distribution
- Tech tree, recipes, all content

## What Changes in Code

**New:**
- `BehaviorMode` enum: Home, Forage, Build, Explore, Caretaker, Urgent
- `ModeTransitionManager` or equivalent: evaluates transition conditions each tick, manages entry/exit
- Per-mode action availability lists
- Per-mode entry/exit threshold constants (with hysteresis support)
- Agent property: `CurrentMode` (persisted, included in run logger)

**Modified:**
- `AgentAI.cs`: Decision cycle changes from "run all scorers, pick highest" to "check mode transitions, then score within current mode's action set"
- `UtilityScorer.cs`: Scorers reorganized by mode. Each scorer tagged with which mode(s) it applies to. Unused scorers not called.
- Agent state: track mode, mode entry tick, mode-specific committed targets (forage target, build project, explore direction/budget)

**Removed:**
- Cross-scorer suppression patches (explore dampening, build boost when exposed, etc.)
- Anti-oscillation patches (anti-backtrack penalties, goal staleness timeouts as primary fix)
- ReturnHome as a standalone scored action

---

## Updating the Test Suite

The behavioral test suite from the Testing Infrastructure spec should be updated:

- **Survival tests** → verify Urgent mode triggers and exits correctly
- **Home-pull tests** → verify Forage→Home transitions and Caretaker range limits
- **Oscillation tests** → verify mode commitment prevents direction reversals
- **Parent-child tests** → verify Caretaker mode entry on birth and feeding behavior
- **Rest tests** → verify night triggers Home mode transitions from Build

**New mode-specific tests needed:**

- Mode transition tests: verify each entry/exit condition fires correctly
- Hysteresis tests: verify agents don't bounce between modes at thresholds
- Explore budget test: verify explorer returns home when budget expires
- Build persistence test: verify multi-day projects resume after overnight rest
- Caretaker range test: verify parent forage distance is limited while in Caretaker mode
- Pre-shelter test: verify agents bootstrap correctly (discover lean_to, build, transition to normal modes)

---

## Run Logger Update

Add `CurrentMode` as a column in DECISION events. This is critical for analysis — the run analyzer needs to see which mode the agent was in when they made each decision. The analyzer skill's behavioral expectations should be updated to check mode-appropriate behavior rather than raw action scores.

---

## [RECALIBRATE] Values for Modes

| Value | Target Behavior | Mode |
|-------|----------------|------|
| Food inventory threshold for Forage entry | Low enough that agents forage proactively, not in crisis | Home→Forage |
| Home storage threshold for Forage entry | Agent considers home reserves, not just pockets | Home→Forage |
| Hunger threshold for Forage exit | Head home before starving, with hysteresis buffer | Forage |
| Forage return food threshold | How much food triggers "I have enough, go home" | Forage |
| Forage maximum duration (ticks) | Safety valve — force return after ~7 sim-hours | Forage |
| Explore hunger/health entry thresholds | Well-fed and healthy before exploring | Home→Explore |
| Explore food inventory entry threshold | Packed lunch for the trip | Home→Explore |
| Explore tick budget (default) | ~10 sim-hours, full day's travel | Explore |
| Explore tick budget (Explorer trait) | Extended scouting, ~15 sim-hours | Explore |
| Caretaker forage range (tiles) | Short trips — ~8 tiles max | Caretaker |
| Caretaker forage return threshold | Come home sooner — ~4 food | Caretaker |
| Caretaker child hunger check interval | Every ~30 ticks | Caretaker |
| Care threshold age (sim-days) | When children no longer need active care — ~112 sim-days (4 years) | Caretaker |
| Urgent entry: hunger threshold | ~25 | Any→Urgent |
| Urgent entry: health threshold | ~20 | Any→Urgent |
| Urgent exit: hunger threshold | ~40 (with hysteresis above entry) | Urgent→Previous |
| Urgent exit: health threshold | ~30 (with hysteresis above entry) | Urgent→Previous |
| Night hour start (tick within sim-day) | ~360 (75% through the day) | Build→Home |
| Night hour end (tick within sim-day) | ~120 (25% through the day) | Home (rest ends) |

---

*This spec was produced from a collaborative design session between the project creator, the coder, and the senior team member, February 2026. It replaces the patch-based approach to behavioral tuning with a structural solution. If anything here conflicts with CLAUDE.md, CLAUDE.md wins.*
