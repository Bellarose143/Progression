namespace CivSim.Core;

public class Trap
{
    public int X { get; set; }
    public int Y { get; set; }
    public int PlacedByAgentId { get; set; }
    public int TickPlaced { get; set; }
    public bool IsActive { get; set; } = true;
    /// <summary>Carcass left by a catch — null until something is caught.</summary>
    public Carcass? CaughtCarcass { get; set; }
}
