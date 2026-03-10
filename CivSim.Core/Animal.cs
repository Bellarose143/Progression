namespace CivSim.Core;

public class Animal
{
    private static int _nextId = 1;
    public static void ResetIdCounter() => _nextId = 1;

    public int Id { get; }
    public AnimalSpecies Species { get; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Health { get; set; }
    public int MaxHealth { get; }
    public AnimalState State { get; set; } = AnimalState.Idle;
    public bool IsAlive => State != AnimalState.Dead && Health > 0;
    public int HerdId { get; set; }
    public (int X, int Y) TerritoryCenter { get; set; }
    public int TerritoryRadius { get; }
    public int DetectionRange { get; }
    public int MoveSpeed { get; }  // ticks between moves
    public int TicksSinceLastMove { get; set; }
    public (int X, int Y)? FleeTarget { get; set; }
    public int TicksInState { get; set; }
    public (int Dx, int Dy) FacingDirection { get; set; } = (0, 1); // default facing south
    public int MeatYield { get; }
    public int FleeCooldown { get; set; } // ticks remaining before animal can flee again
    public int? AggressiveTargetAgentId { get; set; }  // D25c: agent being attacked
    public int AggressiveCooldown { get; set; }  // D25c: ticks before can re-aggro after disengage

    // D25d: Domestication fields
    public int TameProgress { get; set; }           // Food offerings received — 0 until taming starts
    public int? TameTargetAgentId { get; set; }     // Which agent is currently taming this animal
    public int LastTameOfferingTick { get; set; }   // Tick of last food offering (for decay)
    public bool IsDomesticated { get; set; }        // True when taming completes
    public int? OwnerAgentId { get; set; }          // Agent who tamed this animal
    public int? PenId { get; set; }                 // Which pen this animal lives in (null = following or wild)
    public bool IsPup { get; set; }                 // Wolf pup flag — required for wolf taming
    public bool IsDog { get; set; }                 // True when wolf pup fully tamed → Dog

    // D26: Move interpolation metadata (renderer-only, no sim behavior change)
    public int MoveStartTick { get; set; }
    public int MoveEndTick { get; set; }
    public (int X, int Y) MoveOrigin { get; set; }
    public (int X, int Y) MoveDestination { get; set; }
    public bool IsMoving { get; set; }

    /// <summary>D26: Set move interpolation metadata for smooth rendering.</summary>
    public void SetMoveInterpolation(int oldX, int oldY, int currentTick)
    {
        MoveOrigin = (oldX, oldY);
        MoveDestination = (X, Y);
        MoveStartTick = currentTick;
        MoveEndTick = currentTick + MoveSpeed;
        IsMoving = true;
    }

    // Species configuration (static readonly)
    public static readonly Dictionary<AnimalSpecies, AnimalSpeciesConfig> SpeciesConfig = new()
    {
        // 350×350 scale: Territory radii and detection ranges recalibrated (~15ft/tile).
        // Old 64×64 values: Rabbit(3,3), Deer(5,5), Cow(4,4), Sheep(4,3), Boar(3,4), Wolf(4,6).
        [AnimalSpecies.Rabbit]   = new(MaxHealth: 5,  MoveSpeed: 1, DetectionRange: 6,  TerritoryRadius: 8,  HerdSizeMin: 2, HerdSizeMax: 4, MeatYield: 1, HideYield: 0, BoneYield: 0, FleeBehavior: FleeBehavior.Flee),
        [AnimalSpecies.Deer]     = new(MaxHealth: 20, MoveSpeed: 2, DetectionRange: 15, TerritoryRadius: 15, HerdSizeMin: 3, HerdSizeMax: 6, MeatYield: 3, HideYield: 2, BoneYield: 1, FleeBehavior: FleeBehavior.Flee),
        [AnimalSpecies.Cow]      = new(MaxHealth: 30, MoveSpeed: 3, DetectionRange: 10, TerritoryRadius: 12, HerdSizeMin: 3, HerdSizeMax: 4, MeatYield: 5, HideYield: 3, BoneYield: 2, FleeBehavior: FleeBehavior.FleeSlow),
        [AnimalSpecies.Sheep]    = new(MaxHealth: 15, MoveSpeed: 2, DetectionRange: 12, TerritoryRadius: 10, HerdSizeMin: 4, HerdSizeMax: 6, MeatYield: 2, HideYield: 1, BoneYield: 1, FleeBehavior: FleeBehavior.Flee),
        [AnimalSpecies.Boar]     = new(MaxHealth: 35, MoveSpeed: 3, DetectionRange: 3,  TerritoryRadius: 12, HerdSizeMin: 2, HerdSizeMax: 3, MeatYield: 4, HideYield: 1, BoneYield: 2, FleeBehavior: FleeBehavior.Idle),
        [AnimalSpecies.Wolf]     = new(MaxHealth: 25, MoveSpeed: 2, DetectionRange: 4,  TerritoryRadius: 25, HerdSizeMin: 2, HerdSizeMax: 4, MeatYield: 0, HideYield: 2, BoneYield: 1, FleeBehavior: FleeBehavior.Flee),
    };

    public Animal(AnimalSpecies species, int x, int y, int herdId, (int X, int Y) territoryCenter)
    {
        var config = SpeciesConfig[species];
        Id = _nextId++;
        Species = species;
        X = x;
        Y = y;
        Health = config.MaxHealth;
        MaxHealth = config.MaxHealth;
        HerdId = herdId;
        TerritoryCenter = territoryCenter;
        TerritoryRadius = config.TerritoryRadius;
        DetectionRange = config.DetectionRange;
        MoveSpeed = config.MoveSpeed;
        MeatYield = config.MeatYield;
    }

    public void Die()
    {
        State = AnimalState.Dead;
        Health = 0;
    }
}

public enum FleeBehavior { Idle, Flee, FleeSlow }

public record AnimalSpeciesConfig(
    int MaxHealth, int MoveSpeed, int DetectionRange, int TerritoryRadius,
    int HerdSizeMin, int HerdSizeMax, int MeatYield, int HideYield, int BoneYield, FleeBehavior FleeBehavior
);
