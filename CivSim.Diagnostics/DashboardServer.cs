using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CivSim.Diagnostics;

/// <summary>
/// Lightweight HTTP server that serves the unified diagnostic dashboard SPA
/// and JSON API endpoints for browsing/comparing diagnostic runs.
/// Uses System.Net.HttpListener — zero external dependencies.
/// </summary>
public class DashboardServer
{
    private readonly string diagnosticsDir;
    private readonly int port;
    private readonly HttpListener listener;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public DashboardServer(string diagnosticsDir, int port)
    {
        this.diagnosticsDir = diagnosticsDir;
        this.port = port;
        listener = new HttpListener();
    }

    /// <summary>Starts the HTTP server and blocks until Ctrl+C.</summary>
    public void Start()
    {
        string prefix = $"http://localhost:{port}/";
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Console.WriteLine($"  Failed to start on port {port}: {ex.Message}");
            Console.WriteLine($"  Try a different port with --port <number>");
            return;
        }

        Console.WriteLine($"  Dashboard running at: {prefix}");
        Console.WriteLine($"  Watching: {diagnosticsDir}");
        Console.WriteLine();
        Console.WriteLine("  Press Ctrl+C to stop.");
        Console.WriteLine();

        // Handle Ctrl+C gracefully
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            listener.Stop();
        };

        // Request loop
        try
        {
            while (listener.IsListening && !cts.IsCancellationRequested)
            {
                var context = listener.GetContext(); // blocking
                _ = Task.Run(() => HandleRequest(context));
            }
        }
        catch (HttpListenerException) when (cts.IsCancellationRequested)
        {
            // Normal shutdown via Ctrl+C
        }

        Console.WriteLine("  Dashboard stopped.");
    }

    // ── Request Routing ─────────────────────────────────────────────────

    private void HandleRequest(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        string path = request.Url?.AbsolutePath ?? "/";

        try
        {
            response.AddHeader("Access-Control-Allow-Origin", "*");

            if (path == "/" || path == "/index.html")
                ServeIndex(response);
            else if (path == "/api/runs")
                ServeRunList(response);
            else if (path.StartsWith("/api/runs/"))
                ServeRunData(response, path["/api/runs/".Length..]);
            else
                Serve404(response);
        }
        catch (Exception ex)
        {
            try
            {
                response.StatusCode = 500;
                byte[] err = Encoding.UTF8.GetBytes($"Internal error: {ex.Message}");
                response.ContentLength64 = err.Length;
                response.OutputStream.Write(err, 0, err.Length);
            }
            catch { /* response may already be closed */ }
        }
        finally
        {
            try { response.Close(); } catch { }
        }
    }

    // ── Endpoint Handlers ───────────────────────────────────────────────

    private void ServeIndex(HttpListenerResponse response)
    {
        response.ContentType = "text/html; charset=utf-8";
        response.StatusCode = 200;
        byte[] html = Encoding.UTF8.GetBytes(DashboardFrontend.Html);
        response.ContentLength64 = html.Length;
        response.OutputStream.Write(html, 0, html.Length);
    }

    private void ServeRunList(HttpListenerResponse response)
    {
        var runs = ScanRuns();
        WriteJson(response, runs);
    }

    private void ServeRunData(HttpListenerResponse response, string runId)
    {
        // URL-decode and normalize path separators
        runId = Uri.UnescapeDataString(runId).Replace('/', Path.DirectorySeparatorChar);

        string csvPath = Path.Combine(diagnosticsDir, runId + ".csv");
        if (!File.Exists(csvPath))
        {
            Serve404(response);
            return;
        }

        string[] lines;
        try { lines = File.ReadAllLines(csvPath); }
        catch { Serve404(response); return; }

        if (lines.Length < 2) { Serve404(response); return; }

        // Parse all CSV rows
        var ticks = new List<int>();
        var population = new List<int>();
        var births = new List<int>();
        var deaths = new List<int>();
        var starvationDeaths = new List<int>();
        var oldAgeDeaths = new List<int>();
        var avgHunger = new List<float>();
        var avgHealth = new List<float>();
        var oldestAge = new List<int>();
        var totalFood = new List<int>();
        var discoveries = new List<int>();

        for (int i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].Split(',');
            if (parts.Length < 11) continue;
            try
            {
                ticks.Add(int.Parse(parts[0]));
                population.Add(int.Parse(parts[1]));
                births.Add(int.Parse(parts[2]));
                deaths.Add(int.Parse(parts[3]));
                starvationDeaths.Add(int.Parse(parts[4]));
                oldAgeDeaths.Add(int.Parse(parts[5]));
                avgHunger.Add(float.Parse(parts[6]));
                avgHealth.Add(float.Parse(parts[7]));
                oldestAge.Add(int.Parse(parts[8]));
                totalFood.Add(int.Parse(parts[9]));
                discoveries.Add(int.Parse(parts[10]));
            }
            catch { continue; } // skip malformed rows
        }

        if (ticks.Count == 0) { Serve404(response); return; }

        // Bucket births/deaths for bar chart (same logic as HtmlDashboardGenerator)
        int bucketSize = Math.Max(1, ticks.Count / 500);
        var bucketLabels = new List<string>();
        var bucketedBirths = new List<int>();
        var bucketedDeaths = new List<int>();

        for (int i = 0; i < births.Count; i += bucketSize)
        {
            int end = Math.Min(i + bucketSize, births.Count);
            int bSum = 0, dSum = 0;
            for (int j = i; j < end; j++) { bSum += births[j]; dSum += deaths[j]; }
            bucketedBirths.Add(bSum);
            bucketedDeaths.Add(dSum);
            bucketLabels.Add(bucketSize == 1 ? $"{ticks[i]}" : $"{ticks[i]}-{ticks[end - 1]}");
        }

        // Parse discovery timeline from log file
        string logPath = Path.ChangeExtension(csvPath, ".log");
        var discoveryEvents = ParseDiscoveryTimeline(logPath);

        // Parse log header for metadata
        var meta = File.Exists(logPath) ? ParseLogHeader(logPath) : null;

        var data = new RunDetail
        {
            Id = runId.Replace(Path.DirectorySeparatorChar, '/'),
            Label = meta != null ? $"Seed {meta.Seed}" : Path.GetFileNameWithoutExtension(csvPath),
            WorldSize = meta?.WorldSize ?? 0,
            StartingAgents = meta?.Agents ?? 0,
            Seed = meta?.Seed ?? 0,
            RequestedTicks = meta?.Ticks ?? 0,
            Ticks = ticks,
            Population = population,
            Births = bucketedBirths,
            Deaths = bucketedDeaths,
            BirthDeathLabels = bucketLabels,
            AvgHunger = avgHunger,
            AvgHealth = avgHealth,
            OldestAge = oldestAge,
            TotalFood = totalFood,
            Discoveries = discoveries,
            DiscoveryTimeline = discoveryEvents,
            PeakPopulation = population.Max(),
            FinalPopulation = population[^1],
            TotalBirths = births.Sum(),
            TotalDeaths = deaths.Sum(),
            StarvationDeaths = starvationDeaths.Sum(),
            OldAgeDeaths = oldAgeDeaths.Sum()
        };

        WriteJson(response, data);
    }

    private void Serve404(HttpListenerResponse response)
    {
        response.StatusCode = 404;
        byte[] msg = Encoding.UTF8.GetBytes("Not found");
        response.ContentLength64 = msg.Length;
        response.OutputStream.Write(msg, 0, msg.Length);
    }

    // ── File Scanning & Parsing ─────────────────────────────────────────

    private List<RunSummary> ScanRuns()
    {
        var runs = new List<RunSummary>();

        // Scan top-level CSVs
        if (Directory.Exists(diagnosticsDir))
        {
            foreach (var csvFile in Directory.GetFiles(diagnosticsDir, "*.csv"))
                TryAddRun(runs, csvFile, null);
        }

        // Scan batch subfolders
        if (Directory.Exists(diagnosticsDir))
        {
            foreach (var dir in Directory.GetDirectories(diagnosticsDir, "batch_*"))
            {
                string batchLabel = Path.GetFileName(dir);
                foreach (var csvFile in Directory.GetFiles(dir, "*.csv"))
                    TryAddRun(runs, csvFile, batchLabel);
            }
        }

        // Sort newest first
        runs.Sort((a, b) => b.StartedAt.CompareTo(a.StartedAt));
        return runs;
    }

    private void TryAddRun(List<RunSummary> runs, string csvPath, string? batchLabel)
    {
        try
        {
            // Generate stable ID from relative path
            string relPath = Path.GetRelativePath(diagnosticsDir, csvPath);
            string runId = relPath
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(".csv", "");

            string logPath = Path.ChangeExtension(csvPath, ".log");

            // Parse log header for metadata
            var meta = File.Exists(logPath) ? ParseLogHeader(logPath) : null;

            // Quick-scan CSV
            string[] csvLines;
            try { csvLines = File.ReadAllLines(csvPath); }
            catch { return; }

            if (csvLines.Length < 2) return;

            var firstRow = csvLines[1].Split(',');
            var lastRow = csvLines[^1].Split(',');
            if (firstRow.Length < 11 || lastRow.Length < 11) return;

            if (!int.TryParse(lastRow[1], out int finalPop)) return;
            if (!int.TryParse(lastRow[0], out int finalTick)) return;
            int totalRows = csvLines.Length - 1;

            // Scan for peak population (column index 1)
            int peakPop = 0;
            for (int i = 1; i < csvLines.Length; i++)
            {
                var parts = csvLines[i].Split(',');
                if (parts.Length >= 2 && int.TryParse(parts[1], out int pop))
                {
                    if (pop > peakPop) peakPop = pop;
                }
            }

            int.TryParse(lastRow[10], out int disc);

            runs.Add(new RunSummary
            {
                Id = runId,
                WorldSize = meta?.WorldSize ?? 0,
                StartingAgents = meta?.Agents ?? 0,
                Seed = meta?.Seed ?? 0,
                RequestedTicks = meta?.Ticks ?? 0,
                ActualTicks = finalTick,
                FinalPopulation = finalPop,
                PeakPopulation = peakPop,
                Outcome = finalPop > 0 ? "survived" : "extinct",
                StartedAt = meta?.StartedAt ?? File.GetCreationTime(csvPath),
                BatchLabel = batchLabel,
                Discoveries = disc,
                DataPoints = totalRows
            });
        }
        catch
        {
            // Skip malformed files silently
        }
    }

    /// <summary>
    /// Parses the first 6 lines of a diagnostic log file to extract run metadata.
    /// Expected format (0-indexed):
    ///   Line 3: "  World: 64x64 | Agents: 2 | Ticks: 500 | Seed: 42"
    ///   Line 5: "  Started: 2026-02-16 22:40:40"
    /// </summary>
    private LogHeaderMetadata? ParseLogHeader(string logPath)
    {
        try
        {
            using var reader = new StreamReader(logPath);
            var lines = new List<string>();
            for (int i = 0; i < 7 && reader.ReadLine() is string line; i++)
                lines.Add(line);

            if (lines.Count < 6) return null;

            var meta = new LogHeaderMetadata();

            // Parse line 3: "  World: 64x64 | Agents: 2 | Ticks: 500 | Seed: 42"
            string configLine = lines[3].Trim();
            var segments = configLine.Split('|');
            foreach (var seg in segments)
            {
                string p = seg.Trim();
                if (p.StartsWith("World:"))
                {
                    string sizeStr = p["World:".Length..].Trim().Split('x')[0];
                    if (int.TryParse(sizeStr, out int ws)) meta.WorldSize = ws;
                }
                else if (p.StartsWith("Agents:"))
                {
                    if (int.TryParse(p["Agents:".Length..].Trim(), out int a)) meta.Agents = a;
                }
                else if (p.StartsWith("Ticks:"))
                {
                    if (int.TryParse(p["Ticks:".Length..].Trim(), out int t)) meta.Ticks = t;
                }
                else if (p.StartsWith("Seed:"))
                {
                    if (int.TryParse(p["Seed:".Length..].Trim(), out int s)) meta.Seed = s;
                }
            }

            // Parse line 5: "  Started: 2026-02-16 22:40:40"
            string dateLine = lines[5].Trim();
            if (dateLine.StartsWith("Started:"))
            {
                string dateStr = dateLine["Started:".Length..].Trim();
                if (DateTime.TryParse(dateStr, out var dt)) meta.StartedAt = dt;
            }

            return meta;
        }
        catch { return null; }
    }

    /// <summary>
    /// Parses the DISCOVERY TIMELINE section from the FINAL REPORT in a log file.
    /// Lines look like: "  [     1mo] Agent 1 discovered 'Basic Tools'! (basic_tools)"
    /// </summary>
    private List<DiscoveryEvent> ParseDiscoveryTimeline(string logPath)
    {
        var events = new List<DiscoveryEvent>();
        if (!File.Exists(logPath)) return events;

        try
        {
            bool inSection = false;
            foreach (var line in File.ReadLines(logPath))
            {
                if (line.Contains("--- DISCOVERY TIMELINE ---"))
                {
                    inSection = true;
                    continue;
                }

                if (!inSection) continue;

                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("["))
                {
                    int closeBracket = trimmed.IndexOf(']');
                    if (closeBracket > 0)
                    {
                        string timeStr = trimmed[1..closeBracket].Trim();
                        string desc = trimmed[(closeBracket + 1)..].Trim();
                        events.Add(new DiscoveryEvent { Time = timeStr, Description = desc });
                    }
                }
                else if (trimmed.StartsWith("No discoveries"))
                {
                    break;
                }
                else if (trimmed.Length == 0 || trimmed.StartsWith("---") || trimmed.StartsWith("==="))
                {
                    if (events.Count > 0 || trimmed.StartsWith("---"))
                        break;
                }
            }
        }
        catch { }

        return events;
    }

    // ── JSON Helper ─────────────────────────────────────────────────────

    private void WriteJson<T>(HttpListenerResponse response, T data)
    {
        response.ContentType = "application/json; charset=utf-8";
        response.StatusCode = 200;
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(data, JsonOpts);
        response.ContentLength64 = json.Length;
        response.OutputStream.Write(json, 0, json.Length);
    }
}

// ── Data Transfer Objects ───────────────────────────────────────────────

internal class LogHeaderMetadata
{
    public int WorldSize { get; set; }
    public int Agents { get; set; }
    public int Ticks { get; set; }
    public int Seed { get; set; }
    public DateTime StartedAt { get; set; }
}

public class RunSummary
{
    public string Id { get; set; } = "";
    public int WorldSize { get; set; }
    public int StartingAgents { get; set; }
    public int Seed { get; set; }
    public int RequestedTicks { get; set; }
    public int ActualTicks { get; set; }
    public int FinalPopulation { get; set; }
    public int PeakPopulation { get; set; }
    public string Outcome { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public string? BatchLabel { get; set; }
    public int Discoveries { get; set; }
    public int DataPoints { get; set; }
}

public class RunDetail
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public int WorldSize { get; set; }
    public int StartingAgents { get; set; }
    public int Seed { get; set; }
    public int RequestedTicks { get; set; }
    public List<int> Ticks { get; set; } = new();
    public List<int> Population { get; set; } = new();
    public List<int> Births { get; set; } = new();
    public List<int> Deaths { get; set; } = new();
    public List<string> BirthDeathLabels { get; set; } = new();
    public List<float> AvgHunger { get; set; } = new();
    public List<float> AvgHealth { get; set; } = new();
    public List<int> OldestAge { get; set; } = new();
    public List<int> TotalFood { get; set; } = new();
    public List<int> Discoveries { get; set; } = new();
    public List<DiscoveryEvent> DiscoveryTimeline { get; set; } = new();
    public int PeakPopulation { get; set; }
    public int FinalPopulation { get; set; }
    public int TotalBirths { get; set; }
    public int TotalDeaths { get; set; }
    public int StarvationDeaths { get; set; }
    public int OldAgeDeaths { get; set; }
}

public class DiscoveryEvent
{
    public string Time { get; set; } = "";
    public string Description { get; set; } = "";
}
