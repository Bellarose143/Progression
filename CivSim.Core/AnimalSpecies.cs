namespace CivSim.Core;

public enum AnimalSpecies
{
    Rabbit,
    Deer,
    Cow,
    Sheep,
    Boar,
    Wolf
}

public enum AnimalState
{
    Idle,
    Grazing,
    Moving,
    Fleeing,
    Sleeping,
    Dead,
    Aggressive,  // D25c: Boar/Wolf attacking an agent
    Domesticated // D25d: Tamed animal — follows owner or stays in pen
}
