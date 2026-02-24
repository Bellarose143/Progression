# CivSim — Technology Tree: Phase 2 Complete Recipe Table

**Version:** Draft 1 — Approved by leadership with corrections applied
**Date:** February 2026
**Author:** Tech Tree Design Thread
**Status:** Approved for implementation. Two calibration warnings flagged for the calibration pass.

---

## Overview

**Total recipes:** 44 (including clothing at Era 0)
**Branches:** 5 (Tools, Fire, Food, Shelter, Knowledge)
**Eras:** 0–7
**Milestones:** 10

### Recipe Distribution

| Branch | Recipes | Era Span |
|---|---|---|
| Tools and Materials | 10 | 1–7 |
| Fire and Heat | 8 | 1–6 |
| Food and Agriculture | 9 | 1–5 |
| Shelter and Construction | 9 | 1–7 |
| Knowledge and Culture | 7 | 3–7 |
| **Total (+ clothing)** | **43 + 1 = 44** | **0–7** |

### Recipes Per Era

| Era | Name | Recipes | Gate to Next Era |
|---|---|---|---|
| 0 | Innate | 1 (clothing) | — (always unlocked) |
| 1 | Survival | 5 | Know 2+ Era 1 to attempt Era 2 (40%) |
| 2 | Adaptation | 6 | Know 3+ Era 2 to attempt Era 3 (50%) |
| 3 | Settlement | 8 | Know 3+ Era 3 to attempt Era 4 (38%) |
| 4 | Community | 10 | Know 4+ Era 4 to attempt Era 5 (40%) |
| 5 | Specialization | 5 | Know 2+ Era 5 to attempt Era 6 (40%) |
| 6 | Industry | 5 | Know 2+ Era 6 to attempt Era 7 (40%) |
| 7 | Civilization | 4 | End of current tree |

**Era gate note:** Era 4 gate increased from 3 to 4. With 10 available recipes at Era 4 (up from 7 in the original GDD), a gate of 3 would only require 30% breadth — too easy to rush past the community-building era. Gate of 4 maintains ~40% breadth requirement consistent with other eras. Flagged for review.

---

## How to Read the Tables

**Inputs** = resources consumed per experiment attempt, whether success or fail. These are physical resources from the agent's inventory. Knowledge prerequisites are listed separately.

**Prerequisites** = recipes the agent must already know. Only immediate/direct prerequisites are listed. Transitive prerequisites (e.g., if A requires B and B requires C, C is not listed as a prereq of A) are implied by the chain but not listed explicitly, UNLESS the prerequisite is conceptually important to call out for tree readability.

**Base Discovery %** = chance of success per experiment attempt before modifiers. Modifiers include: familiarity (+2% per failed attempt, cap 20%), collaboration (+2% per adjacent agent, cap +10%), tool bonus (+5% with relevant tools), and monument bonus (+5% within 3 tiles).

**Mechanical Effect** = what changes for the agent/settlement when this is discovered. Every recipe must change something observable. Effects marked [FUTURE] require systems not in v1.8 — they are noted for design intent but the v1.8 effect is the one to implement.

---

## Branch 1: Tools and Materials

*Progression: shaped stone → refined stone → hafted tools → quarried stone → copper → bronze → iron → steel.*

*This branch represents humanity's relationship with physical materials. The early game splits into two starting paths (cutting vs chopping) that converge at hafted tools — the "stone lashed to stick" moment. Mid-game introduces quarrying for the construction boom. Late game is defined by the Metal Age progression, each step gated by Fire branch convergence.*

| Recipe ID | Era | Inputs | Prerequisites | Base % | Mechanical Effect | Announce |
|---|---|---|---|---|---|---|
| `stone_knife` | 1 | 1 Stone | None | 12% | First cutting tool. Food gathering 1.25×. Enables hide and bone processing. Foundation for most tool advancement. | STANDARD |
| `crude_axe` | 1 | 2 Wood + 1 Stone | None | 10% | First chopping tool. Wood gathering 1.5×. Enables efficient wood harvesting for construction. | STANDARD |
| `refined_tools` | 2 | 2 Stone + 1 Wood | stone_knife | 10% | Better stone shaping techniques. All gathering 1.75×. Enables quarrying stone from deposits. | STANDARD |
| `bone_tools` | 2 | 2 Animals | stone_knife | 12% | Fishing efficiency 2×. Needle crafting: clothing upgrade reduces exposure damage further (0.15 → 0.10 health/tick). | STANDARD |
| `hafted_tools` | 3 | 2 Wood + 2 Stone | crude_axe, refined_tools | 8% | Stone heads lashed to wooden handles. All gathering 2.0×. Wood gathering 2.5×. Enables heavy construction tasks (walls, land clearing). The "stone knife lashed to stick → crude axe" breakthrough. | STANDARD |
| `quarrying_tools` | 4 | 3 Stone + 2 Wood | hafted_tools | 8% | Specialized stone-cutting tools (chisels, wedges, hammers). Stone gathering 2.5×. Enables efficient quarrying for large construction projects. | STANDARD |
| `copper_working` | 5 | 3 Ore + 1 Stone | smelting, hafted_tools | 5% | First metal tools. All gathering 2.5×. Copper implements for construction, crafting, and farming. The Stone Age ends here. | **MILESTONE** |
| `bronze_working` | 6 | 3 Ore + 1 Stone | copper_working | 3% | First alloy — tin added to copper creates a harder metal. All gathering 3.0×. Building speed 2×. Bronze Age begins. | **MILESTONE** |
| `iron_working` | 7 | 4 Ore + 2 Stone | bronze_working, advanced_metallurgy | 2% | Iron tools require higher temperatures than bronze. All gathering 4.0×. Building speed 3×. Iron Age begins. | **MILESTONE** |
| `steel_working` | 7 | 4 Ore + 3 Wood | iron_working | 2% | Controlled carbon content in iron produces steel. All gathering 4.5×. Building speed 4×. Construction resource costs −25%. Ultimate material technology. | **MILESTONE** |

**Branch notes:**
- `stone_knife` and `crude_axe` together replace the original `basic_tools`. Any recipe that previously required `basic_tools` now requires `stone_knife`, `crude_axe`, or both — mapped by logical fit (cutting tasks → stone_knife, chopping/wood tasks → crude_axe).
- `hafted_tools` is the convergence point: requires BOTH Era 1 paths (crude_axe for wood-working, refined_tools for stone-shaping). Rewards breadth within the branch.
- The Era 3→5 gap in Tools is intentional. Tools contributes `quarrying_tools` at Era 4 for the construction boom, then the branch waits for Fire (smelting) to catch up before the Metal Age begins. This cross-branch dependency is the core structural drama of the tree.
- Metal milestones: copper (first metal), bronze (first alloy), iron (higher capability), steel (ultimate material). All four are milestones per creative direction.

---

## Branch 2: Fire and Heat

*Progression: fire → cooking / clay hardening → pottery → kiln → charcoal → smelting → advanced metallurgy.*

*This branch represents mastery of heat. Early discoveries (cooking, clay work) have immediate survival benefits. The mid-game transitions into industrial capability: kiln and charcoal are infrastructure for the civilization-defining breakthrough of smelting. Late game provides the temperature control needed for iron and steel.*

| Recipe ID | Era | Inputs | Prerequisites | Base % | Mechanical Effect | Announce |
|---|---|---|---|---|---|---|
| `fire` | 1 | 3 Wood | None | 8% | First controlled fire. Cooking prerequisite. Exposure damage halved within 2 tiles of fire source. Foundation for entire Fire branch. Everything changes. | **MILESTONE** |
| `cooking` | 2 | 1 Food | fire | 20% | Cook action available. Cooked food restores 60 hunger (+50% over raw 40). First food technology. High discovery chance — almost obvious once you have fire and food. | STANDARD |
| `clay_hardening` | 2 | 2 Stone | fire | 12% | Discovery that fire makes clay/earth hard and durable. Building speed +25% for all shelter construction. Prerequisite for pottery and mud-brick building. | STANDARD |
| `pottery` | 3 | 2 Stone | clay_hardening | 10% | Shaped and fired clay vessels. Food stored in shelter with pottery knowledge decays 50% slower. Prerequisite for kiln, granary, and writing (clay tablets). | STANDARD |
| `kiln` | 3 | 3 Stone | fire, pottery | 8% | Controlled high-temperature firing chamber. Fired pottery: shelter storage capacity 2×. Fired bricks for construction. Critical prerequisite for charcoal and smelting. | STANDARD |
| `charcoal` | 4 | 5 Wood | kiln | 10% | Wood burned in controlled low-oxygen conditions produces high-temperature fuel. Burns hotter and longer than raw wood. Required for metal smelting. | STANDARD |
| `smelting` | 5 | 2 Ore + 2 Wood | kiln, charcoal | 5% | The breakthrough that certain rocks, when heated intensely, yield workable metal. Extracting metal from ore using sustained high heat in a kiln with charcoal fuel. Gateway to all metalworking. | **MILESTONE** |
| `advanced_metallurgy` | 6 | 3 Stone + 2 Ore | smelting | 4% | Advanced furnace design and temperature control techniques. Higher sustained temperatures enable working with harder metals. Required for iron-temperature smelting. | STANDARD |

**Branch notes:**
- `pottery` moved from Knowledge (Branch 5) to Fire (Branch 2). It is fundamentally a fire-and-clay technology. The Knowledge branch now focuses on social and intellectual innovations.
- `clay_hardening` is new — fills the gap in the vision's stated progression: "fire → clay hardening → pottery → kiln → smelting." It sits at Era 2 and branches into two paths: pottery (→ kiln → smelting) and improved_shelter (mud-brick construction). This creates meaningful choice after clay_hardening.
- `smelting` is new as a distinct recipe. Previously, the jump was charcoal → copper_working. Now the Fire branch explicitly contributes the smelting capability, and copper_working (Tools) contributes the shaping skill. The cross-branch convergence is made visible and dramatic.
- The Fire branch's internal chain (fire → clay_hardening → pottery → kiln → charcoal → smelting) is the longest single-branch prerequisite chain in the tree at 6 deep. This is intentional — mastery of heat is a slow, generational journey.

---

## Branch 3: Food and Agriculture

*Progression: foraging → cooking (cross-branch) → farming / preservation → domestication → granary / irrigation / rotation → plow.*

*This branch represents humanity's relationship with food — from finding it to producing it. Early game is about better foraging. Mid-game is the agricultural revolution. Late game is agricultural infrastructure and metal farming implements.*

| Recipe ID | Era | Inputs | Prerequisites | Base % | Mechanical Effect | Announce |
|---|---|---|---|---|---|---|
| `foraging_knowledge` | 1 | 1 Food | None | 15% | Knowledge of edible plants and seasonal food sources. Food gathering yield from natural sources +50%. Agents identify and prioritize higher-yield food patches. Future hook: prerequisite for herb gathering, mushroom foraging, medicinal plants. | STANDARD |
| `farming` | 2 | 2 Grain + 1 Wood | stone_knife | 10% | Tend Farm action available. Tended farm tiles produce grain at accelerated rate. Transition from food gathering to food production. The agricultural revolution begins. | **MILESTONE** |
| `food_preservation` | 3 | 2 Food | cooking | 12% | Preserve Food action: 2 raw food → 1 preserved food. Preserved food restores 80 hunger (+100% over raw), does not decay. First long-term food security technology. | STANDARD |
| `animal_domestication` | 3 | 3 Animals | farming, lean_to | 6% | Pen animals on tiles near settlement. Penned animals do not despawn. Reliable meat and leather source. Removes hunting uncertainty. | STANDARD |
| `land_clearing` | 3 | 3 Wood | fire, crude_axe | 12% | Clear forest tile → plains tile (multi-day action). Burn and chop forest to create farmable land near settlement. Enables agricultural expansion beyond natural clearings. | STANDARD |
| `granary` | 4 | 5 Wood + 3 Stone | pottery, farming | 8% | Build granary structure (multi-day project). Communal food storage: capacity 50, no decay. Any settlement resident can deposit or withdraw. Food security infrastructure. | STANDARD |
| `irrigation` | 4 | 3 Stone + 1 Wood | farming | 8% | Channel water to farm tiles. Farm tiles within 3 tiles of water produce 2× yield. Requires proximity to water source. Major agricultural upgrade. | STANDARD |
| `crop_rotation` | 4 | 2 Grain + 1 Wood | farming, refined_tools | 6% | Systematic planting patterns prevent soil exhaustion. Farm tiles never deplete. Sustainable long-term agriculture. | STANDARD |
| `plow` | 5 | 2 Wood + 1 Ore | copper_working, farming | 5% | Metal-tipped plow. Farm yield 3×. Deep-soil farming enables farming on any non-water, non-mountain tile. Cross-branch: first agricultural use of metal technology. | STANDARD |

**Branch notes:**
- `foraging_knowledge` is new at Era 1. Gives the Food branch an Era 1 presence and provides a meaningful early survival boost (+50% food yield from natural sources). Future-proofed as a prerequisite for expanded foraging options (herbs, mushrooms, medicinal plants).
- `farming` is tagged MILESTONE — this is the agricultural revolution, the transition from gathering to producing. It's one of the most transformative discoveries in the tree and the moment the observer sees the settlement fundamentally change.
- `land_clearing` moved from Fire branch to Food branch. Its primary purpose and mechanical effect is agricultural (creating farmland). Cross-branch prereqs (fire + crude_axe) preserved.
- `animal_domestication` prereq updated: requires `lean_to` (need a settlement/pen site) instead of the old `lean_to_shelter` naming. Functionally the same.
- The Food branch caps at Era 5 with `plow`. Era 6–7 food technologies (selective breeding, advanced irrigation, agricultural engineering) are deferred to future expansion. The branch has strong presence in Eras 1–5 where food is the primary concern.

---

## Branch 4: Shelter and Construction

*Progression: lean-to → reinforced shelter → improved shelter → walls / communal building / roads → stone masonry → monument → advanced architecture.*

*This branch represents humanity's relationship with the built environment. Each tier uses progressively advanced materials: wood → wood+stone → mud-brick → cut stone → metal-reinforced stone. The branch also includes community infrastructure (communal buildings, roads) that transforms the settlement from a cluster of shelters into a real place.*

| Recipe ID | Era | Inputs | Prerequisites | Base % | Mechanical Effect | Announce |
|---|---|---|---|---|---|---|
| `lean_to` | 1 | 3 Wood | None | 10% | First shelter. Build action: construct lean-to/windbreak. Eliminates exposure damage within 3 tiles. Health regen 2×. Home storage capacity 10. Enables reproduction (shelter hard gate). | STANDARD |
| `reinforced_shelter` | 2 | 3 Wood + 2 Stone | lean_to | 10% | Sturdier wood-and-stone shelter. Health regen 2.5×. Home storage capacity 15. Shelter decay rate reduced 25%. Reproduction stability score: shelter quality ~0.55 (vs lean_to ~0.4). | STANDARD |
| `improved_shelter` | 3 | 5 Wood + 5 Stone | lean_to, clay_hardening | 8% | Mud-brick dwelling using hardened clay techniques. Health regen 3×. Home storage capacity 20. Reproduction stability score: shelter quality ~0.7. A real home, not just cover from the rain. | **MILESTONE** |
| `walls` | 4 | 10 Stone | hafted_tools, improved_shelter | 6% | Stone wall structures around settlement. Settlement defense [FUTURE]. Reproduction stability score: improved_shelter + walls ~0.9. Build time: multi-day project. | STANDARD |
| `communal_building` | 4 | 10 Wood + 10 Stone | oral_tradition, improved_shelter | 4% | Meeting hall / communal structure. Knowledge propagation speed 1.5×. Community decisions [FUTURE]. Build time: multi-day project. The settlement becomes a community. | **MILESTONE** |
| `roads` | 4 | 3 Stone + 2 Wood | hafted_tools | 8% | Path construction knowledge. Build action: clear and improve a tile for travel, reducing movement cost by 50% on that tile. Agents build road tiles between settlement and key resource locations. | STANDARD |
| `stone_masonry` | 5 | 8 Stone + 2 Ore | quarrying_tools, walls | 5% | Cut stone blocks with fitted joints. Stone construction techniques. All shelter/building health regen bonus +1× (stacks). Build durability doubled [FUTURE]. Prerequisite for monument. | STANDARD |
| `monument` | 6 | 20 Stone + 5 Ore | stone_masonry, bronze_working, communal_building | 2% | Monumental stone construction with bronze decoration/tools. Build time: ~30–40 sim-days (multi-season communal project). Knowledge propagation speed 2×. Experiment +5% within 3 tiles. A civilization-defining structure. | STANDARD |
| `advanced_architecture` | 7 | 15 Stone + 5 Ore + 5 Wood | iron_working, stone_masonry | 2% | Metal-reinforced stone construction. Engineered structures using iron/steel. All building construction costs −25%. Largest and most durable structures possible. Bronze statues, iron-braced walls, engineered arches. | STANDARD |

**Branch notes:**
- `reinforced_shelter` is new at Era 2. Fills the gap between lean-to (Era 1) and improved_shelter (Era 3). Provides a meaningful intermediate upgrade (better storage, better health regen, better reproduction score) that rewards continued investment in shelter before clay technology is available.
- `improved_shelter` now requires `clay_hardening` (Fire branch) instead of `pottery`. Mud-brick construction requires knowing how to harden clay, not how to shape vessels. This means improved_shelter can be built before pottery — an agent who prioritizes shelter over storage can go fire → clay_hardening → improved_shelter while another goes fire → clay_hardening → pottery → kiln. Meaningful choice.
- `stone_masonry` is new at Era 5. Prerequisite for `monument` — you must know how to work stone before building in stone at monumental scale. Requires `quarrying_tools` (know how to extract stone) + `walls` (experience building with stone). Cross-branch: Tools → Shelter.
- `monument` now requires stone_masonry + bronze_working + communal_building (3 prereqs across 3 branches). This is the most cross-branch-dependent recipe in the tree. Building a monument requires: knowing how to work stone at scale (Shelter), having metal tools and decoration (Tools), and having organized communal labor (Knowledge). Thematically rich.
- `advanced_architecture` is new at Era 7. The Shelter branch capstone. Uses metal-reinforced construction techniques that only become possible with iron/steel. Practical effect: reduced construction costs across the board.
- `roads` uses per-tile movement cost modification. Reduces movement cost by 50% on built tiles. Agents will prioritize building road tiles between settlement and frequently-visited resource locations.

---

## Branch 5: Knowledge and Culture

*Progression: oral tradition → weaving / ceremony → writing → record keeping / education → governance.*

*This branch represents humanity's social and intellectual development. Unlike other branches, several Knowledge recipes have no resource cost or unusual triggers — they emerge from community practice rather than material experimentation. The branch's power is in enabling and amplifying other branches: oral tradition enables communal building, writing preserves knowledge permanently, record keeping improves food management, education boosts experimentation.*

| Recipe ID | Era | Inputs | Prerequisites | Base % | Mechanical Effect | Announce |
|---|---|---|---|---|---|---|
| `oral_tradition` | 3 | None | None (auto-trigger) | Auto | Auto-triggers when settlement has propagated 5 discoveries through the oral knowledge system. Oral propagation window reduced from 2 sim-days to 1 sim-day. Partial knowledge loss probability on discoverer death during propagation reduced [RECALIBRATE exact reduction]. Prerequisite for communal_building. No resource cost, no probability roll. | STANDARD |
| `weaving` | 4 | 2 Grain + 1 Wood | farming, stone_knife | 12% | Textile production. Shelter insulation: +1 health regen bonus for all shelters in settlement. Trade goods [FUTURE]. Fiber-based tools and bindings. | STANDARD |
| `ceremony` | 4 | 2 Food + 1 Wood | oral_tradition, communal_building | 6% | Organized communal rituals and gatherings. Bond formation speed 2× within settlement. Collaboration bonus +2% (stacks with base). Social cohesion strengthens the community. | STANDARD |
| `writing` | 5 | 1 Stone | pottery, refined_tools | 4% | Knowledge propagation becomes near-instant and permanent. Discoveries are recorded — they persist in the settlement knowledge base even if all agents who knew them die. Eliminates knowledge loss from death. Writing surfaces (clay tablets) + stylus tools. Civilization-defining breakthrough. | **MILESTONE** |
| `record_keeping` | 6 | 1 Stone | writing, granary | 3% | Systematic written records of food stores and resource inventories. Proactive food management: agents gather food before personal hunger crisis (lower hunger threshold for gather action). Cross-branch: Knowledge + Food infrastructure. | STANDARD |
| `education` | 6 | 1 Stone + 1 Wood | writing, communal_building | 3% | Formalized knowledge transfer in dedicated spaces. Experiment success rate +5% settlement-wide (all experimenters in the settlement benefit). The community invests in learning. | STANDARD |
| `governance` | 7 | 1 Stone | record_keeping, communal_building | 2% | Organized leadership and community decision-making structures. Collaboration bonus doubled (from +2% to +4% per adjacent agent, cap remains +10%). Community labor efficiency +25% for all Build actions [FUTURE: role specialization, community priorities]. | STANDARD |

**Branch notes:**
- `pottery` removed from this branch and moved to Fire (Branch 2). Pottery is a fire/material technology, not a knowledge/culture innovation. This refocuses the Knowledge branch on social and intellectual developments.
- `weaving` moved from Era 3 back to Era 4 to reduce Era 3 bloat. It fits the "Community" era — textiles are a community-scale production activity.
- `ceremony` is new at Era 4. Requires oral_tradition (you need stories and traditions) + communal_building (you need a place to gather). Effect is social: stronger bonds and better collaboration. This is the Knowledge branch's contribution to community infrastructure.
- `writing` remains the branch's crown achievement and one of the tree's most important milestones. Its effect — making knowledge permanent and loss-proof — is genuinely civilization-defining. Low resource cost (1 Stone for a clay tablet) but low discovery chance (4%) and high prerequisites (pottery + refined_tools + Era 5 gate).
- `education` is new at Era 6. The +5% experiment bonus settlement-wide is powerful — it accelerates all further discovery. Requires both the intellectual infrastructure (writing) and the physical infrastructure (communal_building as a school/academy).
- `governance` is new at Era 7. V1.8 effect: doubled collaboration bonus + build efficiency. Future effect: role specialization, community labor allocation, settlement priorities. Represents the culmination of social organization.
- This branch is acknowledged as the thinnest and most future-dependent. It's deliberately left lean for v1.8 with clear expansion hooks for governance systems, trade, diplomacy, and culture mechanics in future versions.

---

## Cross-Branch Dependency Map

These are every recipe that depends on knowledge from a different branch. They're the moments where parallel development lines converge — the "interesting bottlenecks" that make the tree feel interconnected rather than like five separate ladders.

### Era 2 Connections
| Recipe | Home Branch | Requires From | Logic |
|---|---|---|---|
| farming | Food | stone_knife (Tools) | Need cutting tools to work soil and harvest grain |

### Era 3 Connections
| Recipe | Home Branch | Requires From | Logic |
|---|---|---|---|
| improved_shelter | Shelter | clay_hardening (Fire) | Mud-brick construction needs knowledge of hardening clay |
| land_clearing | Food | fire (Fire), crude_axe (Tools) | Burn forest + chop remaining wood |
| food_preservation | Food | cooking (Fire) | Must understand fire-based food preparation first |
| animal_domestication | Food | lean_to (Shelter) | Need a settlement/pen site for keeping animals |

### Era 4 Connections
| Recipe | Home Branch | Requires From | Logic |
|---|---|---|---|
| granary | Food | pottery (Fire) | Storage vessels needed for large-scale food preservation |
| crop_rotation | Food | refined_tools (Tools) | Precision tools needed for systematic planting |
| walls | Shelter | hafted_tools (Tools) | Heavy tools needed to cut and place stone |
| communal_building | Shelter | oral_tradition (Knowledge) | Social organization needed before communal infrastructure |
| weaving | Knowledge | farming (Food), stone_knife (Tools) | Grain fiber + cutting tools for textile production |

### Era 5 Connections
| Recipe | Home Branch | Requires From | Logic |
|---|---|---|---|
| copper_working | Tools | smelting (Fire) | Must know how to melt ore before shaping the result |
| writing | Knowledge | pottery (Fire), refined_tools (Tools) | Clay tablets (pottery) + stylus tools (refined_tools) |
| plow | Food | copper_working (Tools) | Metal-tipped farming implement |
| stone_masonry | Shelter | quarrying_tools (Tools) | Specialized stone-cutting tools for precision work |

### Era 6 Connections
| Recipe | Home Branch | Requires From | Logic |
|---|---|---|---|
| monument | Shelter | bronze_working (Tools), communal_building (Shelter — internal), stone_masonry (Shelter — internal) | Metal tools + organized labor + stone expertise |
| record_keeping | Knowledge | granary (Food) | Written inventory tracking of food stores |
| education | Knowledge | communal_building (Shelter) | Dedicated learning spaces |

### Era 7 Connections
| Recipe | Home Branch | Requires From | Logic |
|---|---|---|---|
| iron_working | Tools | advanced_metallurgy (Fire) | Higher furnace temperatures from Fire branch |
| advanced_architecture | Shelter | iron_working (Tools) | Metal-reinforced construction |
| governance | Knowledge | communal_building (Shelter) | Physical infrastructure for administration |

### The Big Story

The tree's visual narrative comes from two major convergence arcs:

**Arc 1: The Metal Age (Eras 4–7).** Fire and Tools run as parallel tracks through the early game. Fire masters heat: pottery → kiln → charcoal → smelting. Tools masters materials: stone_knife → refined_tools → hafted_tools → quarrying_tools. They converge at **smelting + hafted_tools → copper_working** (Era 5). This is the most dramatic cross-branch moment in the tree — the transition from Stone Age to Metal Age requires *both* branches to have reached Era 5 capability. Then they continue to leapfrog: bronze (Tools 6) → advanced_metallurgy (Fire 6) → iron (Tools 7) → steel (Tools 7).

**Arc 2: The Knowledge Revolution (Eras 3–7).** Knowledge starts as an amplifier: oral_tradition speeds up what other branches produce. Writing (Era 5) transforms knowledge from fragile to permanent. Then Knowledge feeds *back* into other branches: record_keeping improves food management, education boosts experimentation for all branches, governance improves construction efficiency. The Knowledge branch is weak alone but makes everything else stronger.

---

## Era-by-Era Recipe List

For reference, here is every recipe organized by era (for era gate verification).

### Era 0 — Innate
| Recipe | Branch |
|---|---|
| clothing | — (innate, all agents know at birth) |

### Era 1 — Survival (5 recipes, gate: know 2+ to attempt Era 2)
| Recipe | Branch |
|---|---|
| stone_knife | Tools |
| crude_axe | Tools |
| fire | Fire |
| foraging_knowledge | Food |
| lean_to | Shelter |

### Era 2 — Adaptation (6 recipes, gate: know 3+ to attempt Era 3)
| Recipe | Branch |
|---|---|
| refined_tools | Tools |
| bone_tools | Tools |
| cooking | Fire |
| clay_hardening | Fire |
| farming | Food |
| reinforced_shelter | Shelter |

### Era 3 — Settlement (8 recipes, gate: know 3+ to attempt Era 4)
| Recipe | Branch |
|---|---|
| hafted_tools | Tools |
| pottery | Fire |
| kiln | Fire |
| food_preservation | Food |
| animal_domestication | Food |
| land_clearing | Food |
| improved_shelter | Shelter |
| oral_tradition | Knowledge (auto-trigger) |

### Era 4 — Community (10 recipes, gate: know 4+ to attempt Era 5)
| Recipe | Branch |
|---|---|
| quarrying_tools | Tools |
| charcoal | Fire |
| granary | Food |
| irrigation | Food |
| crop_rotation | Food |
| walls | Shelter |
| communal_building | Shelter |
| roads | Shelter |
| weaving | Knowledge |
| ceremony | Knowledge |

### Era 5 — Specialization (5 recipes, gate: know 2+ to attempt Era 6)
| Recipe | Branch |
|---|---|
| copper_working | Tools |
| smelting | Fire |
| plow | Food |
| stone_masonry | Shelter |
| writing | Knowledge |

### Era 6 — Industry (5 recipes, gate: know 2+ to attempt Era 7)
| Recipe | Branch |
|---|---|
| bronze_working | Tools |
| advanced_metallurgy | Fire |
| monument | Shelter |
| record_keeping | Knowledge |
| education | Knowledge |

### Era 7 — Civilization (4 recipes, end of current tree)
| Recipe | Branch |
|---|---|
| iron_working | Tools |
| steel_working | Tools |
| advanced_architecture | Shelter |
| governance | Knowledge |

---

## Phase 3: Milestone Review

### Complete Milestone List (10 milestones)

| Milestone | Era | Branch | Why This Is Era-Defining |
|---|---|---|---|
| `fire` | 1 | Fire | The first transformative discovery. Fire changes everything — it enables cooking, clay work, warmth, and is the foundation for the entire Fire branch through to smelting. When the observer sees "FIRE DISCOVERED," they know the simulation has truly begun. |
| `farming` | 2 | Food | The agricultural revolution. The transition from gathering food to producing it. This is the moment a group of wandering foragers begins to become a settled civilization. Farm tiles appearing near the settlement is a visible, emotional shift in behavior. |
| `improved_shelter` | 3 | Shelter | The first permanent dwelling. A mud-brick house is qualitatively different from a lean-to — it's a *home*. This milestone marks the transition from survival to settlement. "They built a real house" is the Era 3 moment. |
| `communal_building` | 4 | Shelter | The settlement becomes a community. Building a shared structure requires social organization (oral_tradition prereq) and represents the first collective decision: "we are building something together that belongs to all of us." The Community era's defining moment. |
| `smelting` | 5 | Fire | The discovery that rocks can become metal. This is the gateway between the Stone Age and the Metal Age — the single most transformative technological breakthrough in the tree. Even before copper is shaped into tools, the knowledge that ore melts into a workable substance is the conceptual revolution. |
| `copper_working` | 5 | Tools | First metal tools. The practical payoff of smelting — agents now have copper implements that outperform any stone tool. Two milestones at Era 5 is justified: smelting is the capability, copper_working is the result. Together they form the Stone-to-Metal transition. |
| `writing` | 5 | Knowledge | Knowledge becomes permanent. Discoveries are recorded and survive the death of every agent who knew them. This is the moment a civilization stops being fragile — knowledge can no longer be lost to a bad winter or a failed expedition. Three milestones at Era 5 reflects that this is the most transformative era in the tree: mastery of metal AND mastery of knowledge in a single generation. |
| `bronze_working` | 6 | Tools | First alloy. The realization that mixing metals creates something stronger than either is a conceptual leap beyond "heat rock, shape metal." Bronze is harder, more durable, and defines an entire age of human civilization. The Bronze Age begins. |
| `iron_working` | 7 | Tools | Iron Age begins. Higher temperatures, harder metal, more abundant ore. Iron tools represent the highest tier of practical technology in the tree. The observer has watched this civilization journey from bare hands to iron — a span of simulated centuries. |
| `steel_working` | 7 | Tools | The ultimate material technology. Controlled carbon content in iron produces the strongest, most versatile material in the tree. Two milestones at Era 7 mirrors Era 5 — both are culmination eras where major breakthroughs cluster. |

### Milestone Pacing

| Era | Milestones | Notes |
|---|---|---|
| 0 | 0 | Innate knowledge, no discovery moment |
| 1 | 1 | fire |
| 2 | 1 | farming |
| 3 | 1 | improved_shelter |
| 4 | 1 | communal_building |
| 5 | 3 | smelting + copper_working + writing (the defining era: Metal Age + Knowledge permanence) |
| 6 | 1 | bronze_working |
| 7 | 2 | iron_working + steel_working (culmination era) |

10 milestones across 7 eras. Era 5 is the peak with three milestones — justified because it represents the two most transformative breakthroughs in human history occurring in the same era: metalworking and written language. This can be tuned down in config if it feels like too much during playtesting.

### Recipes NOT Tagged as Milestones (and Why)

These were candidates I considered and rejected:

- **`oral_tradition`** — Auto-trigger, no experiment roll, no resource cost. Milestones should feel earned through intentional experimentation, not granted automatically. A toast notification is appropriate.
- **`monument`** — A communal project, not a eureka discovery. The monument itself is visually impressive (a year-long build project); it doesn't need a discovery fanfare on top.
- **`advanced_architecture`** — Era 7 capstone for Shelter, but iron_working and steel_working are the defining Era 7 moments. Advanced_architecture is a consequence, not a breakthrough.

---

## Open Items and Flags

### Resolved
- **Era 4 gate raised from 3 to 4** — Approved.
- **Writing as milestone** — Approved. Tagged MILESTONE. Configurable in recipe JSON if playtesting shows Era 5 has too many milestone popups.
- **Roads implementation** — Approved. Per-tile cost modification is a known planned feature. Implementation responsibility with project lead.
- **Gathering multiplier stacking** — Resolved: **replacement within category, not stacking.** Tool multipliers replace each other — copper_working's 2.5× replaces hafted_tools' 2.0×. An agent uses their best tool, they don't dual-wield axes. Cross-category bonuses (e.g., foraging_knowledge's +50% food yield) stack multiplicatively with the active tool multiplier since they represent different types of knowledge.

### Still Open

### Flag 1: Bone Tools Exposure Effect
`bone_tools` includes a clothing upgrade effect (exposure damage 0.15 → 0.10). This modifies the innate `clothing` effect. The implementation needs to clarify: does this create a new "improved_clothing" state, or does it modify the clothing constant? Recommend: it's a modifier applied when `bone_tools` is in the agent's knowledge set, not a separate recipe. Simple conditional check.

### Flag 2: `basic_tools` Migration
`basic_tools` from the original GDD is replaced by `stone_knife` + `crude_axe`. Any existing code referencing `basic_tools` as a recipe ID will need to be updated. Mapping:
- `basic_tools` as prerequisite for cutting/processing tasks → `stone_knife`
- `basic_tools` as prerequisite for wood/construction tasks → `crude_axe`
- `basic_tools` as prerequisite for general tool advancement → `stone_knife` (the more fundamental of the two)
- `basic_tools` as prerequisite for land_clearing → `crude_axe` (chopping task)
- `basic_tools` as prerequisite for weaving → `stone_knife` (fiber cutting)

### Calibration Warnings (from leadership review)

### Warning 1: Gathering Multiplier Escalation
Tool multipliers replace within category and stack multiplicatively with cross-category bonuses (e.g., foraging_knowledge). This means effective food gathering rates escalate significantly through the tree:

| Tech Level | Tool Multiplier | × Foraging Knowledge (1.5×) | Effective Food Gathering |
|---|---|---|---|
| No tools | 1.0× | 1.5× | 1.5× |
| stone_knife | 1.25× | 1.875× | 1.875× |
| hafted_tools | 2.0× | 3.0× | 3.0× |
| copper_working | 2.5× | 3.75× | 3.75× |
| iron_working | 4.0× | 6.0× | 6.0× |
| steel_working | 4.5× | 6.75× | 6.75× |

A 6–7× food gathering rate at iron/steel could trivialize food scarcity in mid-to-late game, undermining the resource pressure that drives exploration and community tension. The calibration pass MUST validate these multipliers against resource availability, regeneration rates, and population growth curves. If food becomes a non-issue by Era 5, the survival tension collapses and the simulation loses drama. Possible mitigations: flatten the late-game tool curve (e.g., iron 3.5× instead of 4.0×), or make foraging_knowledge's bonus smaller (+25% instead of +50%), or increase population food demand at higher population counts. This is a calibration decision, not a design change — the multiplier *structure* is correct, the *numbers* need tuning.

### Warning 2: Reinforced Shelter Stability Band
`reinforced_shelter` sits at reproduction stability score ~0.55, between lean_to (~0.4) and improved_shelter (~0.7). That's a 0.15 increment on each side. Since shelter quality has weight 0.2 in the stability formula, the actual impact on the composite score is:

- lean_to: 0.2 × 0.4 = 0.08 contribution
- reinforced_shelter: 0.2 × 0.55 = 0.11 contribution
- improved_shelter: 0.2 × 0.7 = 0.14 contribution

The difference between lean_to and reinforced_shelter in the composite score is only 0.03. If other factors (food security, dependents, health) dominate the score — which they will in most situations — this 0.03 increment may not meaningfully change reproduction behavior. The calibration pass should verify that upgrading from lean_to to reinforced_shelter produces an observable difference in reproduction timing. If it doesn't, either widen the stability band (e.g., lean_to ~0.3, reinforced ~0.55, improved ~0.8) or increase shelter quality's weight in the formula.

---

*This completes the Phase 2 recipe table. Phase 3 milestone review is included above. All recipes are designed to be compatible with v1.8 systems as specified in the Coder Implementation Package. Effects marked [FUTURE] are noted for design intent only and should not be implemented until their dependent systems exist.*
