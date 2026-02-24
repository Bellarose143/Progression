namespace CivSim.Diagnostics;

/// <summary>
/// Contains the embedded HTML/CSS/JS for the unified diagnostic dashboard SPA.
/// Served as a single page from the DashboardServer.
/// Dark theme matches the existing per-run HTML dashboards.
/// </summary>
public static class DashboardFrontend
{
    public static readonly string Html = @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
<title>CivSim Diagnostic Dashboard</title>
<script src=""https://cdn.jsdelivr.net/npm/chart.js@4.4.7/dist/chart.umd.min.js""
        onerror=""document.getElementById('offline-warning').style.display='block'""></script>
<style>
*, *::before, *::after { margin: 0; padding: 0; box-sizing: border-box; }

:root {
    --bg: #0f0f1a;
    --surface: #16162a;
    --border: #2a2a40;
    --text: #e0e0e0;
    --text-muted: #8888aa;
    --heading: #7eb8ff;
    --blue: #60a5fa;
    --green: #4ade80;
    --amber: #fbbf24;
    --red: #e94560;
    --cyan: #22d3ee;
    --purple: #a78bfa;
    --orange: #f97316;
    --pink: #ec4899;
    --sidebar-width: 300px;
}

body {
    font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
    background: var(--bg);
    color: var(--text);
    height: 100vh;
    overflow: hidden;
    display: flex;
    flex-direction: column;
}

/* ── Header ── */
.header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 12px 20px;
    border-bottom: 1px solid var(--border);
    background: var(--surface);
    flex-shrink: 0;
}
.header h1 {
    font-size: 1.2em;
    color: var(--heading);
    font-weight: 700;
}
.header-right {
    display: flex;
    align-items: center;
    gap: 12px;
    font-size: 0.82em;
}
.header-right label {
    color: var(--text-muted);
    cursor: pointer;
    display: flex;
    align-items: center;
    gap: 4px;
}
.header-right input[type=checkbox] { accent-color: var(--cyan); }
.run-count { color: var(--text-muted); font-size: 0.82em; }

/* ── Layout ── */
.layout {
    display: flex;
    flex: 1;
    overflow: hidden;
}

/* ── Sidebar ── */
.sidebar {
    width: var(--sidebar-width);
    min-width: var(--sidebar-width);
    border-right: 1px solid var(--border);
    display: flex;
    flex-direction: column;
    background: var(--surface);
    overflow: hidden;
}
.sidebar-controls {
    padding: 10px 12px;
    border-bottom: 1px solid var(--border);
    display: flex;
    flex-direction: column;
    gap: 6px;
    flex-shrink: 0;
}
.sidebar-controls select,
.sidebar-controls input[type=text] {
    width: 100%;
    padding: 6px 8px;
    background: var(--bg);
    color: var(--text);
    border: 1px solid var(--border);
    border-radius: 4px;
    font-size: 0.8em;
    font-family: inherit;
}
.sidebar-controls select:focus,
.sidebar-controls input:focus {
    outline: none;
    border-color: var(--heading);
}
.sidebar-controls label {
    font-size: 0.72em;
    color: var(--text-muted);
    text-transform: uppercase;
    letter-spacing: 0.5px;
}

.run-list {
    flex: 1;
    overflow-y: auto;
    padding: 6px;
}
.run-list::-webkit-scrollbar { width: 6px; }
.run-list::-webkit-scrollbar-track { background: transparent; }
.run-list::-webkit-scrollbar-thumb { background: var(--border); border-radius: 3px; }

.run-card {
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: 6px;
    padding: 10px;
    margin-bottom: 4px;
    cursor: pointer;
    transition: border-color 0.15s, background 0.15s;
    display: flex;
    gap: 8px;
    align-items: flex-start;
}
.run-card:hover { border-color: var(--heading); }
.run-card.selected { border-color: var(--blue); background: rgba(96,165,250,0.08); }
.run-card.compare-checked { border-color: var(--cyan); }
.run-card .check {
    flex-shrink: 0;
    margin-top: 2px;
    accent-color: var(--cyan);
}
.run-card-body { flex: 1; min-width: 0; }
.run-card-title {
    font-size: 0.88em;
    font-weight: 600;
    color: var(--text);
    margin-bottom: 2px;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}
.run-card-meta {
    font-size: 0.72em;
    color: var(--text-muted);
    margin-bottom: 4px;
}
.run-card-stats {
    display: flex;
    gap: 8px;
    font-size: 0.72em;
}
.run-card-stats span { white-space: nowrap; }
.badge {
    display: inline-block;
    padding: 1px 6px;
    border-radius: 3px;
    font-size: 0.7em;
    font-weight: 600;
    text-transform: uppercase;
}
.badge.survived { background: rgba(74,222,128,0.15); color: var(--green); }
.badge.extinct { background: rgba(233,69,96,0.15); color: var(--red); }
.badge.batch { background: rgba(167,139,250,0.15); color: var(--purple); font-size: 0.65em; }

.sidebar-footer {
    padding: 10px 12px;
    border-top: 1px solid var(--border);
    flex-shrink: 0;
}
.compare-btn {
    width: 100%;
    padding: 8px;
    background: var(--cyan);
    color: var(--bg);
    border: none;
    border-radius: 5px;
    font-weight: 700;
    font-size: 0.85em;
    cursor: pointer;
    transition: opacity 0.15s;
    font-family: inherit;
}
.compare-btn:disabled { opacity: 0.3; cursor: not-allowed; }
.compare-btn:not(:disabled):hover { opacity: 0.85; }

/* ── Main Content ── */
.main {
    flex: 1;
    overflow-y: auto;
    padding: 20px;
}
.main::-webkit-scrollbar { width: 8px; }
.main::-webkit-scrollbar-track { background: transparent; }
.main::-webkit-scrollbar-thumb { background: var(--border); border-radius: 4px; }

.welcome {
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    height: 100%;
    color: var(--text-muted);
    text-align: center;
}
.welcome h2 { font-size: 1.3em; color: var(--heading); margin-bottom: 8px; }
.welcome p { font-size: 0.9em; max-width: 400px; line-height: 1.6; }

.detail-header {
    text-align: center;
    margin-bottom: 20px;
}
.detail-header h2 {
    font-size: 1.3em;
    color: var(--heading);
    margin-bottom: 4px;
}
.detail-header .subtitle { font-size: 0.85em; color: var(--text-muted); }

/* ── Summary Stat Cards ── */
.summary-panel {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
    gap: 8px;
    max-width: 900px;
    margin: 0 auto 20px auto;
}
.stat-card {
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: 6px;
    padding: 12px 10px;
    text-align: center;
}
.stat-card .value {
    font-size: 1.6em;
    font-weight: 700;
    color: var(--red);
    line-height: 1.2;
}
.stat-card .label {
    font-size: 0.7em;
    color: var(--text-muted);
    margin-top: 3px;
    text-transform: uppercase;
    letter-spacing: 0.4px;
}
.stat-card.blue .value { color: var(--blue); }
.stat-card.green .value { color: var(--green); }
.stat-card.amber .value { color: var(--amber); }
.stat-card.cyan .value { color: var(--cyan); }

/* ── Charts ── */
.charts { max-width: 900px; margin: 0 auto; }
.chart-container {
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 16px;
    margin-bottom: 12px;
}
.chart-container h3 {
    font-size: 0.9em;
    color: var(--heading);
    margin-bottom: 10px;
    font-weight: 600;
}
.chart-container canvas { max-height: 260px; }

/* ── Discovery Timeline ── */
.discovery-section {
    max-width: 900px;
    margin: 0 auto 20px auto;
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: 8px;
    padding: 16px;
}
.discovery-section h3 {
    font-size: 0.9em;
    color: var(--cyan);
    margin-bottom: 10px;
    font-weight: 600;
}
.discovery-item {
    font-size: 0.82em;
    padding: 3px 0;
    border-bottom: 1px solid rgba(42,42,64,0.5);
    color: #c0c0d0;
}
.disc-tick {
    display: inline-block;
    min-width: 65px;
    color: var(--amber);
    font-family: monospace;
    font-weight: 600;
}

/* ── Compare Table ── */
.compare-table {
    max-width: 900px;
    margin: 0 auto 20px auto;
    width: 100%;
    border-collapse: collapse;
}
.compare-table th {
    padding: 8px 12px;
    text-align: center;
    border-bottom: 2px solid var(--border);
    color: var(--heading);
    font-size: 0.78em;
    text-transform: uppercase;
    letter-spacing: 0.4px;
}
.compare-table td {
    padding: 8px 12px;
    text-align: center;
    border-bottom: 1px solid var(--border);
    font-size: 0.85em;
}

/* ── Notification Toast ── */
.toast {
    position: fixed;
    top: 12px;
    left: 50%;
    transform: translateX(-50%) translateY(-60px);
    background: var(--surface);
    border: 1px solid var(--cyan);
    color: var(--cyan);
    padding: 8px 20px;
    border-radius: 6px;
    font-size: 0.82em;
    font-weight: 600;
    transition: transform 0.3s ease;
    z-index: 100;
    pointer-events: none;
}
.toast.show { transform: translateX(-50%) translateY(0); }

/* ── Offline Warning ── */
#offline-warning {
    display: none;
    text-align: center;
    padding: 30px;
    color: var(--red);
    font-size: 1em;
}

/* ── Loading spinner ── */
.loading {
    text-align: center;
    padding: 60px;
    color: var(--text-muted);
}
.loading::after {
    content: '';
    display: inline-block;
    width: 20px;
    height: 20px;
    border: 2px solid var(--border);
    border-top-color: var(--cyan);
    border-radius: 50%;
    animation: spin 0.8s linear infinite;
    margin-left: 8px;
    vertical-align: middle;
}
@keyframes spin { to { transform: rotate(360deg); } }
</style>
</head>
<body>

<div class=""header"">
    <h1>CivSim Diagnostic Dashboard</h1>
    <div class=""header-right"">
        <span class=""run-count"" id=""runCount""></span>
        <label>
            <input type=""checkbox"" id=""autoRefresh"" checked>
            Auto-refresh
        </label>
    </div>
</div>

<div class=""layout"">
    <div class=""sidebar"">
        <div class=""sidebar-controls"">
            <div>
                <label>Sort by</label>
                <select id=""sortSelect"">
                    <option value=""startedAt-desc"">Date (newest)</option>
                    <option value=""startedAt-asc"">Date (oldest)</option>
                    <option value=""seed-asc"">Seed</option>
                    <option value=""peakPopulation-desc"">Peak Population</option>
                    <option value=""actualTicks-desc"">Duration</option>
                    <option value=""outcome-asc"">Outcome</option>
                </select>
            </div>
            <div>
                <label>Filter</label>
                <input type=""text"" id=""filterText"" placeholder=""Search seed, size, batch..."">
            </div>
            <div>
                <label>Outcome</label>
                <select id=""filterOutcome"">
                    <option value=""all"">All</option>
                    <option value=""survived"">Survived</option>
                    <option value=""extinct"">Extinct</option>
                </select>
            </div>
        </div>
        <div class=""run-list"" id=""runList""></div>
        <div class=""sidebar-footer"">
            <button class=""compare-btn"" id=""compareBtn"" disabled>Compare Selected (0)</button>
        </div>
    </div>

    <div class=""main"" id=""mainContent"">
        <div class=""welcome"">
            <h2>Welcome</h2>
            <p>Select a run from the sidebar to view its charts, or check multiple runs and click Compare.</p>
        </div>
    </div>
</div>

<div class=""toast"" id=""toast""></div>
<div id=""offline-warning"">Charts require an internet connection to load Chart.js from CDN.</div>

<script>
// ── State ──
const state = {
    runs: [],
    selectedRunId: null,
    compareRunIds: [],
    compareMode: false,
    charts: {}
};

const COLORS = ['#60a5fa','#4ade80','#fbbf24','#e94560','#22d3ee','#a78bfa','#f97316','#ec4899'];
const GRID_COLOR = 'rgba(255,255,255,0.06)';
const TICK_COLOR = '#666';
const MAX_PTS = 800;

// ── DOM refs ──
const runListEl = document.getElementById('runList');
const mainEl = document.getElementById('mainContent');
const compareBtnEl = document.getElementById('compareBtn');
const sortSelectEl = document.getElementById('sortSelect');
const filterTextEl = document.getElementById('filterText');
const filterOutcomeEl = document.getElementById('filterOutcome');
const autoRefreshEl = document.getElementById('autoRefresh');
const runCountEl = document.getElementById('runCount');
const toastEl = document.getElementById('toast');

// ── Downsampling (matches existing HtmlDashboardGenerator) ──
function downsample(labels, data, maxPts) {
    if (labels.length <= maxPts) return { labels, data };
    const step = Math.ceil(labels.length / maxPts);
    const l = [], d = [];
    for (let i = 0; i < labels.length; i += step) { l.push(labels[i]); d.push(data[i]); }
    return { labels: l, data: d };
}

function downsampleMulti(labels, datasets, maxPts) {
    if (labels.length <= maxPts) return { labels, datasets };
    const step = Math.ceil(labels.length / maxPts);
    const l = [];
    const ds = datasets.map(() => []);
    for (let i = 0; i < labels.length; i += step) {
        l.push(labels[i]);
        datasets.forEach((d, idx) => ds[idx].push(d[i]));
    }
    return { labels: l, datasets: ds };
}

// ── API ──
async function fetchRuns() {
    const resp = await fetch('/api/runs');
    return await resp.json();
}

async function fetchRunDetail(runId) {
    const resp = await fetch('/api/runs/' + encodeURIComponent(runId));
    return await resp.json();
}

// ── Toast notification ──
let toastTimer = null;
function showToast(msg) {
    toastEl.textContent = msg;
    toastEl.classList.add('show');
    if (toastTimer) clearTimeout(toastTimer);
    toastTimer = setTimeout(() => toastEl.classList.remove('show'), 3000);
}

// ── Date formatting ──
function fmtDate(dateStr) {
    const d = new Date(dateStr);
    const mon = (d.getMonth()+1).toString().padStart(2,'0');
    const day = d.getDate().toString().padStart(2,'0');
    const hr = d.getHours().toString().padStart(2,'0');
    const mn = d.getMinutes().toString().padStart(2,'0');
    return mon + '/' + day + ' ' + hr + ':' + mn;
}

function fmtTicks(t) {
    // GDD v1.8 Corrections: TicksPerSimDay=480, SimDaysPerSeason=7, SeasonsPerYear=4, SimDaysPerYear=28
    // Must match C# Agent.FormatTicks().
    const TICKS_PER_DAY = 480;
    const DAYS_PER_SEASON = 7;
    const DAYS_PER_YEAR = 28;
    const simDays = Math.floor(t / TICKS_PER_DAY);
    if (simDays < DAYS_PER_SEASON) return simDays + 'd';
    if (simDays < DAYS_PER_YEAR) {
        const seasons = Math.floor(simDays / DAYS_PER_SEASON);
        const remDays = simDays % DAYS_PER_SEASON;
        return remDays > 0 ? seasons + 's ' + remDays + 'd' : seasons + 's';
    }
    const years = Math.floor(simDays / DAYS_PER_YEAR);
    const remSimDays = simDays % DAYS_PER_YEAR;
    const remSeasons = Math.floor(remSimDays / DAYS_PER_SEASON);
    if (remSeasons > 0) return years + 'y ' + remSeasons + 's';
    return years + 'y';
}

// ── Filtering & Sorting ──
function getFilteredRuns() {
    const outcomeFilter = filterOutcomeEl.value;
    const textFilter = filterTextEl.value.toLowerCase().trim();
    const [sortField, sortDir] = sortSelectEl.value.split('-');
    const asc = sortDir === 'asc';

    let filtered = state.runs.filter(r => {
        if (outcomeFilter !== 'all' && r.outcome !== outcomeFilter) return false;
        if (textFilter) {
            const haystack = [r.seed, r.worldSize, r.batchLabel || '', r.id].join(' ').toLowerCase();
            if (!haystack.includes(textFilter)) return false;
        }
        return true;
    });

    filtered.sort((a, b) => {
        let va = a[sortField], vb = b[sortField];
        if (typeof va === 'string') { va = va.toLowerCase(); vb = vb.toLowerCase(); }
        let cmp = va < vb ? -1 : va > vb ? 1 : 0;
        return asc ? cmp : -cmp;
    });

    return filtered;
}

// ── Render Sidebar ──
function renderSidebar() {
    const filtered = getFilteredRuns();
    runCountEl.textContent = state.runs.length + ' run' + (state.runs.length !== 1 ? 's' : '');

    runListEl.innerHTML = filtered.map(r => {
        const isSelected = r.id === state.selectedRunId;
        const isCompare = state.compareRunIds.includes(r.id);
        let cls = 'run-card';
        if (isSelected) cls += ' selected';
        if (isCompare) cls += ' compare-checked';

        const title = r.seed ? ('Seed ' + r.seed) : r.id;
        const meta = (r.worldSize ? r.worldSize + 'x' + r.worldSize : '?') +
            ' | ' + (r.startingAgents || '?') + ' agents | ' +
            fmtTicks(r.actualTicks);

        return '<div class=""' + cls + '"" data-id=""' + r.id + '"">' +
            '<input type=""checkbox"" class=""check"" ' + (isCompare ? 'checked' : '') +
                ' data-cid=""' + r.id + '"">' +
            '<div class=""run-card-body"">' +
                '<div class=""run-card-title"">' + escHtml(title) + '</div>' +
                '<div class=""run-card-meta"">' + escHtml(meta) + '</div>' +
                '<div class=""run-card-stats"">' +
                    '<span class=""badge ' + r.outcome + '"">' + r.outcome + '</span>' +
                    ' <span>Peak: ' + r.peakPopulation + '</span>' +
                    ' <span>Disc: ' + r.discoveries + '</span>' +
                    (r.batchLabel ? ' <span class=""badge batch"">' + escHtml(r.batchLabel) + '</span>' : '') +
                '</div>' +
                '<div class=""run-card-meta"" style=""margin-top:3px"">' + fmtDate(r.startedAt) + '</div>' +
            '</div>' +
        '</div>';
    }).join('');

    updateCompareBtn();
}

function updateCompareBtn() {
    const n = state.compareRunIds.length;
    compareBtnEl.textContent = 'Compare Selected (' + n + ')';
    compareBtnEl.disabled = n < 2;
}

// ── Sidebar Event Delegation ──
runListEl.addEventListener('click', (e) => {
    // Checkbox click
    const checkbox = e.target.closest('.check');
    if (checkbox) {
        e.stopPropagation();
        const cid = checkbox.dataset.cid;
        const idx = state.compareRunIds.indexOf(cid);
        if (idx >= 0) state.compareRunIds.splice(idx, 1);
        else state.compareRunIds.push(cid);
        renderSidebar();
        return;
    }
    // Card click
    const card = e.target.closest('.run-card');
    if (card) {
        const id = card.dataset.id;
        showRunDetail(id);
    }
});

compareBtnEl.addEventListener('click', () => {
    if (state.compareRunIds.length >= 2) showComparison();
});

sortSelectEl.addEventListener('change', renderSidebar);
filterTextEl.addEventListener('input', renderSidebar);
filterOutcomeEl.addEventListener('change', renderSidebar);

// ── Destroy existing charts ──
function destroyCharts() {
    Object.values(state.charts).forEach(c => { try { c.destroy(); } catch {} });
    state.charts = {};
}

// ── Show Single Run Detail ──
async function showRunDetail(runId) {
    state.selectedRunId = runId;
    state.compareMode = false;
    renderSidebar();

    mainEl.innerHTML = '<div class=""loading"">Loading</div>';

    let data;
    try {
        data = await fetchRunDetail(runId);
    } catch (err) {
        mainEl.innerHTML = '<div class=""welcome""><h2>Error</h2><p>Failed to load run data.</p></div>';
        return;
    }

    destroyCharts();

    const earlyTerm = data.requestedTicks > 0 && data.ticks[data.ticks.length-1] < data.requestedTicks;
    const duration = fmtTicks(data.ticks[data.ticks.length-1]);
    const durationNote = earlyTerm
        ? duration + ' (extinct at tick ' + data.ticks[data.ticks.length-1] + ' of ' + data.requestedTicks + ')'
        : duration + ' (' + data.ticks[data.ticks.length-1] + ' ticks)';

    let html = '<div class=""detail-header"">' +
        '<h2>' + escHtml(data.label) + '</h2>' +
        '<div class=""subtitle"">' +
            (data.worldSize ? data.worldSize + 'x' + data.worldSize + ' &mdash; ' : '') +
            durationNote +
        '</div></div>';

    // Stat cards
    const avgPop = data.population.length
        ? (data.population.reduce((a,b) => a+b, 0) / data.population.length).toFixed(1) : '0';
    const minFood = data.totalFood.length ? Math.min(...data.totalFood) : 0;
    const maxFood = data.totalFood.length ? Math.max(...data.totalFood) : 0;
    const finalDisc = data.discoveries.length ? data.discoveries[data.discoveries.length-1] : 0;

    html += '<section class=""summary-panel"">' +
        statCard(data.peakPopulation, 'Peak Pop', 'blue') +
        statCard(data.finalPopulation, 'Final Pop', '') +
        statCard(data.totalBirths, 'Births', 'green') +
        statCard(data.totalDeaths, 'Deaths', '') +
        statCard(data.starvationDeaths + ' / ' + data.oldAgeDeaths, 'Starved / Old Age', '') +
        statCard(avgPop, 'Avg Pop', 'amber') +
        statCard(minFood.toLocaleString() + ' - ' + maxFood.toLocaleString(), 'Food Range', 'amber') +
        statCard(finalDisc, 'Discoveries', 'cyan') +
    '</section>';

    // Charts
    html += '<section class=""charts"">' +
        chartBox('popChart', 'Population Over Time') +
        chartBox('bdChart', 'Births &amp; Deaths') +
        chartBox('vitalsChart', 'Agent Vitals (Hunger &amp; Health)') +
        chartBox('foodChart', 'Total Food on Map') +
        chartBox('discChart', 'Discoveries Over Time') +
    '</section>';

    // Discovery timeline
    html += '<section class=""discovery-section""><h3>Discovery Timeline</h3>';
    if (data.discoveryTimeline && data.discoveryTimeline.length > 0) {
        data.discoveryTimeline.forEach(d => {
            html += '<div class=""discovery-item""><span class=""disc-tick"">' + escHtml(d.time) + '</span> ' + escHtml(d.description) + '</div>';
        });
    } else {
        html += '<div class=""discovery-item"">No discoveries made.</div>';
    }
    html += '</section>';

    mainEl.innerHTML = html;

    // Create charts after DOM is ready
    requestAnimationFrame(() => createDetailCharts(data));
}

function statCard(value, label, colorClass) {
    return '<div class=""stat-card ' + colorClass + '""><div class=""value"">' + value + '</div><div class=""label"">' + label + '</div></div>';
}

function chartBox(id, title) {
    return '<div class=""chart-container""><h3>' + title + '</h3><canvas id=""' + id + '""></canvas></div>';
}

function createDetailCharts(data) {
    if (typeof Chart === 'undefined') return;

    const baseOpts = { responsive: true, animation: { duration: 300 } };

    // 1. Population
    const pop = downsample(data.ticks, data.population, MAX_PTS);
    state.charts.pop = new Chart(document.getElementById('popChart'), {
        type: 'line',
        data: {
            labels: pop.labels,
            datasets: [{
                label: 'Population',
                data: pop.data,
                borderColor: '#60a5fa',
                backgroundColor: 'rgba(96,165,250,0.15)',
                fill: true, tension: 0.2, pointRadius: 0, borderWidth: 2
            }]
        },
        options: {
            ...baseOpts,
            scales: {
                x: { grid: { color: GRID_COLOR }, ticks: { color: TICK_COLOR, maxTicksLimit: 12 }, title: { display: true, text: 'Tick', color: TICK_COLOR } },
                y: { grid: { color: GRID_COLOR }, ticks: { color: TICK_COLOR }, beginAtZero: true, title: { display: true, text: 'Agents', color: TICK_COLOR } }
            },
            plugins: { legend: { display: false } }
        }
    });

    // 2. Births & Deaths
    state.charts.bd = new Chart(document.getElementById('bdChart'), {
        type: 'bar',
        data: {
            labels: data.birthDeathLabels,
            datasets: [
                { label: 'Births', data: data.births, backgroundColor: 'rgba(74,222,128,0.7)', borderRadius: 2 },
                { label: 'Deaths', data: data.deaths, backgroundColor: 'rgba(233,69,96,0.7)', borderRadius: 2 }
            ]
        },
        options: {
            ...baseOpts,
            scales: {
                x: { stacked: true, grid: { color: GRID_COLOR }, ticks: { color: TICK_COLOR, maxTicksLimit: 15, maxRotation: 0 } },
                y: { stacked: true, grid: { color: GRID_COLOR }, ticks: { color: TICK_COLOR }, beginAtZero: true }
            },
            plugins: { legend: { labels: { color: '#ccc' } } }
        }
    });

    // 3. Vitals
    const vitals = downsampleMulti(data.ticks, [data.avgHunger, data.avgHealth], MAX_PTS);
    state.charts.vitals = new Chart(document.getElementById('vitalsChart'), {
        type: 'line',
        data: {
            labels: vitals.labels,
            datasets: [
                {
                    label: 'Avg Hunger', data: vitals.datasets[0],
                    borderColor: '#fbbf24', fill: false, tension: 0.2, pointRadius: 0, borderWidth: 2, yAxisID: 'y'
                },
                {
                    label: 'Avg Health', data: vitals.datasets[1],
                    borderColor: '#4ade80', fill: false, tension: 0.2, pointRadius: 0, borderWidth: 2, yAxisID: 'y1'
                }
            ]
        },
        options: {
            ...baseOpts,
            interaction: { mode: 'index', intersect: false },
            scales: {
                x: { grid: { color: GRID_COLOR }, ticks: { color: TICK_COLOR, maxTicksLimit: 12 } },
                y: { type: 'linear', position: 'left', grid: { color: GRID_COLOR }, ticks: { color: '#fbbf24' }, min: 0, max: 100, title: { display: true, text: 'Hunger', color: '#fbbf24' } },
                y1: { type: 'linear', position: 'right', grid: { drawOnChartArea: false }, ticks: { color: '#4ade80' }, min: 0, max: 100, title: { display: true, text: 'Health', color: '#4ade80' } }
            },
            plugins: { legend: { labels: { color: '#ccc' } } }
        }
    });

    // 4. Food
    const food = downsample(data.ticks, data.totalFood, MAX_PTS);
    state.charts.food = new Chart(document.getElementById('foodChart'), {
        type: 'line',
        data: {
            labels: food.labels,
            datasets: [{
                label: 'Total Food', data: food.data,
                borderColor: '#d97706', backgroundColor: 'rgba(217,119,6,0.12)',
                fill: true, tension: 0.2, pointRadius: 0, borderWidth: 2
            }]
        },
        options: {
            ...baseOpts,
            scales: {
                x: { grid: { color: GRID_COLOR }, ticks: { color: TICK_COLOR, maxTicksLimit: 12 } },
                y: { grid: { color: GRID_COLOR }, ticks: { color: TICK_COLOR }, beginAtZero: false }
            },
            plugins: { legend: { display: false } }
        }
    });

    // 5. Discoveries
    const disc = downsample(data.ticks, data.discoveries, MAX_PTS);
    state.charts.disc = new Chart(document.getElementById('discChart'), {
        type: 'line',
        data: {
            labels: disc.labels,
            datasets: [{
                label: 'Discoveries', data: disc.data,
                borderColor: '#22d3ee', backgroundColor: 'rgba(34,211,238,0.12)',
                fill: true, stepped: true, pointRadius: 0, borderWidth: 2
            }]
        },
        options: {
            ...baseOpts,
            scales: {
                x: { grid: { color: GRID_COLOR }, ticks: { color: TICK_COLOR, maxTicksLimit: 12 } },
                y: { grid: { color: GRID_COLOR }, ticks: { color: TICK_COLOR, stepSize: 1 }, beginAtZero: true }
            },
            plugins: { legend: { display: false } }
        }
    });
}

// ── Show Comparison ──
async function showComparison() {
    state.compareMode = true;
    state.selectedRunId = null;
    renderSidebar();

    mainEl.innerHTML = '<div class=""loading"">Loading comparison</div>';

    let details;
    try {
        details = await Promise.all(state.compareRunIds.map(id => fetchRunDetail(id)));
    } catch {
        mainEl.innerHTML = '<div class=""welcome""><h2>Error</h2><p>Failed to load comparison data.</p></div>';
        return;
    }

    destroyCharts();

    let html = '<div class=""detail-header""><h2>Run Comparison</h2>' +
        '<div class=""subtitle"">' + details.length + ' runs selected</div></div>';

    // Summary table
    html += '<table class=""compare-table""><thead><tr>' +
        '<th>Run</th><th>World</th><th>Agents</th><th>Peak Pop</th><th>Final Pop</th><th>Duration</th><th>Discoveries</th><th>Outcome</th>' +
    '</tr></thead><tbody>';
    details.forEach((d, i) => {
        const color = COLORS[i % COLORS.length];
        const lastTick = d.ticks.length ? d.ticks[d.ticks.length-1] : 0;
        const outcome = d.finalPopulation > 0 ? 'survived' : 'extinct';
        html += '<tr>' +
            '<td style=""color:' + color + ';font-weight:bold"">' + escHtml(d.label) + '</td>' +
            '<td>' + (d.worldSize ? d.worldSize + 'x' + d.worldSize : '?') + '</td>' +
            '<td>' + (d.startingAgents || '?') + '</td>' +
            '<td>' + d.peakPopulation + '</td>' +
            '<td>' + d.finalPopulation + '</td>' +
            '<td>' + fmtTicks(lastTick) + '</td>' +
            '<td>' + (d.discoveries.length ? d.discoveries[d.discoveries.length-1] : 0) + '</td>' +
            '<td><span class=""badge ' + outcome + '"">' + outcome + '</span></td>' +
        '</tr>';
    });
    html += '</tbody></table>';

    // Charts
    html += '<section class=""charts"">' +
        chartBox('comparePopChart', 'Population Comparison') +
        chartBox('compareFoodChart', 'Food Comparison') +
        chartBox('compareDiscChart', 'Discovery Comparison') +
    '</section>';

    mainEl.innerHTML = html;
    requestAnimationFrame(() => createCompareCharts(details));
}

function createCompareCharts(details) {
    if (typeof Chart === 'undefined') return;
    const baseOpts = { responsive: true, animation: { duration: 300 } };

    // Find longest ticks for labels
    const longestTicks = details.reduce((a, b) => a.ticks.length > b.ticks.length ? a : b).ticks;

    function makeOverlay(canvasId, key, yLabel) {
        const datasets = details.map((d, i) => {
            const s = downsample(d.ticks, d[key], MAX_PTS);
            return {
                label: d.label,
                data: s.data,
                borderColor: COLORS[i % COLORS.length],
                fill: false, tension: 0.2, pointRadius: 0, borderWidth: 2
            };
        });
        const labels = downsample(longestTicks, longestTicks, MAX_PTS).labels;

        return new Chart(document.getElementById(canvasId), {
            type: 'line',
            data: { labels, datasets },
            options: {
                ...baseOpts,
                scales: {
                    x: { grid: { color: GRID_COLOR }, ticks: { color: TICK_COLOR, maxTicksLimit: 12 } },
                    y: { grid: { color: GRID_COLOR }, ticks: { color: TICK_COLOR }, beginAtZero: true,
                         title: { display: true, text: yLabel, color: TICK_COLOR } }
                },
                plugins: { legend: { labels: { color: '#ccc' } } }
            }
        });
    }

    state.charts.comparePop = makeOverlay('comparePopChart', 'population', 'Agents');
    state.charts.compareFood = makeOverlay('compareFoodChart', 'totalFood', 'Food');
    state.charts.compareDisc = makeOverlay('compareDiscChart', 'discoveries', 'Discoveries');
}

// ── HTML escaping ──
function escHtml(s) {
    if (!s) return '';
    return s.toString().replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/""/g,'&quot;');
}

// ── Auto-refresh ──
let refreshTimer = null;
function startAutoRefresh() {
    stopAutoRefresh();
    refreshTimer = setInterval(async () => {
        if (!autoRefreshEl.checked) return;
        try {
            const runs = await fetchRuns();
            if (runs.length !== state.runs.length) {
                const diff = runs.length - state.runs.length;
                state.runs = runs;
                renderSidebar();
                if (diff > 0) showToast(diff + ' new run' + (diff > 1 ? 's' : '') + ' detected');
            }
        } catch {}
    }, 5000);
}
function stopAutoRefresh() {
    if (refreshTimer) { clearInterval(refreshTimer); refreshTimer = null; }
}

autoRefreshEl.addEventListener('change', () => {
    if (autoRefreshEl.checked) startAutoRefresh();
    else stopAutoRefresh();
});

// ── Init ──
async function init() {
    try {
        state.runs = await fetchRuns();
        renderSidebar();
        startAutoRefresh();
    } catch (err) {
        mainEl.innerHTML = '<div class=""welcome""><h2>Connection Error</h2><p>Could not connect to the dashboard server API.</p></div>';
    }
}

init();
</script>
</body>
</html>";
}
