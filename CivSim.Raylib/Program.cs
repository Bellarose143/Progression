using Raylib_cs;
using CivSim.Core;
using CivSim.Raylib;

// CivSim - Raylib Visualization (GDD v1.8 Observation Layer)

const int screenWidth = 1600;
const int screenHeight = 900;
// v1.8 Corrections: World is now 256×256 (was 64×64). Uses SimConfig defaults.
const int initialAgents = 2;
const int tileSize = 64;

Console.WriteLine("=== CivSim Raylib - Initializing ===\n");

// Parse optional --seed argument
int seed;
int seedArgIndex = Array.IndexOf(args, "--seed");
if (seedArgIndex >= 0 && seedArgIndex + 1 < args.Length && int.TryParse(args[seedArgIndex + 1], out int parsedSeed))
{
    seed = parsedSeed;
    Console.WriteLine($"Using user-specified seed: {seed}");
}
else if (args.Length > 0 && int.TryParse(args[0], out int positionalSeed))
{
    seed = positionalSeed;
    Console.WriteLine($"Using user-specified seed: {seed}");
}
else
{
    seed = (int)DateTime.Now.Ticks;
}
Console.WriteLine($"Generating {SimConfig.DefaultGridWidth}x{SimConfig.DefaultGridHeight} world with seed: {seed}");
var world = new World(seed);
Console.WriteLine("World generation complete!");

// Create simulation
var simulation = new Simulation(world, seed);
Console.WriteLine($"Spawning {initialAgents} agents...");
for (int i = 0; i < initialAgents; i++)
{
    simulation.SpawnAgent();
}

// GDD v1.8 Testing Infrastructure: Run logger
string logsDir = Path.Combine(AppContext.BaseDirectory, "Logs");
string logTimestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
string logPath = Path.Combine(logsDir, $"run_{seed}_{logTimestamp}.csv");
var runLogger = new CivSim.Core.RunLogger(logPath, seed);
simulation.RunLogger = runLogger;
runLogger.SetAgents(simulation.Agents);
runLogger.SubscribeToEventBus(simulation.EventBus);
Console.WriteLine($"Run logger: {logPath}");

// Initialize Raylib — Resizable window
Raylib_cs.Raylib.SetConfigFlags(ConfigFlags.ResizableWindow);
Raylib_cs.Raylib.InitWindow(screenWidth, screenHeight, "CivSim - Emergent Civilization Simulator");
Raylib_cs.Raylib.SetTargetFPS(60);

using var renderer = new RaylibRenderer(world, tileSize, screenWidth, screenHeight, simulation.SpawnCenter);

Console.WriteLine("Raylib initialized. Starting simulation...\n");

// Simulation state
bool isPaused = false;
int speedTier = SimConfig.DefaultSpeedTier; // GDD v1.8: 5-tier speed system
float ticksPerSecond = SimConfig.SpeedTierTicksPerSecond[speedTier]; // v1.8 Corrections: float (0.8 at 1x)
double timeSinceLastTick = 0;

// Main game loop
while (!Raylib_cs.Raylib.WindowShouldClose())
{
    float deltaTime = Raylib_cs.Raylib.GetFrameTime();

    // Input: Renderer handles camera, selection, display toggles
    renderer.HandleInput(simulation.Agents, simulation.SpawnCenter, simulation.CurrentTick);

    // Input: Program handles pause and speed
    if (Raylib_cs.Raylib.IsKeyPressed(KeyboardKey.P) || Raylib_cs.Raylib.IsKeyPressed(KeyboardKey.Space))
    {
        isPaused = !isPaused;
    }

    // GDD v1.8: 5-tier speed presets (keys 1-5)
    if (Raylib_cs.Raylib.IsKeyPressed(KeyboardKey.One))   { speedTier = 0; ticksPerSecond = SimConfig.SpeedTierTicksPerSecond[0]; }
    if (Raylib_cs.Raylib.IsKeyPressed(KeyboardKey.Two))   { speedTier = 1; ticksPerSecond = SimConfig.SpeedTierTicksPerSecond[1]; }
    if (Raylib_cs.Raylib.IsKeyPressed(KeyboardKey.Three)) { speedTier = 2; ticksPerSecond = SimConfig.SpeedTierTicksPerSecond[2]; }
    if (Raylib_cs.Raylib.IsKeyPressed(KeyboardKey.Four))  { speedTier = 3; ticksPerSecond = SimConfig.SpeedTierTicksPerSecond[3]; }
    if (Raylib_cs.Raylib.IsKeyPressed(KeyboardKey.Five))  { speedTier = 4; ticksPerSecond = SimConfig.SpeedTierTicksPerSecond[4]; }

    // Fine speed adjustment (still available)
    if (Raylib_cs.Raylib.IsKeyPressed(KeyboardKey.Equal) || Raylib_cs.Raylib.IsKeyPressed(KeyboardKey.KpAdd))
        ticksPerSecond = Math.Min(200f, ticksPerSecond + 0.5f);
    if (Raylib_cs.Raylib.IsKeyPressed(KeyboardKey.Minus) || Raylib_cs.Raylib.IsKeyPressed(KeyboardKey.KpSubtract))
        ticksPerSecond = Math.Max(0.1f, ticksPerSecond - 0.5f);

    // Update simulation
    if (!isPaused)
    {
        timeSinceLastTick += deltaTime;
        double tickInterval = 1.0 / ticksPerSecond;

        while (timeSinceLastTick >= tickInterval)
        {
            simulation.Tick();
            timeSinceLastTick -= tickInterval;

            // Process events for notifications/effects
            var tickEvents = simulation.Logger.GetEventsForTick(simulation.CurrentTick);
            if (tickEvents.Count > 0)
                renderer.ProcessTickEvents(tickEvents, simulation.Agents);

            // Auto-pause on extinction
            if (simulation.Agents.All(a => !a.IsAlive))
            {
                isPaused = true;
                break;
            }
        }
    }

    // Compute interpolation alpha for smooth agent movement
    {
        double tickInterval = 1.0 / ticksPerSecond;
        renderer.LerpAlpha = (tickInterval > 0 && !isPaused)
            ? Math.Clamp((float)(timeSinceLastTick / tickInterval), 0f, 1f)
            : 1.0f;
    }

    // Render
    Raylib_cs.Raylib.BeginDrawing();
    Raylib_cs.Raylib.ClearBackground(Color.Black);

    var stats = simulation.GetStats();
    var recentEvents = simulation.Logger.GetRecentEvents(30);

    renderer.Render(simulation.Agents, stats, recentEvents,
                    ticksPerSecond, isPaused, deltaTime,
                    simulation.PeakPopulation, simulation.Settlements);

    Raylib_cs.Raylib.EndDrawing();
}

// Cleanup
Raylib_cs.Raylib.CloseWindow();

// Finalize run log
var finalStats = simulation.GetStats();
runLogger.WriteRunSummary(finalStats, simulation.Agents);
runLogger.Dispose();
Console.WriteLine($"\nRun log saved: {logPath}");

// Write run summary report
string diagnosticsDir = Path.Combine(AppContext.BaseDirectory, "diagnostics");
string summaryPath = Path.Combine(diagnosticsDir, $"run_summary_seed_{seed}.txt");
CivSim.Core.RunSummaryWriter.Write(simulation, summaryPath);
Console.WriteLine($"Run summary saved: {summaryPath}");

Console.WriteLine("\n=== Final Statistics ===");
Console.WriteLine(finalStats);
Console.WriteLine("\nSimulation ended.");
