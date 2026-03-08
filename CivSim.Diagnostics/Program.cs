using CivSim.Diagnostics;

// Special diagnostic modes (checked first)
if (args.Any(a => a.Equals("--death-traces", StringComparison.OrdinalIgnoreCase)))
{
    DeathTraceInvestigation.Run();
    if (!args.Any(a => a.Equals("--no-pause", StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine("Done. Press any key...");
        Console.ReadKey(true);
    }
    return;
}

if (args.Any(a => a.Equals("--pen-feeding", StringComparison.OrdinalIgnoreCase)))
{
    int[] seeds = { 42, 1337, 16001, 55555, 99999 };
    int penTicks = 150000;
    // Check for custom ticks
    for (int i = 0; i < args.Length; i++)
        if (args[i].Equals("--ticks", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            penTicks = int.Parse(args[i + 1]);
    foreach (var s in seeds)
        PenFeedingDiagnostic.Run(s, penTicks);
    if (!args.Any(a => a.Equals("--no-pause", StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine("Done. Press any key...");
        Console.ReadKey(true);
    }
    return;
}

if (args.Any(a => a.Equals("--d25d-validation", StringComparison.OrdinalIgnoreCase)))
{
    D25dFinalValidation.Run();
    if (!args.Any(a => a.Equals("--no-pause", StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine("Done. Press any key...");
        Console.ReadKey(true);
    }
    return;
}

// CLI Mode
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
                Console.WriteLine("Usage: dotnet run --project CivSim.Diagnostics -- [options]");
                return;
        }
    }

    if (dashboardMode)
    {
        string dashRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string diagnosticsDir = Path.Combine(dashRoot, "diagnostics");
        if (!Directory.Exists(diagnosticsDir)) Directory.CreateDirectory(diagnosticsDir);
        var server = new DashboardServer(diagnosticsDir, dashboardPort);
        server.Start();
        return;
    }

    if (batchFile != null)
    {
        BatchRunner.Run(batchFile);
        if (!noPause) { Console.WriteLine("Done."); Console.ReadKey(true); }
        return;
    }

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
    var runner = new DiagnosticRunner(worldSize, startingAgents, tickCount, seed, verbosity, outputPath);
    runner.Run();
    if (!noPause) { Console.WriteLine("Done."); Console.ReadKey(true); }
    return;
}

// Interactive Mode
Console.WriteLine("CivSim Diagnostic Runner");
int iWorldSize = Prompt("World size", 64);
int iStartingAgents = Prompt("Starting agents", 2);
int iTickCount = Prompt("Ticks to run", 17520);
int iSeed = Prompt("World seed (0 = random)", 0);
if (iSeed == 0) iSeed = new Random().Next(1, 100000);
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
var iRunner = new DiagnosticRunner(iWorldSize, iStartingAgents, iTickCount, iSeed, iVerbosity, iLogFile);
iRunner.Run();
Console.WriteLine("Done.");
Console.ReadKey(true);

static int Prompt(string label, int defaultValue)
{
    Console.Write($"  {label} [{defaultValue}]: ");
    string? input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input)) return defaultValue;
    return int.TryParse(input, out int result) ? result : defaultValue;
}

static string PromptString(string label, string defaultValue)
{
    Console.Write($"  {label} [{defaultValue}]: ");
    string? input = Console.ReadLine();
    return string.IsNullOrWhiteSpace(input) ? defaultValue : input.Trim();
}
