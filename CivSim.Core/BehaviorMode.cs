namespace CivSim.Core;

/// <summary>
/// v1.8 Behavioral Modes: An agent is always in exactly one mode.
/// Each mode defines which actions are available — everything else is suppressed.
/// Mode transitions are explicit with hysteresis to prevent oscillation.
/// </summary>
public enum BehaviorMode
{
    /// <summary>"I'm at my settlement, doing settlement things."
    /// Actions: Eat, Rest, Experiment, Craft, Deposit, Socialize, DepositHome, Reproduce.</summary>
    Home,

    /// <summary>"I'm on a trip to gather something specific and bring it home."
    /// Actions: Move (toward target), Gather, Eat (inventory only, emergency).</summary>
    Forage,

    /// <summary>"I'm working on a construction project until it's done."
    /// Actions: Build, Gather (materials if short), Eat, Rest (at night).</summary>
    Build,

    /// <summary>"I'm scouting in a committed direction to see what's out there."
    /// Actions: Move (sustained direction), Gather (opportunistic), Eat (inventory).</summary>
    Explore,

    /// <summary>"I have young children. Everything I do is shaped by that."
    /// Lens over Home + short-range Forage + Feed Child. Blocks Explore, limits range.</summary>
    Caretaker,

    /// <summary>"I'm in immediate danger. Everything else stops."
    /// Actions: Eat, Seek Food, Seek Shelter, Rest.</summary>
    Urgent
}
