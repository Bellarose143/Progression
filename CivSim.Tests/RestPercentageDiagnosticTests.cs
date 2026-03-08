using CivSim.Core;
using Xunit;
using Xunit.Abstractions;

namespace CivSim.Tests;

/// <summary>
/// Diagnostic investigation: Rest percentage — night duration vs rest duration.
/// Tracks exactly when rest starts/ends, how many ticks are spent resting,
/// and whether rest extends past dawn.
/// </summary>
public class RestPercentageDiagnosticTests
{
    private readonly ITestOutputHelper _output;

    public RestPercentageDiagnosticTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Diagnose_Rest_Percentage_Over_3_Days()
    {
        var sim = new TestSimBuilder()
            .GridSize(32, 32).Seed(1)
            .AddAgent("Alice", isMale: false, hunger: 90f)
            .AgentAt("Alice", 0, 0)
            .AgentHome("Alice", 0, 0)
            .ShelterAt(0, 0)
            .HomeStorageAt(0, 0, ResourceType.Berries, 100) // Plenty of food
            .ResourceAt(1, 0, ResourceType.Berries, 50)
            .ResourceAt(0, 1, ResourceType.Berries, 50)
            .Build();

        int totalTicks = SimConfig.TicksPerSimDay * 3; // 1440 ticks = 3 days
        int restTicks = 0;
        int idleTicks = 0;
        int moveTicks = 0;
        int gatherTicks = 0;
        int eatTicks = 0;
        int otherTicks = 0;

        // Track rest sessions
        bool wasResting = false;
        int restSessionStart = -1;
        int restSessionCount = 0;
        int restEndedPastDawn = 0;
        int nightTicksTotal = 0;

        _output.WriteLine($"=== SimConfig ===");
        _output.WriteLine($"TicksPerSimDay = {SimConfig.TicksPerSimDay}");
        _output.WriteLine($"NightStartHour = {SimConfig.NightStartHour}");
        _output.WriteLine($"NightEndHour = {SimConfig.NightEndHour}");
        _output.WriteLine($"NightRestDuration = {SimConfig.NightRestDuration}");
        _output.WriteLine($"RestDuration = {SimConfig.RestDuration}");
        _output.WriteLine($"Night window = ticks {SimConfig.NightStartHour}-{SimConfig.TicksPerSimDay} + 0-{SimConfig.NightEndHour} = {(SimConfig.TicksPerSimDay - SimConfig.NightStartHour) + SimConfig.NightEndHour} ticks out of {SimConfig.TicksPerSimDay}");
        _output.WriteLine($"Night percentage = {100.0 * ((SimConfig.TicksPerSimDay - SimConfig.NightStartHour) + SimConfig.NightEndHour) / SimConfig.TicksPerSimDay:F1}%");
        _output.WriteLine("");

        for (int t = 0; t < totalTicks; t++)
        {
            sim.Tick(1);
            var alice = sim.GetAgent("Alice");
            if (!alice.IsAlive)
            {
                _output.WriteLine($"Alice died at tick {sim.Simulation.CurrentTick}!");
                break;
            }

            int currentTick = sim.Simulation.CurrentTick;
            int hourOfDay = currentTick % SimConfig.TicksPerSimDay;
            bool isNight = Agent.IsNightTime(currentTick);

            if (isNight) nightTicksTotal++;

            bool isResting = alice.CurrentAction == ActionType.Rest;

            // Track rest session start
            if (isResting && !wasResting)
            {
                restSessionCount++;
                restSessionStart = currentTick;
                int day = currentTick / SimConfig.TicksPerSimDay;
                _output.WriteLine($"  REST START: tick={currentTick}, hourOfDay={hourOfDay}, day={day}, night={isNight}, duration={alice.ActionDurationTicks}, mode={alice.CurrentMode}");
            }

            // Track rest session end
            if (!isResting && wasResting)
            {
                int restEndTick = currentTick;
                int restEndHourOfDay = restEndTick % SimConfig.TicksPerSimDay;
                bool endedDuringNight = Agent.IsNightTime(restEndTick);
                bool endedPastDawn = !endedDuringNight && restEndHourOfDay > SimConfig.NightEndHour + 10;
                // Check: did rest end past dawn by more than 10 ticks?
                // Dawn is at tick 120. If rest ends at 130+, that's past dawn.
                if (endedPastDawn) restEndedPastDawn++;

                int sessionLength = restEndTick - restSessionStart;
                int day = restEndTick / SimConfig.TicksPerSimDay;
                _output.WriteLine($"  REST END:   tick={restEndTick}, hourOfDay={restEndHourOfDay}, day={day}, night={endedDuringNight}, sessionLength={sessionLength}, pastDawn={endedPastDawn}");
            }

            // Count actions
            switch (alice.CurrentAction)
            {
                case ActionType.Rest: restTicks++; break;
                case ActionType.Idle: idleTicks++; break;
                case ActionType.Move: moveTicks++; break;
                case ActionType.Gather: gatherTicks++; break;
                case ActionType.Eat: eatTicks++; break;
                default: otherTicks++; break;
            }

            wasResting = isResting;
        }

        double restPct = 100.0 * restTicks / totalTicks;
        double nightPct = 100.0 * nightTicksTotal / totalTicks;
        double idlePct = 100.0 * idleTicks / totalTicks;

        _output.WriteLine("");
        _output.WriteLine($"=== Results over {totalTicks} ticks ({totalTicks / SimConfig.TicksPerSimDay} days) ===");
        _output.WriteLine($"Rest ticks:    {restTicks} ({restPct:F1}%)");
        _output.WriteLine($"Night ticks:   {nightTicksTotal} ({nightPct:F1}%)");
        _output.WriteLine($"Idle ticks:    {idleTicks} ({idlePct:F1}%)");
        _output.WriteLine($"Move ticks:    {moveTicks} ({100.0 * moveTicks / totalTicks:F1}%)");
        _output.WriteLine($"Gather ticks:  {gatherTicks} ({100.0 * gatherTicks / totalTicks:F1}%)");
        _output.WriteLine($"Eat ticks:     {eatTicks} ({100.0 * eatTicks / totalTicks:F1}%)");
        _output.WriteLine($"Other ticks:   {otherTicks} ({100.0 * otherTicks / totalTicks:F1}%)");
        _output.WriteLine($"");
        _output.WriteLine($"Rest sessions: {restSessionCount}");
        _output.WriteLine($"Rest sessions that ended >10 ticks past dawn: {restEndedPastDawn}");
        _output.WriteLine($"");
        _output.WriteLine($"=== Conclusion ===");

        if (restEndedPastDawn > 0)
            _output.WriteLine($"BUG: {restEndedPastDawn} rest sessions extended past dawn by >10 ticks!");
        else
            _output.WriteLine($"No rest sessions extended past dawn. Rest timing is correct.");

        double nightPctExpected = 100.0 * ((SimConfig.TicksPerSimDay - SimConfig.NightStartHour) + SimConfig.NightEndHour) / SimConfig.TicksPerSimDay;
        if (restPct > 38)
            _output.WriteLine($"WARNING: Rest% ({restPct:F1}%) exceeds 38%, higher than expected for {nightPctExpected:F0}% night.");
        else
            _output.WriteLine($"Rest% ({restPct:F1}%) is within expected range for a {nightPctExpected:F0}% night cycle.");

        // Assertions
        Assert.True(restEndedPastDawn == 0,
            $"Rest should not extend past dawn. {restEndedPastDawn} sessions ended >10 ticks past dawn.");
        Assert.True(restPct <= 38,
            $"Rest% should be <=38% for {nightPctExpected:F0}% night. Got {restPct:F1}%");
    }

    [Fact]
    public void Verify_CalculateRemainingNightTicks_Math()
    {
        // Verify the math for CalculateRemainingNightTicks at various points in the night
        _output.WriteLine("=== CalculateRemainingNightTicks Verification ===");

        int nightStart = SimConfig.NightStartHour; // 420
        int nightEnd = SimConfig.NightEndHour;     // 100
        int dayLen = SimConfig.TicksPerSimDay;     // 480
        int totalNight = (dayLen - nightStart) + nightEnd; // 160

        // Test from start of evening
        int remainingAtStart = CalcRemaining(nightStart);
        _output.WriteLine($"At hourOfDay={nightStart} (night start): remaining={remainingAtStart}, expected={totalNight}");
        Assert.Equal(totalNight, remainingAtStart);

        // Test from middle of evening
        int midEvening = nightStart + (dayLen - nightStart) / 2;
        int expectedMid = (dayLen - midEvening) + nightEnd;
        int remainingMid = CalcRemaining(midEvening);
        _output.WriteLine($"At hourOfDay={midEvening} (mid evening): remaining={remainingMid}, expected={expectedMid}");
        Assert.Equal(expectedMid, remainingMid);

        // Test from midnight (480 = 0)
        int remaining0 = CalcRemaining(0);
        _output.WriteLine($"At hourOfDay=0   (midnight):     remaining={remaining0}, expected={nightEnd}");
        Assert.Equal(nightEnd, remaining0);

        // Test from middle of morning
        int midMorning = nightEnd / 2;
        int remaining50 = CalcRemaining(midMorning);
        _output.WriteLine($"At hourOfDay={midMorning} (mid morning): remaining={remaining50}, expected={nightEnd - midMorning}");
        Assert.Equal(nightEnd - midMorning, remaining50);

        // Test from just before dawn
        int justBeforeDawn = nightEnd - 1;
        int remainingJBD = CalcRemaining(justBeforeDawn);
        _output.WriteLine($"At hourOfDay={justBeforeDawn} (just before dawn): remaining={remainingJBD}, expected=1");
        Assert.Equal(1, remainingJBD);

        // Test from daytime (200) -- not night
        int remaining200 = CalcRemaining(200);
        _output.WriteLine($"At hourOfDay=200 (daytime):      remaining={remaining200}, expected=0");
        Assert.Equal(0, remaining200);

        // Verify that rest starting at X ends at dawn
        _output.WriteLine("");
        _output.WriteLine("=== Rest End Time Verification ===");
        var nightStartsList = new List<int>();
        for (int h = nightStart; h < dayLen; h += 20) nightStartsList.Add(h);
        for (int h = 0; h < nightEnd; h += 20) nightStartsList.Add(h);
        int[] nightStarts = nightStartsList.ToArray();
        foreach (int startHour in nightStarts)
        {
            int remaining = CalcRemaining(startHour);
            int endHour = (startHour + remaining) % SimConfig.TicksPerSimDay;
            _output.WriteLine($"Rest starting at hourOfDay={startHour,3}: duration={remaining,3}, ends at hourOfDay={endHour} (dawn={SimConfig.NightEndHour})");
            Assert.Equal(SimConfig.NightEndHour, endHour);
        }
    }

    [Fact]
    public void Verify_IsNightTime_Boundary()
    {
        _output.WriteLine("=== IsNightTime Boundary Verification ===");

        int dawnTick = SimConfig.NightEndHour;   // 100
        int duskTick = SimConfig.NightStartHour; // 420

        // Check boundaries around dawn
        for (int hour = dawnTick - 5; hour <= dawnTick + 5; hour++)
        {
            bool isNight = Agent.IsNightTime(hour);
            _output.WriteLine($"hourOfDay={hour}: isNight={isNight}");
        }

        _output.WriteLine("---");
        for (int hour = duskTick - 5; hour <= duskTick + 5; hour++)
        {
            bool isNight = Agent.IsNightTime(hour);
            _output.WriteLine($"hourOfDay={hour}: isNight={isNight}");
        }

        Assert.False(Agent.IsNightTime(dawnTick), $"Tick {dawnTick} (dawn) should NOT be night");
        Assert.True(Agent.IsNightTime(dawnTick - 1), $"Tick {dawnTick - 1} (just before dawn) should be night");
        Assert.True(Agent.IsNightTime(duskTick), $"Tick {duskTick} (dusk) should be night");
        Assert.False(Agent.IsNightTime(duskTick - 1), $"Tick {duskTick - 1} (just before dusk) should NOT be night");
    }

    /// <summary>
    /// Replicate the private CalculateRemainingNightTicks logic for testing.
    /// </summary>
    private static int CalcRemaining(int hourOfDay)
    {
        if (hourOfDay >= SimConfig.NightStartHour) // 360-480 evening portion
            return (SimConfig.TicksPerSimDay - hourOfDay) + SimConfig.NightEndHour;
        else if (hourOfDay < SimConfig.NightEndHour) // 0-120 morning portion
            return SimConfig.NightEndHour - hourOfDay;
        else
            return 0; // Not night
    }
}
