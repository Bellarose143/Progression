# CivSim — Animal System Design Document

**Version:** 1.0
**Date:** March 5, 2026
**Status:** Design complete, awaiting review
**Scope:** Full animal lifecycle — hunting, combat, domestication across 3 implementation phases

---

## Overview

Animals transform from a resource integer on tiles (`Animals: 5`) into living entities with species, behavior, movement, and interactions. This is the single largest feature addition since the tech tree, touching world gen, rendering, agent decisions, a new combat system, inventory/resources, and eventually domestication infrastructure.

The design covers three implementation phases:
- **Phase 1:** Animal entities, movement, herding, and hunting of safe prey (Rabbit, Deer, Cow/Sheep)
- **Phase 2:** Dangerous animals (Boar, Wolf), combat system, full hunting yields (Meat, Hide, Bone)
- **Phase 3:** Domestication, pens, breeding, Wolf → Dog

All three phases are designed here. Implementation is phased so each layer can be playtested before the next is built on top.

---

## Part 1: The Animal Entity

### What Changes

**Before:** `tile.Resources[ResourceType.Animals] = 5` — an integer. No species, no position, no behavior. "Gathering" an animal removes 1 from the count.

**After:** Each animal is an object tracked by the simulation. Animals have species, position, health, behavior state, territory, and herd membership. They tick each simulation tick with simple state-machine logic. They are rendered as species-specific sprites that move across the map.

### Animal Data Model

```
Animal
├── Id: int                      // Unique identifier
├── Species: AnimalSpecies       // Rabbit, Deer, CowSheep, Boar, Wolf, Fish
├── X, Y: int                    // Current tile position
├── Health: int                  // 0 = dead → becomes carcass
├── MaxHealth: int               // Species-dependent
├── State: AnimalState           // Idle, Grazing, Moving, Fleeing, Aggressive, Sleeping, Dead
├── TerritoryCenter: (int,int)   // Spawn point — animal roams within radius of this
├── TerritoryRadius: int         // How far from center the animal will roam
├── HerdId: int?                 // Null for solitary animals. Shared ID for herd members.
├── FleeTarget: (int,int)?       // Where the animal is fleeing to (null if not fleeing)
├── IsAlive: bool                // False = carcass, harvestable
├── CarcassDecayTimer: int       // Ticks remaining before carcass rots (only when dead)
└── TicksSinceLastMove: int      // Movement pacing control
```

### Animal States

| State | Description | Transitions to |
|---|---|---|
| Idle | Standing still. Default state. | Grazing, Moving, Fleeing, Aggressive |
| Grazing | Eating at current tile. Visual: head down. | Idle, Moving, Fleeing |
| Moving | Walking to a new tile within territory. | Idle, Grazing, Fleeing |
| Fleeing | Running away from a perceived threat (agent). | Idle (when safe) |
| Aggressive | Facing a threat, preparing to charge or attack. | Phase 2 only. |
| Sleeping | Resting at night. Reduced perception. | Idle (at dawn) |
| Dead | Carcass on tile. Harvestable. Decaying. | Removed (after full decay) |

### Species Definitions

| | Rabbit | Deer | Cow/Sheep | Boar | Wolf | Fish |
|---|---|---|---|---|---|---|
| **Biome** | Forest, Plains | Forest, Forest-Plains edge | Plains, Forest edge | Forest | Forest edge, Mountain foot | Water |
| **Max Health** | 5 | 20 | 25 | 35 | 25 | 10 |
| **Move Speed** | Every tick | Every 2 ticks | Every 3 ticks | Every 3 ticks | Every 2 ticks | N/A |
| **Detection Range** | 3 tiles | 5 tiles | 4 tiles | 3 tiles | 4 tiles | N/A |
| **Reaction** | Flee | Flee | Flee (slow) | Aggressive (Phase 2) | Aggressive (Phase 2) | None |
| **Territory Radius** | 3 tiles | 5 tiles | 4 tiles | 4 tiles | 6 tiles | Stays in water |
| **Herd Size** | 2-4 | 3-6 | 3-5 | 2-3 | 2-4 (pack) | 3-8 (school) |
| **Solitary?** | No | No | No | Occasional (20%) | No (always pack) | No |
| **Yields (Phase 1)** | Meat: 1 | Meat: 3 | Meat: 4 | — (not huntable P1) | — (not huntable P1) | — (not huntable) |
| **Yields (Phase 2)** | Meat: 1 | Meat: 3, Hide: 2, Bone: 1 | Meat: 4, Hide: 2, Bone: 1 | Meat: 4, Hide: 1, Bone: 2 | Hide: 2, Bone: 1 | Meat: 2 (fishing) |
| **Danger (Phase 2)** | None | None | None | 10-15 dmg charge | 10-20 dmg attack | None |
| **Domesticatable** | Yes (P3) | Yes (P3) | Yes (P3) | Yes → Pig (P3) | Yes → Dog (P3) | No |

### Movement and Herding

**Individual movement:** Each tick, an animal may move based on its speed (e.g., deer move every 2 ticks). Movement is a random walk within territory radius. The animal picks a random adjacent passable tile that is within TerritoryRadius of TerritoryCenter. If no valid tile exists, it stays put.

**Herd movement:** Animals with the same HerdId coordinate movement. When the herd "decides" to move (leader moves), all members move in the same general direction. Implementation: designate the lowest-Id animal in each herd as the "leader." When the leader moves, other herd members attempt to move toward the leader's new position (or in the same direction if adjacent). Herd members that fall more than 2 tiles behind the leader get a movement boost (move every tick instead of every N ticks) until they catch up.

**Territory:** Animals don't wander infinitely. TerritoryCenter is set at spawn. TerritoryRadius limits roaming. If an animal reaches the edge of its territory, it turns back. Territory keeps herds in recognizable areas — "the deer by the eastern forest" stays in the eastern forest.

**Night behavior:** Animals sleep at night (same night hours as agents). Sleeping animals don't move and have reduced detection range (halved). This means night hunting (Phase 2+) could be advantageous — approach sleeping prey more closely before they detect you.

**Fleeing behavior:**
1. Agent enters detection range.
2. Animal State → Fleeing.
3. FleeTarget = tile directly away from agent, within territory.
4. Animal moves toward FleeTarget at full speed (every tick regardless of normal speed).
5. If animal reaches territory edge while fleeing, it picks a perpendicular direction.
6. Fleeing ends when agent is outside detection range + 2 tiles (safety buffer).
7. Animal returns to Idle/Grazing after fleeing ends.

**Herd fleeing:** When one herd member starts fleeing, ALL members of the herd flee in the same direction. The "one deer spots you, the whole herd bolts" effect. This is visually compelling and creates realistic hunting challenge.

### Slow Replacement Spawning

Wild animal populations replenish slowly to represent migration and natural breeding. This is NOT an explicit breeding mechanic — animals just appear periodically.

**Rules:**
- Every ANIMAL_REPLENISH_INTERVAL ticks (CALIBRATE LATER, start at 100 = ~2.5 years), the system checks each territory.
- If a herd has fewer members than its spawn size (e.g., deer herd spawned with 5, now has 2), there is a 30% chance of 1 new animal appearing near the territory center.
- New animals share the existing HerdId.
- If an entire herd is wiped out, the territory is empty. After HERD_RESPAWN_DELAY ticks (CALIBRATE LATER, start at 200 = ~5 years), a new herd may spawn in the area if conditions are met (biome still suitable, no agents within 5 tiles).
- Overhunting has real consequences — kill all the deer near your settlement and you'll wait years for new ones. This drives exploration for new hunting grounds and eventually motivates domestication.

**Caps:**
- Maximum animals per biome region stays roughly at current levels. Replenishment replaces losses, it doesn't cause population explosion.
- Fish replenishment is faster (every 50 ticks) since fish are a renewable staple.

---

## Part 2: Hunting

### The Hunt Action

Hunting replaces `Gather` for animals. The old `ResourceType.Animals` integer system is removed entirely. You don't "gather" an animal — you hunt it.

**New action type:** `ActionType.Hunt`

**Hunt is NOT a discovery.** Hunting is instinctive — a hungry human near a rabbit will try to catch it. However, what you're *willing* to attempt depends on what you're holding:

| Tool Tier | What Agent Will Attempt | Why |
|---|---|---|
| Bare-handed | Rabbit only (low success) | You can't catch much with your hands, and you know it |
| Stone knife | Rabbit (good success), Deer, Cow/Sheep | A knife gives you confidence with passive animals |
| Crude axe | Same as knife | Chopping tool works in a fight |
| Hafted tools | Same as knife, slightly better success rates | Better tools, same prey |
| Spear (NEW recipe) | All including Boar | Range lets you engage dangerous prey |
| Bow (NEW recipe) | All, with range advantage | Can soften targets before they close |

**Agents assess danger intuitively.** The scoring function checks: "do I have a tool that can handle this species?" If not, the animal isn't even considered as a hunt target. An unarmed agent next to a wolf feels fear, not hunger. This isn't a game rule — it's common sense behavior.

### Hunt Sequence

**Step 1 — Decide to Hunt.** Utility scorer evaluates Hunt action. Factors:
- Agent hunger (hungrier = higher hunt score)
- Nearest huntable animal (species agent is willing to attempt given tools)
- Distance to animal
- Agent health (low health = won't risk a fight)
- Tool quality (better tools = higher success confidence = higher score)

Hunt competes with Gather (berries/grain), Forage, Farm, and other food-acquisition actions. An agent near a berry patch with a full bush won't hunt a deer 8 tiles away. But an agent with depleted berries nearby and a deer in sight will hunt.

**Step 2 — Approach.** Agent moves toward the animal's tile. This is a standard Move action with the animal as the target. The animal exists on a specific tile and may move while the agent approaches — the agent updates its target each tick to track the animal's current position.

**Step 3 — Animal Reacts.** When the agent enters the animal's detection range:
- **Fleeing prey (Rabbit, Deer, Cow/Sheep):** Animal and its herd flee. The hunt becomes a pursuit.
- **Aggressive prey (Boar, Wolf — Phase 2):** Animal stands ground or charges. Combat begins.

**Step 4 — Pursuit (fleeing prey).** Agent chases the animal. Each tick of pursuit:
- Calculate if agent can close distance (agent speed vs animal flee speed).
- Agent speed is 1 tile/tick (modified by terrain). Animal flee speed is 1 tile/tick for all species (they all flee at max speed).
- Success roll each tick of pursuit: base chance modified by tool tier, terrain, agent health.
  - Rabbit: 10% base bare-handed, 35% with knife, 50% with hafted tools
  - Deer: 0% bare-handed (won't attempt), 20% with knife, 35% with hafted tools
  - Cow/Sheep: 0% bare-handed, 30% with knife, 50% with hafted tools (slow, docile)
- If success: animal is caught. Proceed to kill.
- If fail for MAX_PURSUIT_TICKS (species-dependent): animal escapes. Hunt fails. Animal relocates within territory.
  - Rabbit: max 2 ticks pursuit (quick grab or it's gone)
  - Deer: max 5 ticks pursuit (longer chase)
  - Cow/Sheep: max 3 ticks pursuit (slow but you still have to catch it)

**Step 5 — Kill.** Agent deals lethal damage. Animal health → 0. State → Dead. Animal becomes a carcass on its current tile.

**Step 6 — Harvest.** Agent harvests the carcass. Multi-tick action (HARVEST_DURATION, CALIBRATE LATER, start at 3 ticks). Produces species-specific yields (Meat in Phase 1; Meat + Hide + Bone in Phase 2). Yields go to agent inventory via TryAddToInventory.

**Carcass decay:** If no agent harvests within CARCASS_DECAY_TICKS (CALIBRATE LATER, start at 20 ticks), the carcass partially rots. Meat yield reduced by 50%. After 2x CARCASS_DECAY_TICKS, carcass disappears entirely. This creates urgency — don't kill what you can't harvest. Also enables cooperation: one agent hunts, another nearby can harvest.

### New Resource Types

| Resource | Type | Used For |
|---|---|---|
| Meat | Food | Eating (raw or cooked). Raw meat restores 30 hunger. Cooked meat restores 50. Spoils after MEAT_SPOIL_TICKS if not preserved. |
| Hide | Material | Clothing upgrades, leather working, shelter improvements, containers. Non-food. |
| Bone | Material | Bone tools (existing recipe, inputs change from "2 Animals" to "2 Bone"). Needles, jewelry (future). Non-food. |

**Meat replaces the old "Animals" food resource.** Any existing system that uses Animals as food now uses Meat. Agents eat Meat, cook Meat, preserve Meat. The old ResourceType.Animals is retired.

**Hide and Bone are non-food resources** subject to the existing non-food inventory cap (SimConfig.NonFoodInventoryHardCap). They use TryAddToInventory and respect all existing guards.

### Tech Tree Modifications

**Changed recipes:**
- `bone_tools`: Input changes from "2 Animals" to "2 Bone". Prerequisite stays as stone_knife. Now requires actually hunting animals that yield bone (Deer, Cow/Sheep, Boar — not Rabbit).

**New recipes needed for hunting progression:**

| Recipe ID | Era | Branch | Inputs | Prerequisites | Base % | Effect |
|---|---|---|---|---|---|---|
| `spear` | 3 | Tools | 2 Wood + 1 Stone | hafted_tools | 10% | Ranged melee weapon. Enables hunting Boar. Hunt success +15% for all prey. Deals 15 damage in combat. Agent can engage from 2 tiles (first strike before charge). |
| `bow` | 4 | Tools | 3 Wood + 1 Hide | spear, weaving | 6% | Ranged weapon. Enables safe Wolf hunting. Hunt success +25% for all prey. Deals 10 damage at 3 tile range. Agent can soften prey before melee. |
| `trapping` | 3 | Food | 2 Wood + 1 Hide | stone_knife, foraging_knowledge | 8% | Passive hunting. Agent places traps on tiles. Traps have a chance of catching Rabbit/small prey each tick without agent presence. Yields Meat: 1 per catch. Frees agent to do other things. |

**Note on trapping:** This is a "set and forget" hunting method that produces small amounts of food passively. It bridges the gap between "I have to chase every rabbit" and farming. An agent with trapping knowledge can set traps near home and get a trickle of meat while doing other tasks. Thematically it's the first step toward working *with* the environment rather than just chasing things in it.

---

## Part 3: Combat (Phase 2)

Combat is simple. It is NOT a complex RPG system. It's a health exchange that plays out over a few ticks with outcomes determined by tools, species, and circumstance.

### When Combat Happens

- Agent approaches a Boar within charge range (2 tiles). Boar charges.
- Agent approaches a Wolf within aggression range (3 tiles). Wolf attacks. Other pack members converge.
- Agent attacks any dangerous animal with appropriate tools (spear or bow).

### Combat Resolution

Combat plays out tick-by-tick. Duration scales with animal size:

| Species | Combat Duration | Agent Damage Dealt (per tick) | Animal Damage Dealt (per tick) |
|---|---|---|---|
| Boar | 4-6 ticks | Tool-dependent (see below) | 10-15 |
| Wolf (single) | 3-5 ticks | Tool-dependent | 10-15 |
| Wolf (pack of 3) | 5-8 ticks | Tool-dependent (split across targets) | 25-40 (all wolves attack) |

### Agent Damage Output by Tool

| Tool | Melee Damage/tick | Range | Notes |
|---|---|---|---|
| Bare-handed | 2 | Adjacent only | Basically useless vs dangerous animals |
| Stone knife | 5 | Adjacent | Enough to kill a boar but you'll take serious damage |
| Crude axe | 7 | Adjacent | Better, but still risky |
| Hafted tools | 8 | Adjacent | Solid |
| Spear | 15 | 2 tiles | First strike before charge. Major advantage. |
| Bow | 10/tick at range, 5 melee | 3 tiles | Soften target at range, finish in melee. Best overall. |

### Combat Flow

1. **Initiation.** Agent enters animal's aggression range, OR agent actively engages.
2. **Ranged phase (if spear/bow).** Agent deals damage at range for 1-2 ticks before animal closes. Bow gets more ranged ticks (3 tile range vs 2 for spear).
3. **Melee phase.** Animal and agent are on same/adjacent tiles. Each tick: agent deals melee damage, animal deals species damage.
4. **Disengage check.** Each tick, if agent health < 30% of max, survival priority fires. Agent attempts to flee. Success depends on animal speed vs agent speed. Fleeing from a boar is easier (slow) than fleeing from wolves (fast).
5. **Resolution.** Either animal dies (→ carcass) or agent flees (wounded, hunt failed).

### Agent Death from Animals

This is real. If an agent fights a wolf pack with just a stone knife, they can die. Health reaches 0 → agent death, same as starvation. The event log and notification system treats this as a significant event: "[Agent Name] was killed by wolves near [location]."

This creates meaningful exploration risk. Going far from home alone into wolf territory is dangerous. Going with a partner, or with a spear, changes the calculus. Technology makes the world safer — that's a core theme of CivSim.

### Pack Behavior

When one wolf in a pack enters combat, ALL wolves in the same HerdId within 5 tiles converge on the agent. They arrive over 2-3 ticks (not instant). This means:
- You can sometimes kill one wolf before the pack arrives if you have a bow.
- Fighting a full pack without ranged weapons is near-suicidal.
- Agents should intuitively avoid wolf packs without sufficient tools (scoring evaluates pack size).

---

## Part 4: Domestication (Phase 3)

### Discovery Gating

`animal_domestication` already exists in the tech tree at Era 3. Currently it does nothing. In Phase 3, it enables the Tame action for species-specific domestication.

Additionally, each species may require species-specific knowledge:
- Rabbit/Cow/Sheep: `animal_domestication` is sufficient.
- Boar → Pig: `animal_domestication` + `trapping` (you need to contain them first).
- Wolf → Dog: `animal_domestication` + specific conditions (see below).

### The Tame Action

**New action type:** `ActionType.Tame`

**Process:** Agent approaches animal with food in inventory. Instead of hunting, agent offers food (drops 1 food on ground near animal). Animal has a TameProgress counter (starts at 0). Each successful offering increments TameProgress. When TameProgress reaches species-specific threshold, animal is tamed.

| Species | Tame Threshold | Offerings Needed | Time (est.) | Difficulty |
|---|---|---|---|---|
| Rabbit | 3 | 3 food | ~10-15 ticks | Easy — rabbits are curious |
| Cow/Sheep | 5 | 5 food | ~25-30 ticks | Easy-Medium — docile |
| Deer | 8 | 8 food | ~40-50 ticks | Medium — skittish, flees between offerings |
| Boar → Pig | 10 | 10 food + trapped | Hard — must be in a trap/pen first |
| Wolf → Dog | 15 | 15 food, must be pup | Very Hard — see special rules below |

**Taming is interruptible.** If the agent leaves or the animal flees before the next offering, TameProgress decays by 1 per 10 ticks. Extended absence resets progress. This means taming requires sustained attention — you can't offer one berry and come back a week later.

**Tamed animals change:** Species changes visually (tame sprite variant). State → Domesticated. Animal follows the agent or stays at a designated pen. TerritoryCenter shifts to the settlement. The animal no longer flees from agents.

### Wolf → Dog: The Prestige Domestication

This is special. You don't tame an adult wolf — they're too aggressive. You find a pup.

**Requirements:**
- `animal_domestication` knowledge
- Wolf pack in the area must have existed for 200+ ticks (established pack)
- System spawns wolf pups in established packs at a low rate (1 pup per 400 ticks, max 2 per pack)
- Pups are visually smaller, don't attack, but are protected by the pack
- To reach a pup, agent must either: (a) kill/drive off the adults (risky), (b) find a pup that wandered slightly from the pack (rare event), or (c) have a bow to keep adults at distance while approaching pup
- Taming the pup requires 15 food offerings over ~60-80 ticks
- If the adult wolves detect the agent near the pup, they attack

**What a Dog does:**
- Follows the owner agent
- Perception bonus: +3 tile detection range (dog senses things the human doesn't)
- Hunt bonus: +10% success rate on all prey (dog helps chase/corner)
- Guard: at night, dog "alerts" if wolves or boar approach within 5 tiles (event notification)
- Companionship: agent with a dog has slightly lower restlessness decay (they're less lonely)

**This should feel like a milestone.** The notification "Lily tamed a wolf — first dog in Riverhaven" should be a major event. Visually, the dog walks with Lily everywhere she goes.

### Infrastructure: Pens and Pastures

Tamed animals need space. This connects to the Build system.

**Pen** (new structure):
- Recipe: 5 Wood + 2 Stone. Requires `animal_domestication`.
- Placed on a tile adjacent to home/settlement.
- Holds up to 5 small animals (Rabbit, Pig) or 3 large animals (Cow/Sheep, Deer).
- Animals in pens don't wander. They eat from stored grain (1 grain per 20 ticks per animal).
- Pens must be maintained — if food runs out, penned animals take health damage and eventually die.

**Pasture** (future structure, design only):
- Larger fenced area spanning 2-4 tiles.
- For Deer and Cow/Sheep herds.
- Animals graze naturally (consume tile's grass/grain resource).
- Requires fencing (Wood-intensive).
- Not needed for Phase 3 initial implementation — Pen is sufficient.

### Domesticated Animal Breeding

Tamed animals in pens can breed. This is the payoff of domestication — renewable, controlled food/resource production.

**Rules:**
- 2+ animals of the same species in a pen → breeding chance each BREED_INTERVAL ticks (CALIBRATE LATER, start at 80 = ~2 years).
- Breeding produces 1 offspring. Offspring is immediately tamed (born in captivity).
- Pen capacity limits breeding — full pen stops new births.
- This is how rabbits become a sustainable food source: tame 2, pen them, breed them, harvest extras.

---

## Part 5: Sprite System

### Spritesheet-Based Rendering

Animal sprites are provided as spritesheets — a single PNG containing multiple frames organized in a grid. Each row represents a facing direction, each column is an animation frame. The renderer slices the sheet at runtime using basic grid math. No per-frame image files, no hardcoded sprite references.

This same system will eventually support agent spritesheets with walking animations, but animals are the first implementation.

### Standard Spritesheet Layout (Convention)

The default convention matches the asset style already in use (e.g., deer_roe_buck_32x32.png, wolf_gray_32x32.png):

```
Row 0:  Up / North      — frame 0, frame 1, frame 2
Row 1:  Right / East     — frame 0, frame 1, frame 2
Row 2:  Down / South     — frame 0, frame 1, frame 2
Row 3:  Left / West      — frame 0, frame 1, frame 2
```

Each frame is a fixed size (e.g., 32x32 pixels). The system calculates crop rectangles at load time:
- Frame "deer facing east, frame 2" → `x = 2 * 32, y = 1 * 32, size = 32x32`

Frame cycling for walk animation: the renderer advances frames based on a configurable frame rate (ticks per frame). A deer with `frame_rate: 4` cycles through its 3 walk frames every 12 ticks while moving. When idle, it holds frame 0 of the current facing direction.

### File Structure

```
Assets/
  Sprites/
    Animals/
      rabbit_32x32.png
      deer_roe_buck_32x32.png
      cow_sheep_32x32.png
      boar_32x32.png
      wolf_gray_32x32.png
      wolf_pup_24x24.png
      fish_16x16.png
      dog_32x32.png
      pig_tamed_32x32.png
      deer_tamed_32x32.png
    animals.json          ← sprite registry
```

### Sprite Registry (animals.json)

**Minimal config (standard layout — convention does the work):**

A spritesheet that follows the standard 4-row directional layout needs only one line of real config. The system infers rows, columns, and direction mapping from the image dimensions and frame size:

```json
{
  "deer": {
    "spritesheet": "deer_roe_buck_32x32.png",
    "frame_size": 32
  }
}
```

The system reads the PNG dimensions (e.g., 96x128), divides by frame_size (32), and infers: 3 columns (frames per direction), 4 rows (directions). Applies the standard row mapping: up, right, down, left.

**Full config (explicit overrides for non-standard sheets):**

If a spritesheet doesn't follow the convention — has extra rows for attack/death animations, different direction count, irregular frame count — the JSON can spell out everything explicitly:

```json
{
  "deer": {
    "spritesheet": "deer_roe_buck_32x32.png",
    "frame_width": 32,
    "frame_height": 32,
    "frame_rate": 4,
    "animations": {
      "walk_up":    { "row": 0, "frames": [0, 1, 2] },
      "walk_right": { "row": 1, "frames": [0, 1, 2] },
      "walk_down":  { "row": 2, "frames": [0, 1, 2] },
      "walk_left":  { "row": 3, "frames": [0, 1, 2] },
      "idle_down":  { "row": 2, "frames": [1] },
      "sleep":      { "row": 2, "frames": [1] },
      "graze":      { "row": 2, "frames": [0, 1] }
    }
  }
}
```

When explicit `animations` are present, they override the convention entirely. The renderer asks for an animation name + frame number, the registry returns the exact crop rectangle. This supports future expansion (attack animations, death animations, seasonal variants) without changing the renderer.

**Tamed and variant sprites:** Different spritesheets for tamed versions. The registry maps variants:

```json
{
  "deer": {
    "spritesheet": "deer_roe_buck_32x32.png",
    "frame_size": 32,
    "variants": {
      "tamed": "deer_tamed_32x32.png"
    }
  },
  "boar": {
    "spritesheet": "boar_32x32.png",
    "frame_size": 32,
    "variants": {
      "tamed": "pig_tamed_32x32.png"
    }
  },
  "wolf": {
    "spritesheet": "wolf_gray_32x32.png",
    "frame_size": 32,
    "variants": {
      "pup": "wolf_pup_24x24.png",
      "tamed": "dog_32x32.png"
    }
  }
}
```

The renderer selects the variant spritesheet based on animal state (wild, tamed, pup), then uses the same row/column slicing logic. Variant sheets follow the same standard layout convention unless they have their own explicit `animations` block.

### Renderer Logic (Per Frame)

```
1. What species is this animal?           → "deer"
2. What variant? (wild / tamed / pup)     → "wild" → use base spritesheet
3. What animation? Derived from state:
   - Moving → "walk_{direction}"
   - Idle → "idle_{facing}"
   - Sleeping → "sleep"
   - Grazing → "graze" (if defined, else "idle_{facing}")
   - Fleeing → "walk_{flee_direction}" (faster frame rate)
   - Dead → gray tint on last frame, or dedicated "death" animation
4. What frame index?                      → tick % (frame_count * frame_rate) / frame_rate
5. Look up crop rectangle from registry   → row, column → pixel coordinates
6. Draw that rectangle at the animal's interpolated screen position
```

### How to Add a New Animal

1. Create a spritesheet PNG following the standard 4-row layout (up/right/down/left, 3 frames each).
2. Drop it in `Assets/Sprites/Animals/`.
3. Add an entry to `animals.json` with at minimum `"spritesheet"` and `"frame_size"`.
4. Add an `AnimalSpecies` enum value in code.
5. Define species stats (health, speed, territory, herd size, yields, behavior).
6. The renderer picks it up automatically — no rendering code changes.

For tamed variants or special animations, add a variant spritesheet and/or explicit animation blocks in the JSON. Still no rendering code changes.

### Future: Agent Spritesheets

The same system extends to agents. Agent spritesheets would follow the same convention (4 directional rows, walk cycle frames) with the addition of activity-specific animations (gathering, building, resting). The renderer already handles agents and animals separately, but the spritesheet slicing and animation logic would be shared infrastructure. This is not in scope for the animal system but the architecture is designed to support it.

---

## Part 5.5: Smooth Multi-Tick Movement Interpolation

### The Problem

The current interpolation system creates a step-pause-step pattern. Agent (or animal) position only updates in the simulation when a move completes. On a Plains tile (cost 1.0), that's every tick — tolerable. On a Forest tile (cost 1.5) or Mountain (cost 2.5), the entity sits visually still for multiple ticks, then snaps to the next tile and lerps over a single tick. It looks mechanical and jerky.

Animals make this worse because their move speeds vary by species. A cow that moves 1 tile per 3 ticks would show: freeze for 2 ticks, slide for 1 tick, freeze, slide. That's not a cow walking. That's a cow teleporting in slow motion.

### The Solution: Full-Duration Interpolation

When a move action begins, the renderer should lerp from the start tile to the destination tile over the **full duration of the move**, not just the final tick.

**Data the renderer needs (exposed from simulation):**

```
Entity (Animal or Agent):
├── MoveStartTick: int      // Tick when this move began
├── MoveEndTick: int        // Tick when position will update (start + duration)
├── MoveOrigin: (int,int)   // Tile position at start of move
├── MoveDestination: (int,int)  // Tile position at end of move
├── IsMoving: bool          // True between start and end
```

**Renderer interpolation:**

```
if (entity.IsMoving):
    totalDuration = entity.MoveEndTick - entity.MoveStartTick
    elapsed = currentTick - entity.MoveStartTick + subTickFraction
    t = clamp(elapsed / totalDuration, 0, 1)
    smooth_t = t * t * (3 - 2 * t)     // smoothstep
    visualPosition = lerp(entity.MoveOrigin, entity.MoveDestination, smooth_t)
else:
    visualPosition = entity.Position    // stationary, snap to tile
```

**Result by species:**
- Rabbit (1 tile/tick): fast zip between tiles, just like current but with smoothstep easing.
- Deer (1 tile/2 ticks): smooth glide over 2 ticks. Visibly slower than rabbit.
- Cow (1 tile/3 ticks): slow amble over 3 ticks. Clearly the slowest animal on screen.
- Agent on Plains (1 tick): same as rabbit speed.
- Agent on Forest (1.5 ticks): slightly slower, visible difference from plains walking.
- Agent on Mountain (2.5 ticks): noticeably slow trudge.

**Speed differences become visible naturally.** You don't need labels or UI to tell that a rabbit is fast and a cow is slow — you can *see* it in how they move across the world.

### Walk Animation Sync

The spritesheet walk cycle should sync to movement duration, not a fixed frame rate. A cow taking 3 ticks to cross a tile should complete one full walk cycle (frames 0→1→2→0) over those 3 ticks. A rabbit taking 1 tick should cycle faster. This means `frame_rate` in the registry is a fallback for idle animations; during movement, the frame cycle is tied to move progress:

```
frame_index = floor(move_progress_t * frames_per_direction) % frames_per_direction
```

A cow at 50% of its 3-tick move shows frame 1 (middle of walk cycle). At 100%, frame 2 (step completing). The animation is mechanically tied to the movement — no sliding feet, no moonwalking.

### Applies to Both Animals and Agents

This interpolation system is designed generically. Animals use it from Phase 1. Agents can adopt it by exposing the same MoveStartTick/MoveEndTick/MoveOrigin/MoveDestination fields. The renderer code is shared — it doesn't care whether it's interpolating a deer or Joshua.

**For agents, this fixes the longstanding step-pause-step complaint.** An agent walking through Forest currently shows: freeze (0.5 ticks), snap, freeze, snap. With full-duration interpolation: smooth continuous walk at forest speed. The interpolation problem is solved not by patching the renderer, but by giving the renderer the information it needs to interpolate correctly.

### Implementation Note

This is a renderer change + a small simulation-side change (exposing move timing data). It does NOT change simulation logic — positions still update at the same tick they always did. The visual representation is now continuous between updates instead of discrete. Determinism is unaffected.

---

## Part 6: World Gen Integration

### Removing ResourceType.Animals

The integer-based animal resource is fully replaced by animal entities. During world gen:

**Old system (removed):**
- Tiles get `Resources[Animals] = N` based on biome.
- D23 animal cleanup: 4% of eligible tiles get 3-5 animals.

**New system:**
1. After biome assignment, identify eligible tiles for each species.
2. For each species, select spawn locations based on biome and clustering rules.
3. Create Animal entities with species, position, health, territory, and herd assignment.
4. Animals are tracked in a `List<Animal>` on the World object (or similar collection).
5. The spatial index includes animals for perception queries.

**Spawn rules per species:**

| Species | Eligible Biomes | Spawn Density | Herd Size | Placement |
|---|---|---|---|---|
| Rabbit | Forest, Plains | 1 herd per ~30 eligible tiles | 2-4 | Clustered on adjacent tiles |
| Deer | Forest, Forest-Plains border | 1 herd per ~25 eligible tiles | 3-6 | Clustered, prefer forest edge |
| Cow/Sheep | Plains, Forest edge | 1 herd per ~30 eligible tiles | 3-5 | Clustered on plains |
| Boar | Forest (dense) | 1 group per ~40 forest tiles | 2-3 | Interior forest, away from edges |
| Wolf | Forest-Mountain border | 1 pack per ~50 eligible tiles | 2-4 | Near mountain foothills |
| Fish | Water tiles with adjacent land | 1 school per ~8 water tiles | 3-8 | In water, near shore |

**Total animal population** should be roughly similar to pre-change levels to maintain ecological balance. The exact numbers are CALIBRATE LATER — the densities above are starting points.

---

## Part 7: Tech Tree Connections Summary

### Changed Recipes
| Recipe | Old Input | New Input | Reason |
|---|---|---|---|
| bone_tools | 2 Animals | 2 Bone | Animals is no longer a resource. Bone comes from hunting. |

### New Recipes
| Recipe | Era | Branch | Inputs | Prerequisites |
|---|---|---|---|---|
| spear | 3 | Tools | 2 Wood + 1 Stone | hafted_tools |
| bow | 4 | Tools | 3 Wood + 1 Hide | spear, weaving |
| trapping | 3 | Food | 2 Wood + 1 Hide | stone_knife, foraging_knowledge |
| pen | 3 | Shelter | 5 Wood + 2 Stone | animal_domestication |

### Existing Recipe Now Has Meaning
| Recipe | Era | Current State | After Animal System |
|---|---|---|---|
| animal_domestication | 3 | Discovered but does nothing | Enables Tame action, pen construction |

---

## Part 8: Implementation Phases

### Phase 1: Entities + Movement + Hunting Safe Prey

**Scope:**
- Animal entity system (data model, World.Animals collection, spatial index integration)
- 6 species spawned during world gen (replacing ResourceType.Animals integer)
- Animal tick: state machine with Idle, Grazing, Moving, Fleeing, Sleeping states
- Herd movement with leader-follower pattern
- Territory system (animals stay within radius of spawn point)
- Fleeing behavior (animals react to agent proximity)
- Hunt action for safe prey: Rabbit, Deer, Cow/Sheep
- Tool-tier gating for hunt eligibility
- Pursuit mechanic with success rolls
- Carcass system with decay timer
- Harvest action producing Meat (new resource type)
- Meat as food (raw: 30 hunger, cooked: 50 hunger)
- Old ResourceType.Animals removed entirely
- Slow replacement spawning
- Spritesheet system: JSON registry with convention-based slicing, explicit override support, variant spritesheets for tamed/pup forms. Standard layout: 4 directional rows, 3 frames each. Drop a PNG + add a JSON entry to add new animals.
- Directional sprites: animals face their movement direction, walk cycle animates during movement, idle frame when stationary
- Smooth multi-tick movement interpolation: animals (and agents) lerp across the full move duration, not just the final tick. Cow ambles over 3 ticks, rabbit zips in 1. Walk animation syncs to move progress. Fixes the step-pause-step problem for both animals and agents.
- Animal sprites rendered with species-specific visuals and movement

**Does NOT include:** Combat damage, Hide, Bone, Boar hunting, Wolf hunting, Domestication, Trapping, Spear, Bow, Pens.

**Boar and Wolf exist in Phase 1** — they spawn, move, and react to agents (boar stands ground, wolves growl). But agents won't attempt to hunt them (scoring returns 0 without spear/bow). They're visible threats that agents avoid. The world feels dangerous even before combat is implemented.

**Validation targets:**
- Zero starvation deaths that didn't exist before (Meat replaces old animal food)
- Herds visibly moving on the map with smooth interpolation (no step-pause-step)
- Animals face their direction of travel and animate walk cycles
- Agents hunting rabbits/deer instead of "gathering animals"
- Carcasses visible on tiles after kills
- Animal population roughly stable over time (replenishment working)
- Agent movement also uses smooth multi-tick interpolation (step-pause-step fixed for agents too)
- New animal species can be added by dropping a PNG + editing animals.json (no code changes)

### Phase 2: Combat + Danger + Full Yields

**Scope:**
- Combat system (tick-by-tick health exchange)
- Boar charge behavior and combat
- Wolf aggression and pack convergence
- Agent damage from animals (Health reduction, potential death)
- Spear recipe (enables Boar hunting)
- Bow recipe (enables safe Wolf hunting)
- Hide and Bone as new resource types
- Full hunting yields (Meat + Hide + Bone per species)
- bone_tools recipe input changed to 2 Bone
- Trapping recipe and passive trap mechanic
- Agent death from combat ("killed by wolves" event)
- Hunt scoring considers pack size and danger assessment
- Disengage/flee mechanic when agent health is low

**Validation targets:**
- Agents don't engage Boar/Wolf without appropriate tools
- Combat produces visible health changes
- Agent death from animals is possible but uncommon with good tools
- Hide and Bone appear in inventories and enable new recipes
- Traps produce passive food income

### Phase 3: Domestication

**Scope:**
- Tame action (multi-tick food offering process)
- TameProgress per wild animal
- Tamed animal behavior (follows agent or stays at settlement)
- Pen structure (build recipe, tile placement)
- Penned animal management (feeding, capacity)
- Breeding mechanic for penned animals
- Wolf pup spawning in established packs
- Wolf → Dog taming (special requirements)
- Dog companion behavior (follows owner, perception bonus, hunt bonus, guard alerts)
- Species-specific tame difficulty and requirements
- Tamed variant sprites

**Validation targets:**
- Agents can tame at least one animal species within a reasonable run
- Penned animals produce sustainable food/resources
- Dog noticeably improves owner's capabilities
- Taming wolf feels like a significant achievement

---

## Design Principles (Carry Forward)

1. **Animals are alive, not resources.** They move, react, flee, fight. They are part of the world, not items on a tile.
2. **Hunting is a judgment call.** Agents assess risk based on what they're holding. No bare-handed boar wrestling.
3. **Technology opens the world.** Stone knife → hunt deer. Spear → hunt boar. Bow → hunt wolves. Each tool tier makes more of the world accessible.
4. **Domestication is the agricultural revolution for animals.** It transforms a finite, risky food source (hunting) into a renewable, safe one (farming animals). Same arc as farming for plants.
5. **Wolf → Dog is a story.** The hardest domestication with the best payoff. When it happens, it should be a moment the player remembers.
6. **The sprite system is for Bella.** Drop a PNG, edit a JSON, see it in game. No code required for new animals. Same system extends to agents later.
7. **Movement should look like movement.** Entities lerp across the full duration of their move, not just the last tick. Speed differences are visible — rabbits zip, cows amble, agents on mountains trudge. Walk animations sync to move progress. No step-pause-step, no moonwalking, no teleporting.

---

## Constants Summary (All CALIBRATE LATER)

| Constant | Starting Value | Used In |
|---|---|---|
| ANIMAL_REPLENISH_INTERVAL | 100 ticks | Slow replacement spawning |
| HERD_RESPAWN_DELAY | 200 ticks | Full herd extinction recovery |
| MAX_PURSUIT_TICKS_RABBIT | 2 | Rabbit chase duration |
| MAX_PURSUIT_TICKS_DEER | 5 | Deer chase duration |
| MAX_PURSUIT_TICKS_COWSHEEP | 3 | Cow/Sheep chase duration |
| HARVEST_DURATION | 3 ticks | Butchering time |
| CARCASS_DECAY_TICKS | 20 ticks | Time before carcass rots |
| MEAT_SPOIL_TICKS | 40 ticks | Raw meat shelf life |
| MEAT_RAW_HUNGER | 30 | Raw meat food value |
| MEAT_COOKED_HUNGER | 50 | Cooked meat food value |
| BREED_INTERVAL | 80 ticks | Domesticated breeding cycle |
| WOLF_PUP_SPAWN_INTERVAL | 400 ticks | Pup appearance in packs |
| NEW_ADULT_BOOTSTRAP_DURATION | 500 ticks | (Existing from D24) |
