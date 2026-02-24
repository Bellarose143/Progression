using System.Text;
using System.Text.Json;
using CivSim.Core;

namespace CivSim.Diagnostics;

/// <summary>Metadata about a diagnostic run, passed to the HTML generator.</summary>
public class RunMetadata
{
    public int WorldSize { get; set; }
    public int StartingAgents { get; set; }
    public int RequestedTicks { get; set; }
    public int ActualTicks { get; set; }
    public int Seed { get; set; }
    public string Verbosity { get; set; } = "";
}

/// <summary>
/// Generates self-contained HTML dashboard files from diagnostic CSV data.
/// Uses Chart.js (CDN) for interactive charts. No server needed — just open in a browser.
/// </summary>
public static class HtmlDashboardGenerator
{
    /// <summary>
    /// Generates a single-run dashboard HTML file from CSV data.
    /// </summary>
    public static void GenerateSingleRun(
        string csvPath,
        string htmlPath,
        RunMetadata metadata,
        IReadOnlyList<(int Tick, string Description)> discoveryTimeline)
    {
        // Parse CSV
        var lines = File.ReadAllLines(csvPath);
        if (lines.Length < 2) return; // No data

        var ticks = new List<int>();
        var population = new List<int>();
        var births = new List<int>();
        var deaths = new List<int>();
        var starvationDeaths = new List<int>();
        var oldAgeDeaths = new List<int>();
        var exposureDeaths = new List<int>();
        var avgHunger = new List<float>();
        var avgHealth = new List<float>();
        var oldestAge = new List<int>();
        var totalFood = new List<int>();
        var discoveries = new List<int>();
        var shelterCoverage = new List<float>();
        var granaryFood = new List<int>();
        var settlements = new List<int>();

        // GDD v1.7.1: CSV column order (15 columns):
        // Tick[0],Population[1],Births[2],Deaths[3],StarvationDeaths[4],OldAgeDeaths[5],
        // ExposureDeaths[6],AvgHunger[7],AvgHealth[8],OldestAge[9],TotalFoodOnMap[10],
        // Discoveries[11],ShelterCoverage[12],GranaryFood[13],Settlements[14]
        for (int i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].Split(',');
            if (parts.Length < 14) continue;

            ticks.Add(int.Parse(parts[0]));
            population.Add(int.Parse(parts[1]));
            births.Add(int.Parse(parts[2]));
            deaths.Add(int.Parse(parts[3]));
            starvationDeaths.Add(int.Parse(parts[4]));
            oldAgeDeaths.Add(int.Parse(parts[5]));
            exposureDeaths.Add(int.Parse(parts[6]));
            avgHunger.Add(float.Parse(parts[7]));
            avgHealth.Add(float.Parse(parts[8]));
            oldestAge.Add(int.Parse(parts[9]));
            totalFood.Add(int.Parse(parts[10]));
            discoveries.Add(int.Parse(parts[11]));
            shelterCoverage.Add(float.Parse(parts[12]));
            granaryFood.Add(int.Parse(parts[13]));
            settlements.Add(parts.Length >= 15 ? int.Parse(parts[14]) : 0);
        }

        if (ticks.Count == 0) return;

        // Compute summary stats
        int peakPop = population.Max();
        int peakPopTick = ticks[population.IndexOf(peakPop)];
        int finalPop = population.Last();
        int totalBirths = births.Sum();
        int totalDeathCount = deaths.Sum();
        int totalStarvation = starvationDeaths.Sum();
        int totalOldAge = oldAgeDeaths.Sum();
        int totalExposure = exposureDeaths.Sum();
        float avgPop = (float)population.Average();
        int minFood = totalFood.Min();
        int maxFood = totalFood.Max();
        int finalDiscoveries = discoveries.Last();
        float peakShelter = shelterCoverage.Max();
        int peakGranary = granaryFood.Max();
        int peakSettlements = settlements.Count > 0 ? settlements.Max() : 0;
        bool earlyTermination = metadata.ActualTicks < metadata.RequestedTicks;

        // Bucket births/deaths for bar chart if too many data points
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

        // Serialize data as JSON for embedding
        var jsonOpts = new JsonSerializerOptions { WriteIndented = false };
        string ticksJson = JsonSerializer.Serialize(ticks, jsonOpts);
        string popJson = JsonSerializer.Serialize(population, jsonOpts);
        string hungerJson = JsonSerializer.Serialize(avgHunger, jsonOpts);
        string healthJson = JsonSerializer.Serialize(avgHealth, jsonOpts);
        string foodJson = JsonSerializer.Serialize(totalFood, jsonOpts);
        string discoveriesJson = JsonSerializer.Serialize(discoveries, jsonOpts);
        string bucketLabelsJson = JsonSerializer.Serialize(bucketLabels, jsonOpts);
        string bucketBirthsJson = JsonSerializer.Serialize(bucketedBirths, jsonOpts);
        string bucketDeathsJson = JsonSerializer.Serialize(bucketedDeaths, jsonOpts);
        string shelterJson = JsonSerializer.Serialize(shelterCoverage, jsonOpts);
        string granaryJson = JsonSerializer.Serialize(granaryFood, jsonOpts);

        // Discovery timeline HTML
        var discoveryHtml = new StringBuilder();
        if (discoveryTimeline.Count > 0)
        {
            foreach (var (tick, desc) in discoveryTimeline)
                discoveryHtml.AppendLine($"        <div class=\"discovery-item\"><span class=\"disc-tick\">{Agent.FormatTicks(tick)}</span> {EscapeHtml(desc)}</div>");
        }
        else
        {
            discoveryHtml.AppendLine("        <div class=\"discovery-item\">No discoveries made.</div>");
        }

        string durationStr = earlyTermination
            ? $"{Agent.FormatTicks(metadata.ActualTicks)} (extinct at tick {metadata.ActualTicks} of {metadata.RequestedTicks})"
            : $"{Agent.FormatTicks(metadata.ActualTicks)} ({metadata.ActualTicks} ticks)";

        string runTitle = $"Seed {metadata.Seed} &mdash; {metadata.WorldSize}x{metadata.WorldSize}";

        // Build HTML
        var html = $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>CivSim Dashboard - Seed {metadata.Seed}</title>
<script src=""https://cdn.jsdelivr.net/npm/chart.js@4.4.7/dist/chart.umd.min.js""
        onerror=""document.getElementById('offline-warning').style.display='block'""></script>
<style>
* {{ margin: 0; padding: 0; box-sizing: border-box; }}
body {{
    font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
    background: #0f0f1a;
    color: #e0e0e0;
    padding: 20px;
    min-height: 100vh;
}}
header {{
    text-align: center;
    margin-bottom: 24px;
    padding-bottom: 16px;
    border-bottom: 1px solid #2a2a40;
}}
header h1 {{
    font-size: 1.6em;
    color: #7eb8ff;
    margin-bottom: 6px;
}}
header .subtitle {{
    font-size: 0.9em;
    color: #888;
}}
.summary-panel {{
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
    gap: 10px;
    max-width: 960px;
    margin: 0 auto 28px auto;
}}
.stat-card {{
    background: #16162a;
    border: 1px solid #2a2a40;
    border-radius: 8px;
    padding: 14px 12px;
    text-align: center;
}}
.stat-card .value {{
    font-size: 1.8em;
    font-weight: 700;
    color: #e94560;
    line-height: 1.2;
}}
.stat-card .label {{
    font-size: 0.78em;
    color: #8888aa;
    margin-top: 4px;
    text-transform: uppercase;
    letter-spacing: 0.5px;
}}
.stat-card.green .value {{ color: #4ade80; }}
.stat-card.blue .value {{ color: #60a5fa; }}
.stat-card.amber .value {{ color: #fbbf24; }}
.stat-card.cyan .value {{ color: #22d3ee; }}
.charts {{
    max-width: 960px;
    margin: 0 auto;
}}
.chart-container {{
    background: #16162a;
    border: 1px solid #2a2a40;
    border-radius: 8px;
    padding: 20px;
    margin-bottom: 16px;
}}
.chart-container h3 {{
    font-size: 0.95em;
    color: #7eb8ff;
    margin-bottom: 12px;
    font-weight: 600;
}}
.chart-container canvas {{
    max-height: 280px;
}}
.discovery-section {{
    max-width: 960px;
    margin: 0 auto 28px auto;
    background: #16162a;
    border: 1px solid #2a2a40;
    border-radius: 8px;
    padding: 20px;
}}
.discovery-section h3 {{
    font-size: 0.95em;
    color: #22d3ee;
    margin-bottom: 12px;
    font-weight: 600;
}}
.discovery-item {{
    font-size: 0.85em;
    padding: 4px 0;
    border-bottom: 1px solid #1a1a30;
    color: #c0c0d0;
}}
.disc-tick {{
    display: inline-block;
    min-width: 70px;
    color: #fbbf24;
    font-family: monospace;
    font-weight: 600;
}}
#offline-warning {{
    display: none;
    text-align: center;
    padding: 40px;
    color: #e94560;
    font-size: 1.1em;
    background: #1a1a2e;
    border-radius: 8px;
    margin: 20px auto;
    max-width: 600px;
}}
footer {{
    text-align: center;
    color: #555;
    font-size: 0.75em;
    margin-top: 30px;
    padding-top: 16px;
    border-top: 1px solid #1a1a30;
}}
</style>
</head>
<body>

<header>
    <h1>CivSim Diagnostic Dashboard</h1>
    <div class=""subtitle"">{runTitle} &mdash; {durationStr}</div>
</header>

<div id=""offline-warning"">
    Charts require an internet connection to load Chart.js from CDN.<br>
    The raw data is embedded in this file's source for manual analysis.
</div>

<section class=""summary-panel"">
    <div class=""stat-card blue""><div class=""value"">{peakPop}</div><div class=""label"">Peak Pop (tick {peakPopTick})</div></div>
    <div class=""stat-card""><div class=""value"">{finalPop}</div><div class=""label"">Final Pop</div></div>
    <div class=""stat-card green""><div class=""value"">{totalBirths}</div><div class=""label"">Total Births</div></div>
    <div class=""stat-card""><div class=""value"">{totalDeathCount}</div><div class=""label"">Total Deaths</div></div>
    <div class=""stat-card""><div class=""value"">{totalStarvation}/{totalOldAge}/{totalExposure}</div><div class=""label"">Starved / Old Age / Exposure</div></div>
    <div class=""stat-card amber""><div class=""value"">{avgPop:F1}</div><div class=""label"">Avg Population</div></div>
    <div class=""stat-card amber""><div class=""value"">{minFood:N0}&ndash;{maxFood:N0}</div><div class=""label"">Food Range</div></div>
    <div class=""stat-card cyan""><div class=""value"">{finalDiscoveries}</div><div class=""label"">Discoveries</div></div>
    <div class=""stat-card green""><div class=""value"">{peakShelter:P0}</div><div class=""label"">Peak Shelter</div></div>
    <div class=""stat-card amber""><div class=""value"">{peakGranary}</div><div class=""label"">Peak Granary</div></div>
    <div class=""stat-card cyan""><div class=""value"">{peakSettlements}</div><div class=""label"">Peak Settlements</div></div>
</section>

<section class=""charts"">
    <div class=""chart-container""><h3>Population Over Time</h3><canvas id=""popChart""></canvas></div>
    <div class=""chart-container""><h3>Births &amp; Deaths</h3><canvas id=""bdChart""></canvas></div>
    <div class=""chart-container""><h3>Agent Vitals (Hunger &amp; Health)</h3><canvas id=""vitalsChart""></canvas></div>
    <div class=""chart-container""><h3>Total Food on Map</h3><canvas id=""foodChart""></canvas></div>
    <div class=""chart-container""><h3>Discoveries Over Time</h3><canvas id=""discChart""></canvas></div>
    <div class=""chart-container""><h3>Shelter Coverage</h3><canvas id=""shelterChart""></canvas></div>
    <div class=""chart-container""><h3>Granary Food Storage</h3><canvas id=""granaryChart""></canvas></div>
</section>

<section class=""discovery-section"">
    <h3>Discovery Timeline</h3>
{discoveryHtml}
</section>

<footer>Generated by CivSim Diagnostics &mdash; {DateTime.Now:yyyy-MM-dd HH:mm:ss}</footer>

<script>
const TICKS = {ticksJson};
const POP = {popJson};
const HUNGER = {hungerJson};
const HEALTH = {healthJson};
const FOOD = {foodJson};
const DISC = {discoveriesJson};
const BD_LABELS = {bucketLabelsJson};
const BIRTHS = {bucketBirthsJson};
const DEATHS = {bucketDeathsJson};
const SHELTER = {shelterJson};
const GRANARY = {granaryJson};

const gridColor = 'rgba(255,255,255,0.06)';
const tickColor = '#666';

// Downsample for line charts if too many points
function downsample(labels, data, maxPoints) {{
    if (labels.length <= maxPoints) return {{ labels, data }};
    const step = Math.ceil(labels.length / maxPoints);
    const l = [], d = [];
    for (let i = 0; i < labels.length; i += step) {{
        l.push(labels[i]);
        d.push(data[i]);
    }}
    return {{ labels: l, data: d }};
}}

function downsampleMulti(labels, datasets, maxPoints) {{
    if (labels.length <= maxPoints) return {{ labels, datasets }};
    const step = Math.ceil(labels.length / maxPoints);
    const l = [];
    const ds = datasets.map(() => []);
    for (let i = 0; i < labels.length; i += step) {{
        l.push(labels[i]);
        datasets.forEach((d, idx) => ds[idx].push(d[i]));
    }}
    return {{ labels: l, datasets: ds }};
}}

const MAX_PTS = 800;

// 1. Population
(function() {{
    const s = downsample(TICKS, POP, MAX_PTS);
    new Chart(document.getElementById('popChart'), {{
        type: 'line',
        data: {{
            labels: s.labels,
            datasets: [{{
                label: 'Population',
                data: s.data,
                borderColor: '#60a5fa',
                backgroundColor: 'rgba(96,165,250,0.15)',
                fill: true,
                tension: 0.2,
                pointRadius: 0,
                borderWidth: 2
            }}]
        }},
        options: {{
            responsive: true,
            scales: {{
                x: {{ grid: {{ color: gridColor }}, ticks: {{ color: tickColor, maxTicksLimit: 12 }}, title: {{ display: true, text: 'Tick', color: tickColor }} }},
                y: {{ grid: {{ color: gridColor }}, ticks: {{ color: tickColor }}, beginAtZero: true, title: {{ display: true, text: 'Agents', color: tickColor }} }}
            }},
            plugins: {{ legend: {{ display: false }} }}
        }}
    }});
}})();

// 2. Births & Deaths
(function() {{
    new Chart(document.getElementById('bdChart'), {{
        type: 'bar',
        data: {{
            labels: BD_LABELS,
            datasets: [
                {{ label: 'Births', data: BIRTHS, backgroundColor: 'rgba(74,222,128,0.7)', borderRadius: 2 }},
                {{ label: 'Deaths', data: DEATHS, backgroundColor: 'rgba(233,69,96,0.7)', borderRadius: 2 }}
            ]
        }},
        options: {{
            responsive: true,
            scales: {{
                x: {{ stacked: true, grid: {{ color: gridColor }}, ticks: {{ color: tickColor, maxTicksLimit: 15, maxRotation: 0 }} }},
                y: {{ stacked: true, grid: {{ color: gridColor }}, ticks: {{ color: tickColor }}, beginAtZero: true }}
            }},
            plugins: {{ legend: {{ labels: {{ color: '#ccc' }} }} }}
        }}
    }});
}})();

// 3. Vitals
(function() {{
    const s = downsampleMulti(TICKS, [HUNGER, HEALTH], MAX_PTS);
    new Chart(document.getElementById('vitalsChart'), {{
        type: 'line',
        data: {{
            labels: s.labels,
            datasets: [
                {{
                    label: 'Avg Hunger',
                    data: s.datasets[0],
                    borderColor: '#fbbf24',
                    backgroundColor: 'rgba(251,191,36,0.08)',
                    fill: false,
                    tension: 0.2,
                    pointRadius: 0,
                    borderWidth: 2,
                    yAxisID: 'y'
                }},
                {{
                    label: 'Avg Health',
                    data: s.datasets[1],
                    borderColor: '#4ade80',
                    backgroundColor: 'rgba(74,222,128,0.08)',
                    fill: false,
                    tension: 0.2,
                    pointRadius: 0,
                    borderWidth: 2,
                    yAxisID: 'y1'
                }}
            ]
        }},
        options: {{
            responsive: true,
            interaction: {{ mode: 'index', intersect: false }},
            scales: {{
                x: {{ grid: {{ color: gridColor }}, ticks: {{ color: tickColor, maxTicksLimit: 12 }} }},
                y: {{ type: 'linear', position: 'left', grid: {{ color: gridColor }}, ticks: {{ color: '#fbbf24' }}, min: 0, max: 100, title: {{ display: true, text: 'Hunger', color: '#fbbf24' }} }},
                y1: {{ type: 'linear', position: 'right', grid: {{ drawOnChartArea: false }}, ticks: {{ color: '#4ade80' }}, min: 0, max: 100, title: {{ display: true, text: 'Health', color: '#4ade80' }} }}
            }},
            plugins: {{ legend: {{ labels: {{ color: '#ccc' }} }} }}
        }}
    }});
}})();

// 4. Food
(function() {{
    const s = downsample(TICKS, FOOD, MAX_PTS);
    new Chart(document.getElementById('foodChart'), {{
        type: 'line',
        data: {{
            labels: s.labels,
            datasets: [{{
                label: 'Total Food',
                data: s.data,
                borderColor: '#d97706',
                backgroundColor: 'rgba(217,119,6,0.12)',
                fill: true,
                tension: 0.2,
                pointRadius: 0,
                borderWidth: 2
            }}]
        }},
        options: {{
            responsive: true,
            scales: {{
                x: {{ grid: {{ color: gridColor }}, ticks: {{ color: tickColor, maxTicksLimit: 12 }} }},
                y: {{ grid: {{ color: gridColor }}, ticks: {{ color: tickColor }}, beginAtZero: false }}
            }},
            plugins: {{ legend: {{ display: false }} }}
        }}
    }});
}})();

// 5. Discoveries
(function() {{
    const s = downsample(TICKS, DISC, MAX_PTS);
    new Chart(document.getElementById('discChart'), {{
        type: 'line',
        data: {{
            labels: s.labels,
            datasets: [{{
                label: 'Discoveries',
                data: s.data,
                borderColor: '#22d3ee',
                backgroundColor: 'rgba(34,211,238,0.12)',
                fill: true,
                stepped: true,
                pointRadius: 0,
                borderWidth: 2
            }}]
        }},
        options: {{
            responsive: true,
            scales: {{
                x: {{ grid: {{ color: gridColor }}, ticks: {{ color: tickColor, maxTicksLimit: 12 }} }},
                y: {{ grid: {{ color: gridColor }}, ticks: {{ color: tickColor, stepSize: 1 }}, beginAtZero: true }}
            }},
            plugins: {{ legend: {{ display: false }} }}
        }}
    }});
}})();

// 6. Shelter Coverage
(function() {{
    const s = downsample(TICKS, SHELTER, MAX_PTS);
    new Chart(document.getElementById('shelterChart'), {{
        type: 'line',
        data: {{
            labels: s.labels,
            datasets: [{{
                label: 'Shelter Coverage',
                data: s.data,
                borderColor: '#a78bfa',
                backgroundColor: 'rgba(167,139,250,0.12)',
                fill: true,
                tension: 0.2,
                pointRadius: 0,
                borderWidth: 2
            }}]
        }},
        options: {{
            responsive: true,
            scales: {{
                x: {{ grid: {{ color: gridColor }}, ticks: {{ color: tickColor, maxTicksLimit: 12 }} }},
                y: {{ grid: {{ color: gridColor }}, ticks: {{ color: '#a78bfa', callback: function(v) {{ return (v*100).toFixed(0) + '%'; }} }}, min: 0, max: 1, title: {{ display: true, text: 'Coverage %', color: '#a78bfa' }} }}
            }},
            plugins: {{ legend: {{ display: false }} }}
        }}
    }});
}})();

// 7. Granary Food Storage
(function() {{
    const s = downsample(TICKS, GRANARY, MAX_PTS);
    new Chart(document.getElementById('granaryChart'), {{
        type: 'line',
        data: {{
            labels: s.labels,
            datasets: [{{
                label: 'Granary Food',
                data: s.data,
                borderColor: '#f97316',
                backgroundColor: 'rgba(249,115,22,0.12)',
                fill: true,
                tension: 0.2,
                pointRadius: 0,
                borderWidth: 2
            }}]
        }},
        options: {{
            responsive: true,
            scales: {{
                x: {{ grid: {{ color: gridColor }}, ticks: {{ color: tickColor, maxTicksLimit: 12 }} }},
                y: {{ grid: {{ color: gridColor }}, ticks: {{ color: tickColor }}, beginAtZero: true, title: {{ display: true, text: 'Items', color: tickColor }} }}
            }},
            plugins: {{ legend: {{ display: false }} }}
        }}
    }});
}})();
</script>

</body>
</html>";

        File.WriteAllText(htmlPath, html, Encoding.UTF8);
    }

    /// <summary>
    /// Generates a comparison dashboard HTML file from multiple CSV files (batch mode).
    /// </summary>
    public static void GenerateComparison(string[] csvPaths, string htmlPath)
    {
        // Color palette for multiple runs
        string[] colors = { "#60a5fa", "#4ade80", "#fbbf24", "#e94560", "#22d3ee", "#a78bfa", "#f97316", "#ec4899" };

        // Parse all CSVs
        var runs = new List<(string Label, List<int> Ticks, List<int> Pop, List<int> Food, List<int> Disc)>();

        foreach (var csvPath in csvPaths)
        {
            string label = Path.GetFileNameWithoutExtension(csvPath);
            var lines = File.ReadAllLines(csvPath);
            var t = new List<int>();
            var p = new List<int>();
            var f = new List<int>();
            var d = new List<int>();

            for (int i = 1; i < lines.Length; i++)
            {
                var parts = lines[i].Split(',');
                if (parts.Length < 14) continue;
                t.Add(int.Parse(parts[0]));
                p.Add(int.Parse(parts[1]));
                f.Add(int.Parse(parts[10]));  // TotalFoodOnMap
                d.Add(int.Parse(parts[11]));  // Discoveries
            }

            if (t.Count > 0)
                runs.Add((label, t, p, f, d));
        }

        if (runs.Count == 0) return;

        // Build population datasets
        var popDatasets = new StringBuilder();
        var summaryRows = new StringBuilder();

        for (int r = 0; r < runs.Count; r++)
        {
            var run = runs[r];
            string color = colors[r % colors.Length];
            string popJson = JsonSerializer.Serialize(run.Pop);
            string ticksJson = JsonSerializer.Serialize(run.Ticks);

            if (r > 0) popDatasets.Append(",\n");
            popDatasets.Append($@"        {{
            label: '{EscapeJs(run.Label)}',
            data: {popJson},
            borderColor: '{color}',
            fill: false, tension: 0.2, pointRadius: 0, borderWidth: 2
        }}");

            // Summary row
            int peak = run.Pop.Max();
            int final_ = run.Pop.Last();
            int duration = run.Ticks.Last();
            int disc = run.Disc.Last();
            summaryRows.AppendLine($@"        <tr>
            <td style=""color:{color}; font-weight:bold"">{EscapeHtml(run.Label)}</td>
            <td>{peak}</td><td>{final_}</td><td>{duration}</td><td>{disc}</td>
        </tr>");
        }

        // Use the longest run's ticks as labels
        var longestTicks = runs.OrderByDescending(r => r.Ticks.Count).First().Ticks;
        string labelsJson = JsonSerializer.Serialize(longestTicks);

        string html = $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<title>CivSim Batch Comparison</title>
<script src=""https://cdn.jsdelivr.net/npm/chart.js@4.4.7/dist/chart.umd.min.js""></script>
<style>
* {{ margin: 0; padding: 0; box-sizing: border-box; }}
body {{ font-family: 'Segoe UI', system-ui, sans-serif; background: #0f0f1a; color: #e0e0e0; padding: 20px; }}
header {{ text-align: center; margin-bottom: 24px; }}
header h1 {{ font-size: 1.6em; color: #7eb8ff; }}
.chart-container {{ background: #16162a; border: 1px solid #2a2a40; border-radius: 8px; padding: 20px; margin: 0 auto 16px auto; max-width: 960px; }}
.chart-container h3 {{ font-size: 0.95em; color: #7eb8ff; margin-bottom: 12px; }}
.chart-container canvas {{ max-height: 300px; }}
table {{ max-width: 960px; margin: 20px auto; border-collapse: collapse; width: 100%; }}
th, td {{ padding: 8px 16px; text-align: center; border-bottom: 1px solid #2a2a40; }}
th {{ color: #7eb8ff; font-size: 0.85em; text-transform: uppercase; }}
td {{ font-size: 0.9em; }}
footer {{ text-align: center; color: #555; font-size: 0.75em; margin-top: 30px; }}
</style>
</head>
<body>
<header><h1>CivSim Batch Comparison</h1><p style=""color:#888"">{runs.Count} runs compared</p></header>

<table>
<thead><tr><th>Run</th><th>Peak Pop</th><th>Final Pop</th><th>Duration (ticks)</th><th>Discoveries</th></tr></thead>
<tbody>
{summaryRows}
</tbody>
</table>

<div class=""chart-container""><h3>Population Comparison</h3><canvas id=""popChart""></canvas></div>

<script>
const LABELS = {labelsJson};
new Chart(document.getElementById('popChart'), {{
    type: 'line',
    data: {{
        labels: LABELS,
        datasets: [
{popDatasets}
        ]
    }},
    options: {{
        responsive: true,
        scales: {{
            x: {{ grid: {{ color: 'rgba(255,255,255,0.06)' }}, ticks: {{ color: '#666', maxTicksLimit: 12 }} }},
            y: {{ grid: {{ color: 'rgba(255,255,255,0.06)' }}, ticks: {{ color: '#666' }}, beginAtZero: true }}
        }},
        plugins: {{ legend: {{ labels: {{ color: '#ccc' }} }} }}
    }}
}});
</script>

<footer>Generated by CivSim Diagnostics &mdash; {DateTime.Now:yyyy-MM-dd HH:mm:ss}</footer>
</body>
</html>";

        File.WriteAllText(htmlPath, html, Encoding.UTF8);
    }

    private static string EscapeHtml(string text)
    {
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    private static string EscapeJs(string text)
    {
        return text.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n");
    }
}
