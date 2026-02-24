using CivSim.Core.Events;

namespace CivSim.Core;

/// <summary>
/// GDD v1.8 Testing Infrastructure — Layer 1: Run Logger.
/// Writes a structured CSV log of every agent decision, action completion, and system event
/// to CivSim.Raylib/Logs/run_{seed}_{timestamp}.csv for post-run analysis.
///
/// CSV columns (all rows, unused cells are empty):
///   Tick, SimDay, EventType, AgentID, AgentName,
///   ChosenAction, ChosenTarget, ChosenScore, RunnerUpAction, RunnerUpScore,
///   Hunger, Health, PosX, PosY, HomeTileX, HomeTileY, DistFromHome,
///   IsExposed, InventoryFood, InventoryWood, InventoryStone,
///   KnowledgeCount, DependentCount, IsNight,
///   CompletedAction, Result, Detail, Duration, SubType
/// </summary>
public sealed class RunLogger : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly int _seed;
    private int _pendingFlush;
    private bool _disposed;
    private const int FlushInterval = 100;

    // Column count sanity — must match header below
    private const int ColumnCount = 30;

    // ── Diagnostic accumulators (incremented during logging, dumped at shutdown) ──

    // Per-agent action counts: agentName → (action → count)
    private readonly Dictionary<string, Dictionary<string, int>> _actionCounts = new();
    // Per-agent mode counts: agentName → (mode → count)
    private readonly Dictionary<string, Dictionary<string, int>> _modeCounts = new();
    // Per-agent home drift: agentName → (nearHome count, farHome count, totalWithHome)
    private readonly Dictionary<string, (int Near, int Far, int Total)> _homeDrift = new();
    // Per-agent position tracking for oscillation: agentName → (prevX, prevY, prevDx, prevDy, moves, reversals)
    private readonly Dictionary<string, (int PrevX, int PrevY, int PrevDx, int PrevDy, int Moves, int Reversals)> _oscillation = new();
    // Per-agent night rest tracking: agentName → (nightDecisions, nightRestDecisions)
    private readonly Dictionary<string, (int NightDecisions, int NightRests)> _nightRest = new();
    // Per-agent mode-action mismatch count
    private readonly Dictionary<string, int> _modeActionMismatches = new();
    // Per-agent mode oscillation: agentName → (lastMode, lastModeTick, pairSwitchCounts in rolling window)
    private readonly Dictionary<string, (string LastMode, int LastTick, Dictionary<string, int> PairCounts)> _modeOscillation = new();
    // Per-agent max consecutive ticks in Urgent
    private readonly Dictionary<string, (int Current, int Max)> _urgentStreaks = new();
    // Per-agent max consecutive ticks in Forage
    private readonly Dictionary<string, (int Current, int Max)> _forageStreaks = new();
    // Tight score margins: count of decisions where |chosen - runner| < 0.05
    private int _tightMarginCount;
    private int _totalScoredDecisions;
    // Priority violations: hungry agent doing non-survival action
    private int _priorityViolations;
    // System event timeline
    private readonly List<(int Tick, string SubType, string Detail)> _systemEvents = new();
    // Total decision count
    private int _totalDecisions;

    // Allowed actions per mode for mismatch detection
    // Note: "Gather" is the logged action name (not GatherFood/GatherResource which are internal)
    private static readonly Dictionary<string, HashSet<string>> ModeAllowedActions = new()
    {
        ["Home"] = new() { "Experiment", "Socialize", "Reproduce", "DepositHome", "DepositGranary",
            "WithdrawHome", "WithdrawGranary", "Preserve", "Build", "Eat", "Rest", "Idle",
            "TendFarm", "ShareFood", "Gather", "Move", "Cook" },
        ["Forage"] = new() { "Gather", "Move", "Eat", "Rest", "Explore" },
        ["Build"] = new() { "Build", "Move", "Eat", "Rest" },
        ["Explore"] = new() { "Move", "Explore", "Gather", "Eat", "Rest" },
        ["Caretaker"] = new() { "Experiment", "Socialize", "Reproduce", "DepositHome", "DepositGranary",
            "WithdrawHome", "WithdrawGranary", "Preserve", "Build", "Eat", "Rest", "Idle",
            "TendFarm", "ShareFood", "Gather", "Move", "Cook" },
        ["Urgent"] = new() { "Eat", "Move", "Gather", "Cook",
            "WithdrawHome", "WithdrawGranary", "Rest", "Explore" },
    };

    public static readonly string Header =
        "Tick,SimDay,EventType,AgentID,AgentName," +
        "ChosenAction,ChosenTarget,ChosenScore,RunnerUpAction,RunnerUpScore," +
        "Hunger,Health,PosX,PosY,HomeTileX,HomeTileY,DistFromHome," +
        "IsExposed,InventoryFood,InventoryWood,InventoryStone," +
        "KnowledgeCount,DependentCount,IsNight,CurrentMode," +
        "CompletedAction,Result,Detail,Duration,SubType";

    public RunLogger(string filePath, int seed)
    {
        _seed = seed;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _writer = new StreamWriter(filePath, append: false) { AutoFlush = false };
        _writer.WriteLine(Header);
    }

    // ── DECISION event ───────────────────────────────────────────────────

    /// <summary>
    /// Log when an agent starts a new action (priority cascade or utility choice).
    /// For priority cascade actions (P1-P3.5), score is a fixed priority value (100, 90, etc.)
    /// and runnerUpAction is empty. For utility-scored actions, supply the actual scores.
    /// </summary>
    public void LogDecision(int tick, Agent agent, string chosenAction, string chosenTarget,
        float chosenScore, string runnerUpAction = "", float runnerUpScore = 0f)
    {
        int homeTileX = agent.HomeTile.HasValue ? agent.HomeTile.Value.X : -1;
        int homeTileY = agent.HomeTile.HasValue ? agent.HomeTile.Value.Y : -1;
        int distFromHome = agent.HomeTile.HasValue
            ? Math.Abs(agent.X - agent.HomeTile.Value.X) + Math.Abs(agent.Y - agent.HomeTile.Value.Y)
            : -1;
        int dependentCount = agent.Relationships.Values.Count(r => r == RelationshipType.Child);
        bool isNight = Agent.IsNightTime(tick);

        string modeName = agent.CurrentMode.ToString();

        WriteRow(
            tick, $"{tick / (float)SimConfig.TicksPerSimDay:F2}", "DECISION",
            agent.Id.ToString(), Escape(agent.Name),
            chosenAction, Escape(chosenTarget),
            chosenScore.ToString("F3"), runnerUpAction, runnerUpScore.ToString("F3"),
            agent.Hunger.ToString("F1"), agent.Health.ToString(),
            agent.X.ToString(), agent.Y.ToString(),
            homeTileX.ToString(), homeTileY.ToString(), distFromHome.ToString(),
            agent.IsExposed.ToString(),
            agent.FoodInInventory().ToString(),
            agent.Inventory.GetValueOrDefault(ResourceType.Wood, 0).ToString(),
            agent.Inventory.GetValueOrDefault(ResourceType.Stone, 0).ToString(),
            agent.Knowledge.Count.ToString(),
            dependentCount.ToString(),
            isNight.ToString(),
            modeName,
            /* CompletedAction */ "", /* Result */ "", /* Detail */ "",
            /* Duration */ "", /* SubType */ ""
        );

        // ── Accumulate diagnostic stats ──
        AccumulateDecisionStats(agent.Name, chosenAction, modeName, tick,
            agent.Hunger, agent.Health, isNight,
            agent.X, agent.Y, distFromHome,
            chosenScore, runnerUpScore, runnerUpAction);
    }

    // ── COMPLETE event ───────────────────────────────────────────────────

    /// <summary>
    /// Log when a multi-tick action finishes (success, failed, or interrupted).
    /// </summary>
    public void LogCompletion(int tick, Agent agent, string completedAction,
        string result, string detail, int duration)
    {
        WriteRow(
            tick, $"{tick / (float)SimConfig.TicksPerSimDay:F2}", "COMPLETE",
            agent.Id.ToString(), Escape(agent.Name),
            /* ChosenAction */ "", "", "", "", "",
            /* Hunger */ "", "", "", "", "", "", "",
            /* IsExposed */ "", "", "", "", "", "", "",
            /* CurrentMode */ "",
            completedAction, result, Escape(detail), duration.ToString(), ""
        );
    }

    // ── SYSTEM event ─────────────────────────────────────────────────────

    /// <summary>
    /// Log a simulation-level event (BIRTH, DEATH, DISCOVERY, PROPAGATION, BUILD, MILESTONE, etc.)
    /// </summary>
    public void LogSystemEvent(int tick, string subType, string detail,
        int agentId = -1, string agentName = "")
    {
        WriteRow(
            tick, $"{tick / (float)SimConfig.TicksPerSimDay:F2}", "SYSTEM",
            agentId >= 0 ? agentId.ToString() : "", Escape(agentName),
            "", "", "", "", "",
            "", "", "", "", "", "", "",
            "", "", "", "", "", "", "",
            /* CurrentMode */ "",
            "", "", Escape(detail), "", subType
        );

        _systemEvents.Add((tick, subType, detail));
    }

    // ── EventBus subscription ─────────────────────────────────────────────

    /// <summary>
    /// Subscribe to the EventBus to automatically capture BIRTH, DEATH, DISCOVERY,
    /// PROPAGATION, BUILD, and MILESTONE system events.
    /// </summary>
    public void SubscribeToEventBus(EventBus bus)
    {
        bus.Subscribe(events =>
        {
            foreach (var evt in events)
            {
                string? subType = evt.Type switch
                {
                    EventType.Birth => "BIRTH",
                    EventType.Death => "DEATH",
                    EventType.Discovery => ClassifyDiscovery(evt.Message),
                    EventType.Milestone => "MILESTONE",
                    _ => null
                };

                if (subType != null)
                    LogSystemEvent(evt.Tick, subType, evt.Message, evt.AgentId);
            }
        });
    }

    /// <summary>
    /// Distinguishes DISCOVERY (recipe found by agent) from PROPAGATION
    /// (settlement learned via oral tradition) by message content.
    /// Also catches BUILD events emitted via Discovery type.
    /// </summary>
    private static string ClassifyDiscovery(string message)
    {
        if (message.Contains("learned") || message.Contains("oral tradition") || message.Contains("propagation"))
            return "PROPAGATION";
        if (message.Contains("built") || message.Contains("constructed"))
            return "BUILD";
        return "DISCOVERY";
    }

    // ── Diagnostic accumulation ────────────────────────────────────────────

    private void AccumulateDecisionStats(string name, string action, string mode, int tick,
        float hunger, int health, bool isNight, int x, int y, int distFromHome,
        float chosenScore, float runnerUpScore, string runnerUpAction)
    {
        _totalDecisions++;

        // 1. Per-agent action distribution
        if (!_actionCounts.TryGetValue(name, out var actions))
        { actions = new Dictionary<string, int>(); _actionCounts[name] = actions; }
        actions[action] = actions.GetValueOrDefault(action) + 1;

        // 2. Per-agent mode distribution
        if (!_modeCounts.TryGetValue(name, out var modes))
        { modes = new Dictionary<string, int>(); _modeCounts[name] = modes; }
        modes[mode] = modes.GetValueOrDefault(mode) + 1;

        // 3. Home drift
        if (distFromHome >= 0)
        {
            if (!_homeDrift.TryGetValue(name, out var hd))
                hd = (0, 0, 0);
            _homeDrift[name] = (
                hd.Near + (distFromHome <= 15 ? 1 : 0),
                hd.Far + (distFromHome > 15 ? 1 : 0),
                hd.Total + 1
            );
        }

        // 4. Oscillation (direction reversals)
        if (!_oscillation.TryGetValue(name, out var osc))
        {
            _oscillation[name] = (x, y, 0, 0, 0, 0);
        }
        else
        {
            int dx = x - osc.PrevX;
            int dy = y - osc.PrevY;
            bool moved = dx != 0 || dy != 0;
            int newMoves = osc.Moves + (moved ? 1 : 0);
            int newReversals = osc.Reversals;
            if (moved && osc.PrevDx != 0 || osc.PrevDy != 0)
            {
                if (moved && dx == -osc.PrevDx && dy == -osc.PrevDy && (osc.PrevDx != 0 || osc.PrevDy != 0))
                    newReversals++;
            }
            _oscillation[name] = (x, y, moved ? dx : osc.PrevDx, moved ? dy : osc.PrevDy, newMoves, newReversals);
        }

        // 5. Night rest
        if (isNight)
        {
            if (!_nightRest.TryGetValue(name, out var nr))
                nr = (0, 0);
            _nightRest[name] = (nr.NightDecisions + 1, nr.NightRests + (action == "Rest" ? 1 : 0));
        }

        // 6. Mode-action mismatch
        if (ModeAllowedActions.TryGetValue(mode, out var allowed) && !allowed.Contains(action))
        {
            _modeActionMismatches[name] = _modeActionMismatches.GetValueOrDefault(name) + 1;
        }

        // 7. Mode oscillation (same pair switching > 5 times in 100 ticks)
        if (!_modeOscillation.TryGetValue(name, out var mo))
        {
            _modeOscillation[name] = (mode, tick, new Dictionary<string, int>());
        }
        else if (mo.LastMode != mode)
        {
            string pair = string.Compare(mo.LastMode, mode) < 0
                ? $"{mo.LastMode}<>{mode}" : $"{mode}<>{mo.LastMode}";
            mo.PairCounts[pair] = mo.PairCounts.GetValueOrDefault(pair) + 1;
            _modeOscillation[name] = (mode, tick, mo.PairCounts);
        }

        // 8. Urgent streaks
        {
            if (!_urgentStreaks.TryGetValue(name, out var us))
                us = (0, 0);
            if (mode == "Urgent")
            {
                int cur = us.Current + 1;
                _urgentStreaks[name] = (cur, Math.Max(us.Max, cur));
            }
            else
            {
                _urgentStreaks[name] = (0, us.Max);
            }
        }

        // 9. Forage streaks
        {
            if (!_forageStreaks.TryGetValue(name, out var fs))
                fs = (0, 0);
            if (mode == "Forage")
            {
                int cur = fs.Current + 1;
                _forageStreaks[name] = (cur, Math.Max(fs.Max, cur));
            }
            else
            {
                _forageStreaks[name] = (0, fs.Max);
            }
        }

        // 10. Tight score margins
        if (!string.IsNullOrEmpty(runnerUpAction))
        {
            _totalScoredDecisions++;
            if (Math.Abs(chosenScore - runnerUpScore) < 0.05f)
                _tightMarginCount++;
        }

        // 11. Priority violations: hungry agent doing non-survival action
        if (hunger < 40f && action != "Eat" && action != "GatherFood" && action != "Move"
            && action != "Cook" && action != "WithdrawHome" && action != "WithdrawGranary"
            && action != "Rest")
        {
            _priorityViolations++;
        }
    }

    private void WriteDiagnosticStats()
    {
        _writer.WriteLine();
        _writer.WriteLine("# --- DIAGNOSTIC STATS ---");
        _writer.WriteLine($"# TotalDecisions: {_totalDecisions}");
        _writer.WriteLine($"# PriorityViolations: {_priorityViolations}");
        _writer.WriteLine($"# TightScoreMargins: {_tightMarginCount}/{_totalScoredDecisions}");
        _writer.WriteLine();

        // Per-agent action distribution
        _writer.WriteLine("# == ACTION DISTRIBUTION ==");
        foreach (var (name, actions) in _actionCounts.OrderBy(kv => kv.Key))
        {
            int total = actions.Values.Sum();
            var sorted = actions.OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key}={kv.Value}({100.0 * kv.Value / total:F1}%)");
            _writer.WriteLine($"# {name}: {string.Join(", ", sorted)}");
        }
        _writer.WriteLine();

        // Per-agent mode distribution
        _writer.WriteLine("# == MODE DISTRIBUTION ==");
        foreach (var (name, modes) in _modeCounts.OrderBy(kv => kv.Key))
        {
            int total = modes.Values.Sum();
            var sorted = modes.OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key}={kv.Value}({100.0 * kv.Value / total:F1}%)");
            _writer.WriteLine($"# {name}: {string.Join(", ", sorted)}");
        }
        _writer.WriteLine();

        // Home drift
        _writer.WriteLine("# == HOME DRIFT ==");
        foreach (var (name, hd) in _homeDrift.OrderBy(kv => kv.Key))
        {
            float nearPct = hd.Total > 0 ? 100f * hd.Near / hd.Total : 0;
            string flag = nearPct < 80 ? " [RULE-H1 VIOLATION]" : "";
            _writer.WriteLine($"# {name}: {nearPct:F1}% within 15 tiles ({hd.Near}/{hd.Total}){flag}");
        }
        _writer.WriteLine();

        // Oscillation
        _writer.WriteLine("# == OSCILLATION ==");
        foreach (var (name, osc) in _oscillation.OrderBy(kv => kv.Key))
        {
            if (osc.Moves > 0)
            {
                float pct = 100f * osc.Reversals / osc.Moves;
                string flag = pct > 3 ? " [RULE-D1 FLAG]" : "";
                _writer.WriteLine($"# {name}: {pct:F1}% reversals ({osc.Reversals}/{osc.Moves} moves){flag}");
            }
            else
            {
                _writer.WriteLine($"# {name}: no moves recorded");
            }
        }
        _writer.WriteLine();

        // Night rest
        _writer.WriteLine("# == NIGHT REST ==");
        foreach (var (name, nr) in _nightRest.OrderBy(kv => kv.Key))
        {
            float pct = nr.NightDecisions > 0 ? 100f * nr.NightRests / nr.NightDecisions : 0;
            _writer.WriteLine($"# {name}: {pct:F1}% night rest ({nr.NightRests}/{nr.NightDecisions} night decisions)");
        }
        _writer.WriteLine();

        // Mode-action mismatches
        if (_modeActionMismatches.Any(kv => kv.Value > 0))
        {
            _writer.WriteLine("# == MODE-ACTION MISMATCHES ==");
            foreach (var (name, count) in _modeActionMismatches.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key))
                _writer.WriteLine($"# {name}: {count} mismatches [RULE-M1]");
            _writer.WriteLine();
        }

        // Mode oscillation
        {
            bool anyOsc = false;
            foreach (var (name, mo) in _modeOscillation.OrderBy(kv => kv.Key))
            {
                var flagged = mo.PairCounts.Where(kv => kv.Value > 5).ToList();
                if (flagged.Any())
                {
                    if (!anyOsc) { _writer.WriteLine("# == MODE OSCILLATION =="); anyOsc = true; }
                    foreach (var (pair, count) in flagged)
                        _writer.WriteLine($"# {name}: {pair} switched {count} times [RULE-M2 FLAG]");
                }
            }
            if (anyOsc) _writer.WriteLine();
        }

        // Urgent/Forage streaks
        _writer.WriteLine("# == MODE STREAKS ==");
        foreach (var (name, us) in _urgentStreaks.Where(kv => kv.Value.Max > 0).OrderBy(kv => kv.Key))
        {
            string flag = us.Max > 300 ? " [RULE-M3 FLAG]" : "";
            _writer.WriteLine($"# {name}: max Urgent streak = {us.Max}{flag}");
        }
        foreach (var (name, fs) in _forageStreaks.Where(kv => kv.Value.Max > 0).OrderBy(kv => kv.Key))
        {
            string flag = fs.Max > 250 ? " [RULE-M4 FLAG]" : "";
            _writer.WriteLine($"# {name}: max Forage streak = {fs.Max}{flag}");
        }
        _writer.WriteLine();

        // System events timeline
        _writer.WriteLine("# == SYSTEM EVENTS TIMELINE ==");
        foreach (var (tick, subType, detail) in _systemEvents)
        {
            float simDay = tick / (float)SimConfig.TicksPerSimDay;
            _writer.WriteLine($"# Tick {tick} (Day {simDay:F1}): [{subType}] {detail}");
        }
    }

    // ── Run summary ───────────────────────────────────────────────────────

    /// <summary>
    /// Append the # --- RUN SUMMARY --- block at the end of the file.
    /// Call this after the simulation ends, before disposing.
    /// </summary>
    public void WriteRunSummary(SimulationStats stats, List<Agent> agents)
    {
        Flush();

        int simDays = stats.CurrentTick / SimConfig.TicksPerSimDay;
        float simYears = SimConfig.TicksToYears(stats.CurrentTick);

        _writer.WriteLine("# --- RUN SUMMARY ---");
        _writer.WriteLine($"# Seed: {_seed}");
        _writer.WriteLine($"# Duration: {stats.CurrentTick} ticks ({simDays} sim-days, {simYears:F1} sim-years)");
        _writer.WriteLine($"# Peak Population: {stats.TotalAgents}");

        var dead = agents.Where(a => !a.IsAlive).ToList();
        if (dead.Any())
        {
            var desc = dead.Select(a =>
                $"{a.Name}, {(string.IsNullOrEmpty(a.DeathCause) ? "unknown" : a.DeathCause)}, age {Agent.FormatTicks(a.Age)}");
            _writer.WriteLine($"# Deaths: {dead.Count} ({string.Join("; ", desc)})");
        }
        else
        {
            _writer.WriteLine("# Deaths: 0");
        }

        _writer.WriteLine($"# Discoveries: {stats.TotalDiscoveries} ({string.Join(", ", stats.DiscoveredKnowledge)})");
        _writer.WriteLine("# Milestones: (see MILESTONE events in log)");
        _writer.WriteLine($"# Settlements: {stats.SettlementCount} ({string.Join(", ", stats.SettlementNames)})");

        var alive = agents.Where(a => a.IsAlive).ToList();
        var aliveDesc = alive.Select(a => $"{a.Name} age {Agent.FormatTicks(a.Age)}");
        _writer.WriteLine($"# Final Population: {alive.Count} ({string.Join(", ", aliveDesc)})");

        WriteDiagnosticStats();

        _writer.Flush();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void WriteRow(int tick, params string[] fields)
    {
        // fields already include SimDay onward (tick is first)
        _writer.Write(tick);
        _writer.Write(',');
        _writer.WriteLine(string.Join(",", fields));
        _pendingFlush++;
        if (_pendingFlush >= FlushInterval)
        {
            _writer.Flush();
            _pendingFlush = 0;
        }
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    public void Flush() => _writer.Flush();

    public void Dispose()
    {
        if (!_disposed)
        {
            _writer.Flush();
            _writer.Dispose();
            _disposed = true;
        }
    }
}
