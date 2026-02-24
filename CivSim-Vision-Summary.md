# CivSim — Vision Summary

**Last Updated:** February 2026  
**Author:** Project Creator (captured via structured interview)  
**Purpose:** This document is the source of truth for what CivSim is supposed to BE and FEEL like. Read this before the GDD. The GDD tells you *what* to build. This tells you *why*.

---

## The Elevator Pitch

Two people are dropped into a fresh procedural world with nothing. Over the course of generations, they and their descendants discover technology, build shelter, form communities, and progress through the eras of civilization. The player watches it unfold. No two runs are the same.

## The Feeling

Imagine sitting down one evening and starting a run. You watch Adam and Eve find a cave, figure out that sticks and rocks can be lashed together into crude tools, start storing food. You pause and go to bed. The next evening, you come back. Their children are grown. Someone figured out pottery. The settlement has three shelters now. You're invested in these people.

Days later, the settlement has copper tools. A generation after that, someone mixes tin with copper and discovers bronze. The tech tree — which you can see in full, like a Rimworld or KSP research screen — is slowly lighting up. Each lit node represents real time and real struggle by people you watched grow up.

That's CivSim.

## What Makes It Different

The inspiration comes from a frustration with survival games: in Minecraft, you punch a tree and five minutes later you're in the iron age. The progression from "nothing" to "advanced technology" is compressed into meaninglessness. CivSim asks: what about all the steps in between? What about the crude tools before stone tools? Copper before iron? Bronze? Glass from river sand? Machinery? The massive, fascinating middle ground between "dirt and sticks" and "rockets to the moon" — and the fact that no single person lives long enough to see the whole journey. It takes generations.

## Core Principles

**Generational, not individual.** The arc spans lifetimes. A discovery in generation 3 builds on what generation 1 found. Knowledge passes forward through community and story — "the elders say there's shiny stone in the eastern cliffs."

**Earned, not rushed.** The time between discoveries is the content. A community living with fire for years before someone figures out clay is not dead air — it's life being lived. The creator has explicitly said: 50-minute runs are fine. Multi-session runs across days are fine. Do not compress the experience.

**Settlers, not wanderers.** Agents have homes. They go out and come back. They store food, craft tools, raise children at home. Exploration radiates outward as stability allows. A trip to a distant copper deposit is a real expedition with preparation and risk.

**Responsible, not mechanical.** Agents don't reproduce on a timer. They have children when they can sustain them — when food is secure, shelter exists, and the workload allows it. Two starving adults having a baby is a bug, not emergence.

**Deep, not wide.** The tech tree progresses through meaningful intermediate steps. Stone → lashed stone axe → copper → bronze → iron → steel. Each step opens new possibilities. Breadth at a single tier matters less than depth across tiers.

**Watchable, not optimized.** The simulation is meant to be observed. Clean UI. Readable agent names (real human names, not random strings). Prominent announcements for milestone discoveries. A visible tech tree you can browse. You should be able to glance at the screen and understand what's happening.

## What Success Looks Like

The creator imagines showing friends: "I ran this simulation for 24 hours total across multiple sessions. These two people started with literally nothing. Over simulated centuries, their descendants worked through the progression of human technology — stone tools, fire, copper, bronze, iron, machinery — and eventually reached the space age. Every step was earned. Every generation carried the work forward."

That's the target. Everything we build serves that vision.

## What Success Does NOT Look Like

- 200 agents alive at tick 1000 (this is "Baby Making Simulator")
- Full tech tree unlocked in 5 minutes of wall-clock time
- Agents scattered randomly across the map with no home behavior
- A log full of teaching spam
- 1 FPS performance
- Every tile stuffed with instantly-respawning resources
- Features that exist but feel shallow and broken
- New mechanics added before existing ones work well

---

*This document captures the project creator's vision as expressed through multiple structured conversations in February 2026. It should be referenced alongside, and takes priority over, any team member's interpretation of the GDD.*
