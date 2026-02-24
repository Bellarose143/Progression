namespace CivSim.Core;

/// <summary>
/// A data-driven definition for a discoverable recipe or technology.
/// Pure data class — no behavior. GDD v1.8: 44 recipes across 5 branches, Eras 0–7.
/// </summary>
public class Recipe
{
    /// <summary>Unique identifier for this recipe (e.g., "fire", "stone_knife").</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable name (e.g., "Fire", "Stone Knife").</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Resources consumed when an agent attempts this recipe.
    /// Key = resource type, Value = quantity required.
    /// </summary>
    public Dictionary<ResourceType, int> RequiredResources { get; set; } = new();

    /// <summary>
    /// Discovery IDs the agent must already know before attempting this recipe.
    /// Empty list means no prerequisites (Era 1 recipes).
    /// </summary>
    public List<string> RequiredKnowledge { get; set; } = new();

    /// <summary>Base probability (0.0–1.0) of success per attempt before modifiers.</summary>
    public float BaseChance { get; set; }

    /// <summary>The discovery ID string added to the agent's Knowledge on success.</summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>Human-readable descriptions of what this discovery enables. For UI/logging only.</summary>
    public List<string> Effects { get; set; } = new();

    /// <summary>Tech era (0–7). Higher eras require more prerequisites and take longer to discover.</summary>
    public int Tier { get; set; }

    /// <summary>Flavor text describing the discovery. For UI/logging.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Which branch of the tech tree this recipe belongs to.</summary>
    public string Branch { get; set; } = string.Empty;

    /// <summary>
    /// Announcement level for discovery notifications.
    /// "MILESTONE" = large centered popup. "STANDARD" = toast notification.
    /// </summary>
    public string AnnouncementLevel { get; set; } = "STANDARD";
}

/// <summary>
/// Static registry of all 44 recipes in the simulation (GDD v1.8 Phase 2 Tech Tree).
/// 5 branches: Tools & Materials, Fire & Heat, Food & Agriculture, Shelter & Construction, Knowledge & Culture.
/// 8 eras: Innate (0) through Civilization (7). 10 milestones.
/// </summary>
public static class RecipeRegistry
{
    public static readonly List<Recipe> AllRecipes = new()
    {
        // ══════════════════════════════════════════════════════════════════
        // Era 0: Innate
        // ══════════════════════════════════════════════════════════════════
        new Recipe
        {
            Id = "clothing", Name = "Clothing", Tier = 0, Branch = "Knowledge",
            RequiredResources = new(),
            RequiredKnowledge = new(),
            BaseChance = 0f, // Innate — all agents know at birth
            Output = "clothing",
            Effects = new() { "50% less movement hunger cost" },
            Description = "Innate knowledge of fashioning protective garments."
        },

        // ══════════════════════════════════════════════════════════════════
        // Era 1: Survival — Branch 1: Tools & Materials
        // ══════════════════════════════════════════════════════════════════
        new Recipe
        {
            Id = "stone_knife", Name = "Stone Knife", Tier = 1, Branch = "Tools",
            RequiredResources = new() { { ResourceType.Stone, 1 } },
            RequiredKnowledge = new(),
            BaseChance = 0.12f,
            Output = "stone_knife",
            Effects = new() { "Food gathering 1.25x", "Enables hide and bone processing" },
            Description = "A sharp edge knapped from stone. The first cutting tool."
        },
        new Recipe
        {
            Id = "crude_axe", Name = "Crude Axe", Tier = 1, Branch = "Tools",
            RequiredResources = new() { { ResourceType.Wood, 2 }, { ResourceType.Stone, 1 } },
            RequiredKnowledge = new(),
            BaseChance = 0.10f,
            Output = "crude_axe",
            Effects = new() { "Wood gathering 1.5x", "Enables efficient wood harvesting" },
            Description = "A heavy stone wedged into wood. The first chopping tool."
        },

        // Era 1: Survival — Branch 2: Fire & Heat
        new Recipe
        {
            Id = "fire", Name = "Fire", Tier = 1, Branch = "Fire",
            RequiredResources = new() { { ResourceType.Wood, 3 } },
            RequiredKnowledge = new(),
            BaseChance = 0.08f,
            Output = "fire",
            AnnouncementLevel = "MILESTONE",
            Effects = new() { "Unlocks cooking", "Exposure damage halved within 2 tiles" },
            Description = "Controlled flame. Everything changes."
        },

        // Era 1: Survival — Branch 3: Food & Agriculture
        new Recipe
        {
            Id = "foraging_knowledge", Name = "Foraging Knowledge", Tier = 1, Branch = "Food",
            RequiredResources = new() { { ResourceType.Berries, 1 } },
            RequiredKnowledge = new(),
            BaseChance = 0.15f,
            Output = "foraging_knowledge",
            Effects = new() { "Food gathering yield +50% from natural sources" },
            Description = "Knowledge of edible plants and seasonal food sources."
        },

        // Era 1: Survival — Branch 4: Shelter & Construction
        new Recipe
        {
            Id = "lean_to", Name = "Lean-To", Tier = 1, Branch = "Shelter",
            RequiredResources = new() { { ResourceType.Wood, 3 } },
            RequiredKnowledge = new(),
            BaseChance = 0.10f,
            Output = "lean_to",
            Effects = new() { "Build lean-to shelter", "Eliminates exposure within 3 tiles", "Health regen 2x", "Home storage 10", "Enables reproduction" },
            Description = "A simple windbreak and roof. The first shelter."
        },

        // ══════════════════════════════════════════════════════════════════
        // Era 2: Adaptation — Branch 1: Tools & Materials
        // ══════════════════════════════════════════════════════════════════
        new Recipe
        {
            Id = "refined_tools", Name = "Refined Tools", Tier = 2, Branch = "Tools",
            RequiredResources = new() { { ResourceType.Stone, 2 }, { ResourceType.Wood, 1 } },
            RequiredKnowledge = new() { "stone_knife" },
            BaseChance = 0.10f,
            Output = "refined_tools",
            Effects = new() { "All gathering 1.75x", "Enables quarrying stone" },
            Description = "Better stone shaping techniques with polished edges."
        },
        new Recipe
        {
            Id = "bone_tools", Name = "Bone Tools", Tier = 2, Branch = "Tools",
            RequiredResources = new() { { ResourceType.Animals, 2 } },
            RequiredKnowledge = new() { "stone_knife" },
            BaseChance = 0.12f,
            Output = "bone_tools",
            Effects = new() { "Fishing efficiency 2x", "Clothing upgrade: exposure damage 0.15 to 0.10" },
            Description = "Needles and hooks carved from animal bones."
        },

        // Era 2: Adaptation — Branch 2: Fire & Heat
        new Recipe
        {
            Id = "cooking", Name = "Cooking", Tier = 2, Branch = "Fire",
            RequiredResources = new() { { ResourceType.Animals, 1 } },
            RequiredKnowledge = new() { "fire" },
            BaseChance = 0.20f,
            Output = "cooking",
            Effects = new() { "Cooked food restores 60 hunger (+50% over raw)" },
            Description = "Preparing food over fire increases its nutritional value."
        },
        new Recipe
        {
            Id = "clay_hardening", Name = "Clay Hardening", Tier = 2, Branch = "Fire",
            RequiredResources = new() { { ResourceType.Stone, 2 } },
            RequiredKnowledge = new() { "fire" },
            BaseChance = 0.12f,
            Output = "clay_hardening",
            Effects = new() { "Building speed +25%", "Prerequisite for pottery and mud-brick" },
            Description = "Fire makes clay hard and durable. A foundational material discovery."
        },

        // Era 2: Adaptation — Branch 3: Food & Agriculture
        new Recipe
        {
            Id = "farming", Name = "Farming", Tier = 2, Branch = "Food",
            RequiredResources = new() { { ResourceType.Grain, 2 }, { ResourceType.Wood, 1 } },
            RequiredKnowledge = new() { "stone_knife" },
            BaseChance = 0.10f,
            Output = "farming",
            AnnouncementLevel = "MILESTONE",
            Effects = new() { "Tend Farm action available", "Grain regenerates on farmed tiles" },
            Description = "Deliberate cultivation of grain. The agricultural revolution begins."
        },

        // Era 2: Adaptation — Branch 4: Shelter & Construction
        new Recipe
        {
            Id = "reinforced_shelter", Name = "Reinforced Shelter", Tier = 2, Branch = "Shelter",
            RequiredResources = new() { { ResourceType.Wood, 3 }, { ResourceType.Stone, 2 } },
            RequiredKnowledge = new() { "lean_to" },
            BaseChance = 0.10f,
            Output = "reinforced_shelter",
            Effects = new() { "Health regen 2.5x", "Home storage 15", "Shelter decay -25%" },
            Description = "Sturdier wood-and-stone shelter. A real improvement."
        },

        // ══════════════════════════════════════════════════════════════════
        // Era 3: Settlement — Branch 1: Tools & Materials
        // ══════════════════════════════════════════════════════════════════
        new Recipe
        {
            Id = "hafted_tools", Name = "Hafted Tools", Tier = 3, Branch = "Tools",
            RequiredResources = new() { { ResourceType.Wood, 2 }, { ResourceType.Stone, 2 } },
            RequiredKnowledge = new() { "crude_axe", "refined_tools" },
            BaseChance = 0.08f,
            Output = "hafted_tools",
            Effects = new() { "All gathering 2.0x", "Wood gathering 2.5x", "Enables heavy construction" },
            Description = "Stone heads lashed to wooden handles. The breakthrough."
        },

        // Era 3: Settlement — Branch 2: Fire & Heat
        new Recipe
        {
            Id = "pottery", Name = "Pottery", Tier = 3, Branch = "Fire",
            RequiredResources = new() { { ResourceType.Stone, 2 } },
            RequiredKnowledge = new() { "clay_hardening" },
            BaseChance = 0.10f,
            Output = "pottery",
            Effects = new() { "Food decay 50% slower in shelter", "Prerequisite for kiln, granary, writing" },
            Description = "Shaped and fired clay vessels for storage."
        },
        new Recipe
        {
            Id = "kiln", Name = "Kiln", Tier = 3, Branch = "Fire",
            RequiredResources = new() { { ResourceType.Stone, 3 } },
            RequiredKnowledge = new() { "fire", "pottery" },
            BaseChance = 0.08f,
            Output = "kiln",
            Effects = new() { "Shelter storage capacity 2x", "Prerequisite for charcoal and smelting" },
            Description = "Controlled high-temperature firing chamber."
        },

        // Era 3: Settlement — Branch 3: Food & Agriculture
        new Recipe
        {
            Id = "food_preservation", Name = "Food Preservation", Tier = 3, Branch = "Food",
            RequiredResources = new() { { ResourceType.Animals, 2 } },
            RequiredKnowledge = new() { "cooking" },
            BaseChance = 0.12f,
            Output = "food_preservation",
            Effects = new() { "Preserve: 2 food -> 1 preserved (80 hunger, no decay)" },
            Description = "Drying, smoking, and salting food for long-term storage."
        },
        new Recipe
        {
            Id = "animal_domestication", Name = "Animal Domestication", Tier = 3, Branch = "Food",
            RequiredResources = new() { { ResourceType.Animals, 3 } },
            RequiredKnowledge = new() { "farming", "lean_to" },
            BaseChance = 0.06f,
            Output = "animal_domestication",
            Effects = new() { "Penned animals near settlement don't despawn" },
            Description = "Taming and breeding animals for sustainable food."
        },
        new Recipe
        {
            Id = "land_clearing", Name = "Land Clearing", Tier = 3, Branch = "Food",
            RequiredResources = new() { { ResourceType.Wood, 3 } },
            RequiredKnowledge = new() { "fire", "crude_axe" },
            BaseChance = 0.12f,
            Output = "land_clearing",
            Effects = new() { "Clear forest tile -> plains (multi-day action)" },
            Description = "Controlled burning and cutting to create farmable land."
        },

        // Era 3: Settlement — Branch 4: Shelter & Construction
        new Recipe
        {
            Id = "improved_shelter", Name = "Improved Shelter", Tier = 3, Branch = "Shelter",
            RequiredResources = new() { { ResourceType.Wood, 5 }, { ResourceType.Stone, 5 } },
            RequiredKnowledge = new() { "lean_to", "clay_hardening" },
            BaseChance = 0.08f,
            Output = "improved_shelter",
            AnnouncementLevel = "MILESTONE",
            Effects = new() { "Health regen 3x", "Home storage 20", "A real home" },
            Description = "Mud-brick dwelling. Not just cover from the rain."
        },

        // Era 3: Settlement — Branch 5: Knowledge & Culture
        new Recipe
        {
            Id = "oral_tradition", Name = "Oral Tradition", Tier = 3, Branch = "Knowledge",
            RequiredResources = new(),
            RequiredKnowledge = new(),
            BaseChance = 0f, // Auto-triggers when settlement propagates 5 discoveries
            Output = "oral_tradition",
            Effects = new() { "Oral propagation window 2 -> 1 sim-day", "Reduced knowledge loss on discoverer death" },
            Description = "The community formalizes its storytelling traditions."
        },

        // ══════════════════════════════════════════════════════════════════
        // Era 4: Community — Branch 1: Tools & Materials
        // ══════════════════════════════════════════════════════════════════
        new Recipe
        {
            Id = "quarrying_tools", Name = "Quarrying Tools", Tier = 4, Branch = "Tools",
            RequiredResources = new() { { ResourceType.Stone, 3 }, { ResourceType.Wood, 2 } },
            RequiredKnowledge = new() { "hafted_tools" },
            BaseChance = 0.08f,
            Output = "quarrying_tools",
            Effects = new() { "Stone gathering 2.5x", "Enables efficient quarrying" },
            Description = "Chisels, wedges, and hammers for cutting stone."
        },

        // Era 4: Community — Branch 2: Fire & Heat
        new Recipe
        {
            Id = "charcoal", Name = "Charcoal", Tier = 4, Branch = "Fire",
            RequiredResources = new() { { ResourceType.Wood, 5 } },
            RequiredKnowledge = new() { "kiln" },
            BaseChance = 0.10f,
            Output = "charcoal",
            Effects = new() { "High-temperature fuel", "Required for smelting" },
            Description = "Wood burned in controlled low-oxygen conditions."
        },

        // Era 4: Community — Branch 3: Food & Agriculture
        new Recipe
        {
            Id = "granary", Name = "Granary", Tier = 4, Branch = "Food",
            RequiredResources = new() { { ResourceType.Wood, 5 }, { ResourceType.Stone, 3 } },
            RequiredKnowledge = new() { "pottery", "farming" },
            BaseChance = 0.08f,
            Output = "granary",
            Effects = new() { "Communal food storage: capacity 50, no decay" },
            Description = "Sealed storage for preserving food supplies."
        },
        new Recipe
        {
            Id = "irrigation", Name = "Irrigation", Tier = 4, Branch = "Food",
            RequiredResources = new() { { ResourceType.Stone, 3 }, { ResourceType.Wood, 1 } },
            RequiredKnowledge = new() { "farming" },
            BaseChance = 0.08f,
            Output = "irrigation",
            Effects = new() { "2x farm yield within 3 tiles of water" },
            Description = "Channeling water to crops for improved growth."
        },
        new Recipe
        {
            Id = "crop_rotation", Name = "Crop Rotation", Tier = 4, Branch = "Food",
            RequiredResources = new() { { ResourceType.Grain, 2 }, { ResourceType.Wood, 1 } },
            RequiredKnowledge = new() { "farming", "refined_tools" },
            BaseChance = 0.06f,
            Output = "crop_rotation",
            Effects = new() { "Farms never deplete" },
            Description = "Alternating crops to maintain soil fertility."
        },

        // Era 4: Community — Branch 4: Shelter & Construction
        new Recipe
        {
            Id = "walls", Name = "Walls", Tier = 4, Branch = "Shelter",
            RequiredResources = new() { { ResourceType.Stone, 10 } },
            RequiredKnowledge = new() { "hafted_tools", "improved_shelter" },
            BaseChance = 0.06f,
            Output = "walls",
            Effects = new() { "Settlement defense", "Reproduction stability ~0.9" },
            Description = "Stone walls for protection and territory marking."
        },
        new Recipe
        {
            Id = "communal_building", Name = "Communal Building", Tier = 4, Branch = "Shelter",
            RequiredResources = new() { { ResourceType.Wood, 10 }, { ResourceType.Stone, 10 } },
            RequiredKnowledge = new() { "oral_tradition", "improved_shelter" },
            BaseChance = 0.04f,
            Output = "communal_building",
            AnnouncementLevel = "MILESTONE",
            Effects = new() { "Knowledge propagation speed 1.5x", "Community gathering place" },
            Description = "A meeting hall. The settlement becomes a community."
        },
        new Recipe
        {
            Id = "roads", Name = "Roads", Tier = 4, Branch = "Shelter",
            RequiredResources = new() { { ResourceType.Stone, 3 }, { ResourceType.Wood, 2 } },
            RequiredKnowledge = new() { "hafted_tools" },
            BaseChance = 0.08f,
            Output = "roads",
            Effects = new() { "Build road tiles: movement cost -50%" },
            Description = "Cleared and improved paths between settlement and resources."
        },

        // Era 4: Community — Branch 5: Knowledge & Culture
        new Recipe
        {
            Id = "weaving", Name = "Weaving", Tier = 4, Branch = "Knowledge",
            RequiredResources = new() { { ResourceType.Grain, 2 }, { ResourceType.Wood, 1 } },
            RequiredKnowledge = new() { "farming", "stone_knife" },
            BaseChance = 0.12f,
            Output = "weaving",
            Effects = new() { "+1 health regen for all shelters in settlement" },
            Description = "Plant fibers woven into textiles and baskets."
        },
        new Recipe
        {
            Id = "ceremony", Name = "Ceremony", Tier = 4, Branch = "Knowledge",
            RequiredResources = new() { { ResourceType.Berries, 2 }, { ResourceType.Wood, 1 } },
            RequiredKnowledge = new() { "oral_tradition", "communal_building" },
            BaseChance = 0.06f,
            Output = "ceremony",
            Effects = new() { "Bond formation speed 2x", "Collaboration bonus +2%" },
            Description = "Organized communal rituals strengthening social cohesion."
        },

        // ══════════════════════════════════════════════════════════════════
        // Era 5: Specialization — Branch 1: Tools & Materials
        // ══════════════════════════════════════════════════════════════════
        new Recipe
        {
            Id = "copper_working", Name = "Copper Working", Tier = 5, Branch = "Tools",
            RequiredResources = new() { { ResourceType.Ore, 3 }, { ResourceType.Stone, 1 } },
            RequiredKnowledge = new() { "smelting", "hafted_tools" },
            BaseChance = 0.05f,
            Output = "copper_working",
            AnnouncementLevel = "MILESTONE",
            Effects = new() { "All gathering 2.5x", "First metal tools", "The Stone Age ends" },
            Description = "Shaping smelted copper into tools and implements."
        },

        // Era 5: Specialization — Branch 2: Fire & Heat
        new Recipe
        {
            Id = "smelting", Name = "Smelting", Tier = 5, Branch = "Fire",
            RequiredResources = new() { { ResourceType.Ore, 2 }, { ResourceType.Wood, 2 } },
            RequiredKnowledge = new() { "kiln", "charcoal" },
            BaseChance = 0.05f,
            Output = "smelting",
            AnnouncementLevel = "MILESTONE",
            Effects = new() { "Extract metal from ore", "Gateway to all metalworking" },
            Description = "Certain rocks, heated intensely, yield workable metal."
        },

        // Era 5: Specialization — Branch 3: Food & Agriculture
        new Recipe
        {
            Id = "plow", Name = "Plow", Tier = 5, Branch = "Food",
            RequiredResources = new() { { ResourceType.Wood, 2 }, { ResourceType.Ore, 1 } },
            RequiredKnowledge = new() { "copper_working", "farming" },
            BaseChance = 0.05f,
            Output = "plow",
            Effects = new() { "Farm yield 3x", "Deep-soil farming on any non-water/mountain tile" },
            Description = "Metal-tipped plow for efficient soil preparation."
        },

        // Era 5: Specialization — Branch 4: Shelter & Construction
        new Recipe
        {
            Id = "stone_masonry", Name = "Stone Masonry", Tier = 5, Branch = "Shelter",
            RequiredResources = new() { { ResourceType.Stone, 8 }, { ResourceType.Ore, 2 } },
            RequiredKnowledge = new() { "quarrying_tools", "walls" },
            BaseChance = 0.05f,
            Output = "stone_masonry",
            Effects = new() { "All shelter health regen +1x (stacks)", "Prerequisite for monument" },
            Description = "Cut stone blocks with fitted joints. Precision construction."
        },

        // Era 5: Specialization — Branch 5: Knowledge & Culture
        new Recipe
        {
            Id = "writing", Name = "Writing", Tier = 5, Branch = "Knowledge",
            RequiredResources = new() { { ResourceType.Stone, 1 } },
            RequiredKnowledge = new() { "pottery", "refined_tools" },
            BaseChance = 0.04f,
            Output = "writing",
            AnnouncementLevel = "MILESTONE",
            Effects = new() { "Knowledge propagation near-instant and permanent", "Discoveries persist even if all agents die", "Eliminates knowledge loss from death" },
            Description = "Recording knowledge in permanent form. Civilization-defining."
        },

        // ══════════════════════════════════════════════════════════════════
        // Era 6: Industry — Branch 1: Tools & Materials
        // ══════════════════════════════════════════════════════════════════
        new Recipe
        {
            Id = "bronze_working", Name = "Bronze Working", Tier = 6, Branch = "Tools",
            RequiredResources = new() { { ResourceType.Ore, 3 }, { ResourceType.Stone, 1 } },
            RequiredKnowledge = new() { "copper_working" },
            BaseChance = 0.03f,
            Output = "bronze_working",
            AnnouncementLevel = "MILESTONE",
            Effects = new() { "All gathering 3.0x", "Build speed 2x", "The Bronze Age begins" },
            Description = "Alloying tin and copper creates a harder metal."
        },

        // Era 6: Industry — Branch 2: Fire & Heat
        new Recipe
        {
            Id = "advanced_metallurgy", Name = "Advanced Metallurgy", Tier = 6, Branch = "Fire",
            RequiredResources = new() { { ResourceType.Stone, 3 }, { ResourceType.Ore, 2 } },
            RequiredKnowledge = new() { "smelting" },
            BaseChance = 0.04f,
            Output = "advanced_metallurgy",
            Effects = new() { "Higher sustained temperatures", "Required for iron working" },
            Description = "Advanced furnace design and temperature control."
        },

        // Era 6: Industry — Branch 4: Shelter & Construction
        new Recipe
        {
            Id = "monument", Name = "Monument", Tier = 6, Branch = "Shelter",
            RequiredResources = new() { { ResourceType.Stone, 20 }, { ResourceType.Ore, 5 } },
            RequiredKnowledge = new() { "stone_masonry", "bronze_working", "communal_building" },
            BaseChance = 0.02f,
            Output = "monument",
            Effects = new() { "Knowledge propagation speed 2x", "+5% experiment bonus in 3-tile radius" },
            Description = "Monumental stone construction. A civilization-defining structure."
        },

        // Era 6: Industry — Branch 5: Knowledge & Culture
        new Recipe
        {
            Id = "record_keeping", Name = "Record Keeping", Tier = 6, Branch = "Knowledge",
            RequiredResources = new() { { ResourceType.Stone, 1 } },
            RequiredKnowledge = new() { "writing", "granary" },
            BaseChance = 0.03f,
            Output = "record_keeping",
            Effects = new() { "Proactive food management: gather before hunger crisis" },
            Description = "Systematic written records of food stores and resources."
        },
        new Recipe
        {
            Id = "education", Name = "Education", Tier = 6, Branch = "Knowledge",
            RequiredResources = new() { { ResourceType.Stone, 1 }, { ResourceType.Wood, 1 } },
            RequiredKnowledge = new() { "writing", "communal_building" },
            BaseChance = 0.03f,
            Output = "education",
            Effects = new() { "Experiment success rate +5% settlement-wide" },
            Description = "Formalized knowledge transfer. The community invests in learning."
        },

        // ══════════════════════════════════════════════════════════════════
        // Era 7: Civilization — Branch 1: Tools & Materials
        // ══════════════════════════════════════════════════════════════════
        new Recipe
        {
            Id = "iron_working", Name = "Iron Working", Tier = 7, Branch = "Tools",
            RequiredResources = new() { { ResourceType.Ore, 4 }, { ResourceType.Stone, 2 } },
            RequiredKnowledge = new() { "bronze_working", "advanced_metallurgy" },
            BaseChance = 0.02f,
            Output = "iron_working",
            AnnouncementLevel = "MILESTONE",
            Effects = new() { "All gathering 4.0x", "Build speed 3x", "The Iron Age begins" },
            Description = "Iron tools require higher temperatures than bronze."
        },
        new Recipe
        {
            Id = "steel_working", Name = "Steel Working", Tier = 7, Branch = "Tools",
            RequiredResources = new() { { ResourceType.Ore, 4 }, { ResourceType.Wood, 3 } },
            RequiredKnowledge = new() { "iron_working" },
            BaseChance = 0.02f,
            Output = "steel_working",
            AnnouncementLevel = "MILESTONE",
            Effects = new() { "All gathering 4.5x", "Build speed 4x", "Construction costs -25%" },
            Description = "Controlled carbon in iron produces steel. Ultimate material."
        },

        // Era 7: Civilization — Branch 4: Shelter & Construction
        new Recipe
        {
            Id = "advanced_architecture", Name = "Advanced Architecture", Tier = 7, Branch = "Shelter",
            RequiredResources = new() { { ResourceType.Stone, 15 }, { ResourceType.Ore, 5 }, { ResourceType.Wood, 5 } },
            RequiredKnowledge = new() { "iron_working", "stone_masonry" },
            BaseChance = 0.02f,
            Output = "advanced_architecture",
            Effects = new() { "All building construction costs -25%", "Metal-reinforced structures" },
            Description = "Engineered structures using iron and steel. The pinnacle of construction."
        },

        // Era 7: Civilization — Branch 5: Knowledge & Culture
        new Recipe
        {
            Id = "governance", Name = "Governance", Tier = 7, Branch = "Knowledge",
            RequiredResources = new() { { ResourceType.Stone, 1 } },
            RequiredKnowledge = new() { "record_keeping", "communal_building" },
            BaseChance = 0.02f,
            Output = "governance",
            Effects = new() { "Collaboration bonus doubled (2% to 4% per agent)", "Build efficiency +25%" },
            Description = "Organized leadership and community decision-making."
        }
    };

    /// <summary>
    /// Returns all recipes the agent can currently attempt
    /// (has required knowledge, has required resources in inventory).
    /// Excludes innate/auto-trigger recipes (BaseChance=0), already-known recipes,
    /// and recipes the agent's settlement already knows or is propagating.
    /// </summary>
    public static List<Recipe> GetAvailableRecipes(Agent agent,
        SettlementKnowledge? knowledgeSystem = null, List<Settlement>? settlements = null)
    {
        var available = new List<Recipe>();

        // Cache settlement knowledge (completed + pending) to avoid per-recipe lookups
        HashSet<string>? settlementKnowledge = null;
        if (knowledgeSystem != null && settlements != null)
            settlementKnowledge = knowledgeSystem.GetAllKnowledgeIncludingPending(settlements, agent.X, agent.Y);

        foreach (var recipe in AllRecipes)
        {
            // Skip if agent already knows this discovery
            if (agent.Knowledge.Contains(recipe.Output))
                continue;

            // Skip recipes the settlement already knows or is currently propagating
            if (settlementKnowledge != null && settlementKnowledge.Contains(recipe.Output))
                continue;

            // Skip innate/auto-trigger recipes (clothing, oral_tradition)
            if (recipe.BaseChance <= 0f)
                continue;

            // Check knowledge prerequisites
            bool hasKnowledge = true;
            foreach (var req in recipe.RequiredKnowledge)
            {
                if (!agent.Knowledge.Contains(req))
                {
                    hasKnowledge = false;
                    break;
                }
            }
            if (!hasKnowledge) continue;

            // Check resource requirements
            bool hasResources = true;
            foreach (var kvp in recipe.RequiredResources)
            {
                int held = agent.Inventory.GetValueOrDefault(kvp.Key, 0);
                if (held < kvp.Value)
                {
                    hasResources = false;
                    break;
                }
            }
            if (!hasResources) continue;

            available.Add(recipe);
        }

        return available;
    }
}
