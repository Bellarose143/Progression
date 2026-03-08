namespace CivSim.Core;

/// <summary>
/// GDD v1.7.1: Represents a detected settlement — a cluster of 3+ shelters within SettlementRadius.
/// Settlements are observation-only (no AI behavior change). Updated periodically by SettlementDetector.
/// </summary>
public class Settlement
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int CenterX { get; set; }
    public int CenterY { get; set; }
    public int ShelterCount { get; set; }
    public List<int> ResidentAgentIds { get; set; } = new();
    public int FoundedTick { get; set; }

    public override string ToString()
    {
        return $"{Name} at ({CenterX},{CenterY}) - {ShelterCount} shelters, {ResidentAgentIds.Count} residents";
    }
}
