using System.Text.Json;

namespace CivSim.Diagnostics;

/// <summary>
/// Reads a JSON batch configuration file and runs multiple diagnostic instances sequentially.
/// Each run gets its own log/csv/html in a timestamped batch folder.
/// After all runs, generates a comparison HTML dashboard.
/// </summary>
public static class BatchRunner
{
    public static void Run(string configPath)
    {
        if (!File.Exists(configPath))
        {
            Console.WriteLine($"Batch config not found: {configPath}");
            return;
        }

        string json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<BatchConfig>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (config?.Runs == null || config.Runs.Count == 0)
        {
            Console.WriteLine("No runs defined in batch config.");
            return;
        }

        string solutionRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string batchDir = Path.Combine(solutionRoot, "diagnostics", $"batch_{timestamp}");
        Directory.CreateDirectory(batchDir);

        Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║           CivSim Batch Diagnostic Runner                ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine($"  Runs: {config.Runs.Count} | Output: {batchDir}");
        Console.WriteLine();

        int runIndex = 0;
        foreach (var run in config.Runs)
        {
            runIndex++;
            string label = run.Label ?? $"run_{run.Seed}";
            // Sanitize label for filename
            string safeLabel = string.Join("_", label.Split(Path.GetInvalidFileNameChars()));
            string logPath = Path.Combine(batchDir, $"{safeLabel}.log");

            int seed = run.Seed == 0 ? new Random().Next(1, 100000) : run.Seed;
            int worldSize = run.WorldSize > 0 ? run.WorldSize : 256;
            int agents = run.Agents > 0 ? run.Agents : 2;
            int ticks = run.Ticks > 0 ? run.Ticks : 17520;

            Verbosity verbosity = (run.Verbosity?.ToLowerInvariant()) switch
            {
                "summary" => Verbosity.Summary,
                "trace" => Verbosity.Trace,
                _ => Verbosity.Full
            };

            Console.WriteLine($"═══ Run {runIndex}/{config.Runs.Count}: {label} ═══");
            Console.WriteLine($"  {worldSize}x{worldSize} world, {agents} agents, {ticks} ticks, seed={seed}");
            Console.WriteLine();

            var runner = new DiagnosticRunner(worldSize, agents, ticks, seed, verbosity, logPath);
            runner.Run();

            Console.WriteLine();
        }

        // Generate comparison dashboard if multiple runs
        var csvFiles = Directory.GetFiles(batchDir, "*.csv");
        if (csvFiles.Length > 1)
        {
            string comparisonPath = Path.Combine(batchDir, "comparison.html");
            HtmlDashboardGenerator.GenerateComparison(csvFiles, comparisonPath);
            Console.WriteLine($"Comparison dashboard: {Path.GetFullPath(comparisonPath)}");
        }

        Console.WriteLine();
        Console.WriteLine($"Batch complete. {runIndex} runs finished.");
        Console.WriteLine($"Output folder: {Path.GetFullPath(batchDir)}");
    }
}

public class BatchConfig
{
    public List<RunConfig> Runs { get; set; } = new();
}

public class RunConfig
{
    public string? Label { get; set; }
    public int WorldSize { get; set; }
    public int Agents { get; set; }
    public int Ticks { get; set; }
    public int Seed { get; set; }
    public string? Verbosity { get; set; }
}
