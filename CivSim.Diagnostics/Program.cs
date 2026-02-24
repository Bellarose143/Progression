using CivSim.Diagnostics;

// ── CLI Mode: if arguments provided, skip interactive prompts ──
if (args.Length > 0)
{
    int worldSize = 256, startingAgents = 2, tickCount = 17520, seed = 0;
    string verbosityStr = "full";
    string? outputPath = null;
    bool noPause = false;
    string? batchFile = null;
    bool dashboardMode = false;
    int dashboardPort = 5000;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i].ToLowerInvariant())
        {
            case "--world-size": worldSize = int.Parse(args[++i]); break;
            case "--agents": startingAgents = int.Parse(args[++i]); break;
            case "--ticks": tickCount = int.Parse(args[++i]); break;
            case "--seed": seed = int.Parse(args[++i]); break;
            case "--verbosity": verbosityStr = args[++i]; break;
            case "--output": outputPath = args[++i]; break;
            case "--no-pause": noPause = true; break;
            case "--batch": batchFile = args[++i]; break;
            case "--dashboard": dashboardMode = true; break;
            case "--port": dashboardPort = int.Parse(args[++i]); break;
            case "--help":
            case "-h":
                Console.WriteLine("CivSim Diagnostic Runner - CLI Mode");
                Console.WriteLine();
                Console.WriteLine("Usage: dotnet run --project CivSim.Diagnostics -- [options]");
                Console.WriteLine();
                Console.WriteLine("Options:");
                Console.WriteLine("  --world-size <int>   World grid size (default: 64)");
                Console.WriteLine("  --agents <int>       Starting agent count (default: 2)");
                Console.WriteLine("  --ticks <int>        Ticks to simulate (default: 17520 = 2 years)");
                Console.WriteLine("  --seed <int>         World seed, 0=random (default: 0)");
                Console.WriteLine("  --verbosity <level>  summary|full|trace (default: full)");
                Console.WriteLine("  --output <path>      Log file path (default: diagnostics/run_<timestamp>.log)");
                Console.WriteLine("  --no-pause           Skip 'press any key' at end (for scripting)");
                Console.WriteLine("  --batch <path>       JSON batch config file (runs multiple configs)");
                Console.WriteLine("  --dashboard          Start the unified dashboard web server");
                Console.WriteLine("  --port <int>         Dashboard server port (default: 5000)");
                Console.WriteLine("  --help, -h           Show this help message");
                return;
        }
    }

    // Dashboard mode — starts HTTP server, blocks until Ctrl+C
    if (dashboardMode)
    {
        string dashRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string diagnosticsDir = Path.Combine(dashRoot, "diagnostics");

        if (!Directory.Exists(diagnosticsDir))
        {
            Directory.CreateDirectory(diagnosticsDir);
        }

        Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║           CivSim Diagnostic Dashboard                   ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        var server = new DashboardServer(diagnosticsDir, dashboardPort);
        server.Start();
        return;
    }

    // Batch mode
    if (batchFile != null)
    {
        BatchRunner.Run(batchFile);
        if (!noPause)
        {
            Console.WriteLine();
            Console.WriteLine("Done. Press any key to exit.");
            Console.ReadKey(true);
        }
        return;
    }

    // Single CLI run
    if (seed == 0) seed = new Random().Next(1, 100000);

    string solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
    outputPath ??= Path.Combine(solutionRoot, "diagnostics", $"run_{timestamp}.log");

    Verbosity verbosity = verbosityStr.ToLowerInvariant() switch
    {
        "summary" => Verbosity.Summary,
        "trace" => Verbosity.Trace,
        _ => Verbosity.Full
    };

    Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
    Console.WriteLine("║           CivSim Diagnostic Runner (CLI)                ║");
    Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
    Console.WriteLine();
    Console.WriteLine($"  Config: {worldSize}x{worldSize} world, {startingAgents} agents, {tickCount} ticks, seed={seed}, verbosity={verbosity}");
    Console.WriteLine();

    var runner = new DiagnosticRunner(worldSize, startingAgents, tickCount, seed, verbosity, outputPath);
    runner.Run();

    if (!noPause)
    {
        Console.WriteLine();
        Console.WriteLine("Done. Press any key to exit.");
        Console.ReadKey(true);
    }
    return;
}

// ── Interactive Mode: original prompt-based flow ──

Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
Console.WriteLine("║           CivSim Diagnostic Runner                      ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
Console.WriteLine();

int iWorldSize = Prompt("World size", 64);
int iStartingAgents = Prompt("Starting agents", 2);
int iTickCount = Prompt("Ticks to run", 17520);
int iSeed = Prompt("World seed (0 = random)", 0);
if (iSeed == 0) iSeed = new Random().Next(1, 100000);

// Resolve log path relative to solution root (four levels up from bin/Debug/net7.0)
string iSolutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
string iTimestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
string iDefaultLogPath = Path.Combine(iSolutionRoot, "diagnostics", $"run_{iTimestamp}.log");
string iLogFile = PromptString("Log file", iDefaultLogPath);

string iVerbInput = PromptString("Verbosity (summary|full|trace)", "full").ToLowerInvariant();
Verbosity iVerbosity = iVerbInput switch
{
    "summary" => Verbosity.Summary,
    "trace" => Verbosity.Trace,
    _ => Verbosity.Full
};

Console.WriteLine();
Console.WriteLine($"  Config: {iWorldSize}x{iWorldSize} world, {iStartingAgents} agents, {iTickCount} ticks, seed={iSeed}, verbosity={iVerbosity}");
Console.WriteLine();

var iRunner = new DiagnosticRunner(iWorldSize, iStartingAgents, iTickCount, iSeed, iVerbosity, iLogFile);
iRunner.Run();

Console.WriteLine();
Console.WriteLine("Done. Press any key to exit.");
Console.ReadKey(true);

// ── Helper functions ──

static int Prompt(string label, int defaultValue)
{
    Console.Write($"  {label} [{defaultValue}]: ");
    string? input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input))
        return defaultValue;
    return int.TryParse(input, out int result) ? result : defaultValue;
}

static string PromptString(string label, string defaultValue)
{
    Console.Write($"  {label} [{defaultValue}]: ");
    string? input = Console.ReadLine();
    return string.IsNullOrWhiteSpace(input) ? defaultValue : input.Trim();
}
