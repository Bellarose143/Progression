# CLAUDE.md — CivSim Project Instructions

## What Is This File?

This is the binding contract between the project vision and any AI agent working on CivSim. Every coding decision, every feature implementation, every architectural choice gets measured against this document. If something conflicts with this file, this file wins.

---

## Agent Workflow (Ralph Loop)

You are an autonomous coding agent. Follow this workflow exactly.

### Your Task

1. Read this entire CLAUDE.md first — the vision, principles, and anti-patterns below are non-negotiable
2. Read the PRD at `prd.json`
3. Read the progress log at `progress.txt` (check Codebase Patterns section first)
4. Check you're on the correct branch from PRD `branchName`. If not, check it out or create from main.
5. Pick the **highest priority** user story where `passes: false`
6. Implement that single user story
7. Quality checks:
   - `dotnet build` → must produce 0 errors
   - `dotnet test` → all existing tests must pass
   - If either fails, do NOT mark passes: true. Document the failure in progress.txt and exit cleanly.
8. Update CLAUDE.md files if you discover reusable patterns (see Progress Reporting below)
9. If checks pass, commit ALL changes with message: `feat: [Story ID] - [Story Title]`
10. Update the PRD to set `passes: true` for the completed story
11. Append your progress to `progress.txt`

### Progress Report Format

APPEND to progress.txt (never replace, always append):

```
## [Date/Time] - [Story ID]
- What was implemented
- Files changed
- **Learnings for future iterations:**
  - Patterns discovered (e.g., "this codebase uses X for Y")
  - Gotchas encountered (e.g., "don't forget to update Z when changing W")
  - Useful context (e.g., "the evaluation panel is in component X")
---
```

The learnings section is critical — it helps future iterations avoid repeating mistakes and understand the codebase better.

### Consolidate Patterns

If you discover a **reusable pattern** that future iterations should know, add it to the `## Codebase Patterns` section at the TOP of progress.txt (create it if it doesn't exist). This section should consolidate the most important learnings:

```
## Codebase Patterns
- Example: Use separate RNG instances for optional systems to avoid cascade
- Example: Always call Pen.ResetIdCounter() in test setup alongside Agent/Animal/Carcass
- Example: New scored actions must not emit zero-score entries (causes RNG cascade)
```

Only add patterns that are **general and reusable**, not story-specific details.

### Quality Requirements

- ALL commits must pass quality checks (build, test)
- Do NOT commit broken code
- Keep changes focused and minimal
- Follow existing code patterns

### Stop Condition

⛔ HARD STOP AFTER ONE STORY ⛔

After completing ONE user story and committing:
1. Write your progress report to progress.txt
2. If ALL stories have `passes: true`, output: <promise>COMPLETE</promise>
3. Otherwise — STOP. Do not read prd.json again. Do not look at other stories. Do not continue working. Just stop.

The bash loop will spawn a fresh instance for the next story. Your job for this iteration is done. Continuing past one story is a violation of this contract.

### Important Rules

- Work on ONE story per iteration
- Commit frequently
- Keep CI green
- Read the Codebase Patterns section in progress.txt before starting
- If a story requires a design decision not covered by the GDD, specs, or this file, do NOT invent an answer. Write the question to progress.txt and skip that story — it will be reviewed by the human before the next run.
- Do not implement placeholder or stub implementations. Full implementations only.
- Do not add features not in the current PRD story, even if they seem obviously needed. Flag them in progress.txt instead.

---

## Project Identity

**CivSim** is an emergent civilization simulator. Two human agents are dropped into a procedural world with nothing. They gather resources, discover technology, reproduce, form communities, and build toward civilization across generations. There are no scripted tech trees, no predetermined outcomes. Each run is unique.

The simulation answers one question: **if people started from absolute zero, what would happen?**

**Platform:** .NET Console Application (C#), Raylib-cs for 2D rendering
**Status:** Long-term hobby project, built iteratively
**Current GDD:** See the latest versioned PDF in the project files. The GDD is the specification. This file is the spirit.

---

## The Vision (Read This First, Read This Often)

The creator's vision, in their own words:

> Imagine a simulation where 2 people were placed into a completely fresh world (like Adam and Eve), and progressed through the tiers of discovery and eras of civilization while slowly but steadily having kids/families to create a larger community over time who can carry on their mission/legacy.

The core fantasy is **generational progression**. It is NOT about:
- Optimizing survival metrics
- Maximizing population growth
- Speedrunning through tech tiers
- Creating the most efficient agent AI

It IS about:
- Watching a lineage struggle, learn, build, and pass things forward across lifetimes
- The meaningful journey BETWEEN discoveries (not just the discoveries themselves)
- Agents behaving like people with homes, routines, and responsibilities
- Each technological step feeling earned through time and effort
- A contemplative, watchable experience where you form attachment to characters

### The Pacing Principle

The team has been told multiple times: **the creator does not care if a run takes 50 minutes, or requires multiple sessions across days.** Do not compress, rush, or optimize away the lived experience of agents between milestones. The journey between "discovered fire" and "figured out clay hardening" is content, not dead air to skip past.

A satisfying run looks like: "I watched for 30 minutes. They found shelter, started storing food, figured out crude tools. I paused and came back the next evening. Their kids are grown now, someone discovered pottery. I'm invested in these people."

**If a feature exists to make the simulation faster/shorter rather than better/deeper, it is wrong.**

---

## Design Principles (Non-Negotiable)

### 1. Agents Are Settlers, Not Random Walkers

Agents have a HOME. They go out and come back. They store things at home. They craft at home. They sleep at home.

- All behavior radiates from the home/shelter location
- Longer trips require planning: carrying rations, resting overnight (lean-to or carried sleeping gear)
- Exploration expands gradually outward from home as the family stabilizes
- Nobody takes a week-long expedition when they can barely feed themselves
- The copper deposit a day away is a REAL expedition with risk and preparation

**Test:** If you watch an agent for 100 ticks and they never return to or reference their home, something is broken.

### 2. Reproduction Is a Survival Calculus, Not a Timer

Agents should evaluate whether they can sustain a child before having one. This is situational:
- Bad seed, barely surviving? No kids for a long time. Responsible adults don't have children they can't feed.
- Spawned near abundant resources with solid shelter? Earlier reproduction makes sense.
- Factors: food surplus/security, shelter quality, workload capacity, existing dependents

**This is NOT:** a cooldown timer + proximity check + food threshold. It is agents making a responsible decision based on their actual situation.

**Test:** If two agents reproduce while they themselves are hungry or homeless, something is broken.

### 3. The World Is a Place, Not a Spreadsheet

Think West Virginia from a plane: forested mountains. On the ground: trees, rocks, caves, a river, animals, ore veins in cliff faces. The biome is the macro identity; resources are discovered through exploration within it.

- Resources exist in natural formations and patches, not uniformly stuffed into every tile
- A forest tile is primarily trees, but might contain rocks, a cave entrance, or animals
- Resources do NOT respawn quickly. Depletion drives exploration and migration
- Ore exists underground in mountains BEFORE anyone knows how to mine. The world has latent potential that unlocks as knowledge grows
- Geography matters: mountains are obstacles, rivers are features that shape settlement location, "as the crow flies" ≠ actual travel distance

**Test:** If you zoom out and every tile looks the same, or if an agent never needs to travel more than 1 tile for any resource, the world is wrong.

### 4. Knowledge Is Communal Within Settlements, Geographic Between Them

When someone in the settlement discovers something, the settlement knows. Period. No agent-by-agent teaching spam.

- Discovery within a settlement = communal knowledge
- An explorer who discovers something away from home must RETURN to share it. If they die on the trip, the knowledge is lost. That's dramatic and interesting.
- Inter-generational transfer: agents remember locations ("grandpa told us about the shiny rock in the eastern cliffs") — not just recipes but WHERE things are
- Settlements can share knowledge through contact/trade, but distance creates friction

**Test:** If the event log is full of "Agent X taught Agent Y [recipe]" messages, the knowledge system is wrong.

### 5. The Tech Tree Is Deep, Not Wide

The inspiration gap that drives this project: "Why do you have to stop at iron, and why is iron the straight jump from stone?" Between "nothing but dirt and sticks" and "launching rockets to the moon" is an enormous, fascinating progression.

- Stone knife → stone knife lashed to stick → crude axe. That IS progression.
- Fire → clay hardening → pottery → kiln → smelting. Each step opens new possibilities.
- Copper exists before you can smelt it. Your fire isn't hot enough yet. Fast forward, you discover adding tin to copper makes bronze.
- The tech tree should be visible, complete, and watchable — not hidden fog-of-war. Discovered nodes light up. You see where you are and what's possible.

**Do not** add breadth (more recipes at the same tier) before depth (more tiers and meaningful steps between eras).

### 6. A Stalled Civilization Is Interesting, Not Broken

A community stuck in the bronze age that expands, builds monuments, trades, and forms governance is a valid and compelling outcome. The simulation does not have a win condition. It has history.

---

## Implementation Rules

### Quality Over Feature Breadth — Always

- Do not implement NEW features before EXISTING features work well
- Do not add unrequested features. If the GDD doesn't specify it, don't build it. Flag it as a question.
- "Halfassed and shallow" is worse than "fewer things done well"
- Example: Don't simulate fishing mechanics if gathering/storing/using basic resources isn't solid yet

### The GDD Is the Spec

- The latest versioned GDD PDF is the source of truth for what to build
- If the GDD is ambiguous, **ask** — do not invent an answer
- If you think the GDD is wrong, raise it as a question with specific reasoning
- Never change game design decisions in code. Design changes go through the GDD process.

### Specs Are Design Decisions

- Files in `specs/` contain approved design decisions from the project creator
- Specs override the GDD when they conflict (specs are newer and more specific)
- If a spec is ambiguous, write the question to progress.txt and skip the story
- Do NOT modify spec files

### Performance Is a Hard Constraint

- If a feature can't run at acceptable framerate (30+ FPS with current population), it doesn't ship
- The creator has observed 1 FPS at 200 population with 4% CPU utilization. This means architectural problems, not hardware limits.
- Required architectural patterns:
  - **Event bus** for all system communication (not console.writeline, not polling)
  - **Tiered perception** (immediate awareness every tick, full scan periodic based on agent state)
  - **Spatial indexing** with incremental updates (not full rebuilds)
  - **Event-driven resource regeneration** (tiles track their own timing, not polled)
- Frame performance profiling before optimization — measure, don't guess

### UI and Observation Quality

- Agent names come from curated lists of real human names (male and female, 100+ each). NOT procedural random letter strings.
- The tech tree should be a visual graph (like Rimworld's research tree or KSP's tech tree) — browsable, scrollable, with discovered nodes lit up
- Milestone discoveries get prominent announcements (large pop-up, fade-out). Minor discoveries get a toast at most.
- Teaching/knowledge-spread messages should be minimal to nonexistent in the UI. The system works communally; the player doesn't need to see the bookkeeping.
- The simulation should pass the "10-second test": a new viewer should be able to glance at the screen and understand how many agents exist, whether they're doing well, and what they're doing.

### Art and Visual Standards

- If pixel art or sprites are used, they must be implemented correctly. Water should look like water. Agents should have heads. If the art can't be integrated properly, use clean colored shapes or icons instead.
- Readability > prettiness. A clean, readable UI beats a fancy but confusing one.

---

## Time Scale

| Unit | Ticks | Real Meaning |
|------|-------|-------------|
| 1 tick | 1 | ~2 sim-minutes |
| 1 sim-day | 480 | Day/night cycle |
| 1 season | 13,440 | 28 sim-days |
| 1 sim-year | 53,760 | 4 seasons |

---

## What NOT to Build (Yet)

These are explicitly deferred. Interesting but not now:
- Playable game mechanics (player-controlled agents)
- Multiplayer or networked simulation
- 3D graphics or Unity integration
- Detailed micro-simulation (individual insects, fluid dynamics, weather)
- Real-world historical accuracy or specific cultural modeling
- Monetization or distribution

---

## Communication Protocol

### When You Hit Ambiguity

If the GDD or specs don't specify something you need to implement:
1. State what's ambiguous
2. State what you think the answer should be and why
3. Write the question to progress.txt and **skip the story** — do not invent an answer

### When You Think Something Is Wrong

If you believe a spec or GDD conflicts with the vision in this file:
1. Implement what the spec/GDD says
2. Flag the conflict in progress.txt: "I implemented X per the spec, but I think it conflicts with the vision because Y. Should we revisit?"

---

## Key Technical Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Pathfinding | A* with terrain cost weights | Mountains cost 5x, plains 1x. Cost map evolves with technology. |
| Agent perception | Tiered model | Immediate (1-2 tiles) every tick. Full scan periodic based on state. Memory fills gaps. |
| Event system | Event bus architecture | Decouples all systems. No console.writeline for game events. |
| Resource regen | Event-driven per-tile | Tiles track own timing. No global polling. |
| Spatial queries | Spatial index with incremental updates | No full rebuilds each tick. |
| Knowledge model | Communal per-settlement | No agent-to-agent teaching mechanic visible to player. |
| Agent memory | Transient (resource locations decay) + Permanent (home, landmarks, stories) | Grandpa's knowledge of the copper vein persists across generations as settlement lore. |

---

## The Anti-Patterns (Things That Have Gone Wrong Before)

These are real problems from the project's history. Do not repeat them.

1. **"Baby Making Simulator"** — Agents reproducing exponentially before basic survival is stable. Population is a CONSEQUENCE of good decisions, not a goal.
2. **Time scale confusion** — Agents dying of "old age" at 2 years. Age and time displays must be human-readable and make intuitive sense.
3. **Resource spam** — Every tile stuffed with every resource, respawning instantly. Resources should be scarce enough to drive exploration.
4. **Random walking** — Agents wandering aimlessly with no home behavior. Agents are settlers, not nomads.
5. **Teaching spam** — Log flooded with "Agent X taught Agent Y" messages. Knowledge is communal within settlements.
6. **Farms everywhere** — Farms built in random locations far from home. Agriculture should be near the settlement.
7. **Feature creep before quality** — Adding new mechanics when existing ones don't work well. Fix gathering before adding fishing.
8. **Performance as afterthought** — Building features first, optimizing later. Architecture must support scale from the start.
9. **Design decisions in code** — Coder choosing game balance values or inventing mechanics not in the GDD. Design goes through the GDD.
10. **Babies starving next to farms** — Broken food-sharing or child-feeding mechanics. If food exists and a child is starving nearby, something is architecturally wrong, not just a tuning issue.
11. **RNG cascade blindness** — Adding new scored actions or world gen changes without understanding that any change to Random.Next() call order cascades all downstream behavior. Use separate RNG instances for optional systems. Don't emit zero-score entries in the scorer.
12. **Multi-path code duplication** — Applying a guard to one code path when the same behavior exists across 11+ paths. Use Effect-First Search: grep for the EFFECT you're preventing, not the code you just changed.

---

## Version History

| Date | Change |
|------|--------|
| Feb 2026 | Initial CLAUDE.md created from comprehensive vision alignment sessions with project creator |
| Mar 2026 | Added Ralph agent workflow, time scale reference, specs protocol, anti-patterns #11-12 from D21-D25 retrospectives |

---

*This document was created through extensive dialogue between the project creator and the senior team member, capturing vision, priorities, and lessons learned across the full development history of CivSim. It supersedes any assumptions or interpretations made by team members that conflict with what's written here.*
