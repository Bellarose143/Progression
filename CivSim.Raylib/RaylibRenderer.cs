using Raylib_cs;
using CivSim.Core;
using CivSim.Raylib.Rendering;
using System.Numerics;
using Rl = Raylib_cs.Raylib;

namespace CivSim.Raylib;

/// <summary>
/// Thin orchestrator that coordinates all rendering subsystems.
/// Owns the camera, selection state, display settings, and sprite atlas.
/// </summary>
public class RaylibRenderer : IDisposable
{
    private readonly World world;
    private readonly int tileSize;
    private int screenWidth;
    private int screenHeight;

    // Sprite atlas (shared across subsystems)
    private readonly SpriteAtlas atlas;

    // Subsystems
    private readonly WorldRenderer worldRenderer;
    private readonly AgentRenderer agentRenderer;
    private readonly UIRenderer uiRenderer = new();
    private readonly NotificationManager notificationManager = new();
    private readonly AnimalRenderer animalRenderer;
    private readonly VisualEffectManager effectManager;
    private readonly TechTreeRenderer techTreeRenderer = new();
    private readonly StructureRegistry structureRegistry;
    private readonly ResourceSpriteRegistry resourceRegistry;

    // PERF-01: Shared sprite batch for world-space atlas rendering
    private readonly SpriteBatch spriteBatch = new();

    // Camera
    private Camera2D camera;

    // Selection state
    public Agent? SelectedAgent { get; set; }
    public (int X, int Y)? SelectedTile { get; set; }
    public bool FollowMode { get; set; }
    private int tabIndex = -1;
    private int? expandedAgentId = null;

    // Display settings
    public bool ShowGrid { get; set; }
    public bool ShowDiscoveryPanel { get; set; }
    public bool ShowTechTree { get; set; }
    public EventFilterLevel EventFilter { get; set; } = EventFilterLevel.High;

    // Timing
    private float elapsedTime;

    /// <summary>Interpolation alpha (0..1) between last tick and next tick. Set by caller each frame.</summary>
    public float LerpAlpha { get; set; } = 1.0f;

    public RaylibRenderer(World world, int tileSize, int screenWidth, int screenHeight,
                           (int X, int Y)? spawnCenter = null)
    {
        this.world = world;
        this.tileSize = tileSize;
        this.screenWidth = screenWidth;
        this.screenHeight = screenHeight;

        // Load sprite atlas (must be after Raylib.InitWindow)
        atlas = new SpriteAtlas();
        atlas.Load();

        // Load structure registry
        structureRegistry = new StructureRegistry();
        structureRegistry.Load(atlas);

        // Load resource sprite registry
        resourceRegistry = new ResourceSpriteRegistry();
        resourceRegistry.Load(atlas);

        // Create subsystems with atlas
        worldRenderer = new WorldRenderer(atlas, structureRegistry, resourceRegistry);
        agentRenderer = new AgentRenderer(atlas);
        animalRenderer = new AnimalRenderer(atlas);
        effectManager = new VisualEffectManager(atlas);

        // Initialize camera centered on spawn or world center
        float cx, cy;
        if (spawnCenter.HasValue)
        {
            cx = spawnCenter.Value.X * tileSize + tileSize / 2f;
            cy = spawnCenter.Value.Y * tileSize + tileSize / 2f;
        }
        else
        {
            cx = world.Width * tileSize / 2f;
            cy = world.Height * tileSize / 2f;
        }

        // Offset: center the world in the viewport area (left of the panel)
        int viewportWidth = screenWidth - UIRenderer.PanelWidth;
        camera = new Camera2D
        {
            Target = new Vector2(cx, cy),
            Offset = new Vector2(viewportWidth / 2f, screenHeight / 2f),
            Rotation = 0.0f,
            Zoom = 0.3f
        };
    }

    /// <summary>
    /// Handles camera movement, selection input, and display toggles.
    /// Call once per frame before Render.
    /// </summary>
    public void HandleInput(List<Agent> agents, (int X, int Y)? spawnCenter, int currentTick = 0)
    {
        // GDD v1.7.2: Dynamic screen dimensions for resizable window
        screenWidth = Rl.GetScreenWidth();
        screenHeight = Rl.GetScreenHeight();
        int viewportWidth = screenWidth - UIRenderer.PanelWidth;
        camera.Offset = new Vector2(viewportWidth / 2f, screenHeight / 2f);

        // GDD v1.8 Section 8: Tech tree overlay captures all input when active
        if (ShowTechTree)
        {
            if (techTreeRenderer.HandleInput(screenWidth, screenHeight))
                ShowTechTree = false;
            return; // Don't process camera/selection while tech tree is open
        }

        // T key — toggle tech tree
        if (Rl.IsKeyPressed(KeyboardKey.T))
        {
            ShowTechTree = true;
            return;
        }

        // ── Camera ──────────────────────────────────────────────────
        // Mouse wheel zoom toward mouse position (BUG-01 fix: preserve point under cursor)
        float wheel = Rl.GetMouseWheelMove();
        if (wheel != 0)
        {
            var mouseScreen = Rl.GetMousePosition();
            var beforeZoom = Rl.GetScreenToWorld2D(mouseScreen, camera);
            camera.Zoom += wheel * 0.1f;
            camera.Zoom = Math.Clamp(camera.Zoom, 0.08f, 4.0f);
            var afterZoom = Rl.GetScreenToWorld2D(mouseScreen, camera);
            camera.Target += beforeZoom - afterZoom;
        }

        // Pan with middle mouse button
        if (Rl.IsMouseButtonDown(MouseButton.Middle))
        {
            Vector2 delta = Rl.GetMouseDelta();
            delta = Vector2.Negate(delta);
            delta = Vector2.Divide(delta, camera.Zoom);
            camera.Target = Vector2.Add(camera.Target, delta);
        }

        // Pan with WASD
        float panSpeed = 10.0f / camera.Zoom;
        Vector2 movement = Vector2.Zero;
        if (Rl.IsKeyDown(KeyboardKey.W)) movement.Y -= panSpeed;
        if (Rl.IsKeyDown(KeyboardKey.S)) movement.Y += panSpeed;
        if (Rl.IsKeyDown(KeyboardKey.A)) movement.X -= panSpeed;
        if (Rl.IsKeyDown(KeyboardKey.D)) movement.X += panSpeed;
        if (movement != Vector2.Zero)
        {
            camera.Target = Vector2.Add(camera.Target, movement);
            FollowMode = false;
        }

        // Reset camera (R)
        if (Rl.IsKeyPressed(KeyboardKey.R))
        {
            if (spawnCenter.HasValue)
            {
                camera.Target = new Vector2(
                    spawnCenter.Value.X * tileSize + tileSize / 2f,
                    spawnCenter.Value.Y * tileSize + tileSize / 2f);
            }
            else
            {
                camera.Target = new Vector2(world.Width * tileSize / 2f, world.Height * tileSize / 2f);
            }
            camera.Zoom = 0.3f;
        }

        // ── Selection ───────────────────────────────────────────────
        // Left click — select agent or tile, or click roster row
        if (Rl.IsMouseButtonPressed(MouseButton.Left))
        {
            Vector2 mousePos = Rl.GetMousePosition();
            if (mousePos.X < screenWidth - UIRenderer.PanelWidth)
            {
                // Click in world area — select agent or tile
                Vector2 worldPos = Rl.GetScreenToWorld2D(mousePos, camera);
                int tileX = (int)(worldPos.X / tileSize);
                int tileY = (int)(worldPos.Y / tileSize);

                if (world.IsInBounds(tileX, tileY))
                {
                    var agentsAtTile = world.GetAgentsAt(tileX, tileY);
                    if (agentsAtTile.Count > 0)
                    {
                        SelectedAgent = agentsAtTile[0];
                        SelectedTile = null;
                        expandedAgentId = agentsAtTile[0].Id;
                    }
                    else
                    {
                        SelectedAgent = null;
                        SelectedTile = (tileX, tileY);
                        expandedAgentId = null;
                    }
                }
            }
            else if (mousePos.Y > screenHeight / 2)
            {
                // Click in roster area — toggle expand/collapse
                int clickY = (int)mousePos.Y;
                bool hitAny = false;
                foreach (var (agentId, yStart, yEnd) in uiRenderer.RosterHitZones)
                {
                    if (clickY >= yStart && clickY < yEnd)
                    {
                        if (expandedAgentId == agentId)
                            expandedAgentId = null; // collapse
                        else
                        {
                            expandedAgentId = agentId;
                            SelectedAgent = agents.FirstOrDefault(a => a.Id == agentId);
                            SelectedTile = null;
                        }
                        hitAny = true;
                        break;
                    }
                }
                if (!hitAny)
                    expandedAgentId = null;
            }
        }

        // Tab — cycle through alive agents
        if (Rl.IsKeyPressed(KeyboardKey.Tab))
        {
            var aliveAgents = agents.Where(a => a.IsAlive).ToList();
            if (aliveAgents.Count > 0)
            {
                tabIndex = (tabIndex + 1) % aliveAgents.Count;
                SelectedAgent = aliveAgents[tabIndex];
                SelectedTile = null;
                expandedAgentId = SelectedAgent.Id;
                camera.Target = new Vector2(
                    SelectedAgent.X * tileSize + tileSize / 2f,
                    SelectedAgent.Y * tileSize + tileSize / 2f);
            }
        }

        // Escape — deselect
        if (Rl.IsKeyPressed(KeyboardKey.Escape))
        {
            SelectedAgent = null;
            SelectedTile = null;
            FollowMode = false;
            tabIndex = -1;
            expandedAgentId = null;
        }

        // F — toggle follow mode
        if (Rl.IsKeyPressed(KeyboardKey.F))
            FollowMode = !FollowMode;

        // Follow mode — camera tracks selected agent (using interpolated visual position)
        if (FollowMode && SelectedAgent != null && SelectedAgent.IsAlive)
        {
            var visualPos = agentRenderer.GetVisualPosition(SelectedAgent.Id, tileSize);
            if (visualPos != Vector2.Zero)
                camera.Target = visualPos + new Vector2(tileSize / 2f, tileSize / 2f);
            else
                camera.Target = new Vector2(
                    SelectedAgent.X * tileSize + tileSize / 2f,
                    SelectedAgent.Y * tileSize + tileSize / 2f);
        }

        // GDD v1.7: Keep dead agents selectable for DeathReportDuration (120 ticks) after death
        if (SelectedAgent != null && !SelectedAgent.IsAlive)
        {
            FollowMode = false; // Don't follow dead agents

            if (SelectedAgent.DeathTick >= 0 && currentTick - SelectedAgent.DeathTick <= SimConfig.DeathReportDuration)
            {
                // Still within death report window — keep selected
            }
            else
            {
                // Death report expired or no death tracking — deselect
                SelectedAgent = null;
            }
        }

        // ── Display toggles ────────────────────────────────────────
        if (Rl.IsKeyPressed(KeyboardKey.G))
            ShowGrid = !ShowGrid;

        if (Rl.IsKeyPressed(KeyboardKey.L))
        {
            EventFilter = EventFilter switch
            {
                EventFilterLevel.High => EventFilterLevel.Medium,
                EventFilterLevel.Medium => EventFilterLevel.All,
                _ => EventFilterLevel.High
            };
        }

        if (Rl.IsKeyPressed(KeyboardKey.K))
            ShowDiscoveryPanel = !ShowDiscoveryPanel;
    }

    /// <summary>
    /// Processes events from the latest sim tick for notifications and effects.
    /// </summary>
    public void ProcessTickEvents(List<SimulationEvent> tickEvents, List<Agent> agents)
    {
        notificationManager.ProcessTickEvents(tickEvents);
        effectManager.ProcessTickEvents(tickEvents, agents, tileSize);
    }

    /// <summary>
    /// Main render method. Call between BeginDrawing/EndDrawing.
    /// </summary>
    public void Render(List<Agent> agents, SimulationStats stats,
                       IReadOnlyList<SimulationEvent> recentEvents,
                       float ticksPerSecond, bool isPaused, float deltaTime,
                       int peakPopulation)
    {
        elapsedTime += deltaTime;
        notificationManager.Update(deltaTime);
        effectManager.Update(deltaTime);

        // ── World-space rendering ───────────────────────────────────
        Rl.BeginMode2D(camera);

        worldRenderer.Render(world, camera, tileSize, ShowGrid, screenWidth, screenHeight, spriteBatch);

        animalRenderer.Render(world, camera, tileSize, screenWidth, screenHeight, stats.CurrentTick, LerpAlpha);

        if (SelectedTile.HasValue)
            uiRenderer.RenderTileSelection(SelectedTile.Value.X, SelectedTile.Value.Y, tileSize);

        agentRenderer.Render(agents, world, camera, tileSize, SelectedAgent, elapsedTime, LerpAlpha, spriteBatch);

        // Path line for selected agent (drawn after agents so it's visible on top)
        // Pass world so the renderer can compute display-only A* paths for greedy-movement goals
        agentRenderer.RenderPathLine(SelectedAgent, tileSize, LerpAlpha, world);

        // Fix 5: Hunt/chase line from selected agent to target animal (dark red dashed)
        agentRenderer.RenderHuntLine(SelectedAgent, world, tileSize, LerpAlpha);

        // Fix 5: Health bars on animals targeted in combat
        animalRenderer.RenderCombatHealthBars(world, agents, tileSize, camera);

        effectManager.Render();

        Rl.EndMode2D();

        // ── Screen-space rendering ──────────────────────────────────
        agentRenderer.RenderOverflowBadges(camera);
        agentRenderer.RenderLabels(agents, camera, tileSize, SelectedAgent);

        // Clip UI panel rendering to prevent text overflow past screen edge
        int panelX = screenWidth - UIRenderer.PanelWidth;
        Rl.BeginScissorMode(panelX, 0, UIRenderer.PanelWidth, screenHeight);

        uiRenderer.RenderStatsPanel(screenWidth, screenHeight, stats, ticksPerSecond,
                                     peakPopulation, world, EventFilter);

        // Agent roster (always visible) — shows all alive agents with expand/collapse
        uiRenderer.RenderAgentRoster(screenWidth, screenHeight, agents, world, expandedAgentId);

        // Tile or death report detail (only when no alive agents selected)
        if (SelectedAgent == null || !SelectedAgent.IsAlive)
            uiRenderer.RenderSelectionDetail(screenWidth, screenHeight, SelectedAgent, SelectedTile, world, agents);

        if (ShowDiscoveryPanel)
        {
            uiRenderer.RenderDiscoveryPanel(screenWidth, screenHeight, stats.DiscoveredKnowledge);
        }

        Rl.EndScissorMode();

        uiRenderer.RenderEventLog(screenHeight, recentEvents.ToList(), EventFilter);

        uiRenderer.RenderControls();

        notificationManager.Render(screenWidth - UIRenderer.PanelWidth);

        // GDD v1.8 Section 8: Tech tree overlay
        if (ShowTechTree)
        {
            techTreeRenderer.Render(screenWidth, screenHeight, stats.DiscoveredKnowledge);
        }

        if (isPaused)
        {
            int textWidth = Rl.MeasureText("PAUSED", 40);
            int viewportCenterX = (screenWidth - UIRenderer.PanelWidth) / 2;
            Rl.DrawText("PAUSED", viewportCenterX - textWidth / 2, screenHeight / 2 - 20, 40, Color.Yellow);
        }
    }

    public void Dispose()
    {
        resourceRegistry.Dispose();
        structureRegistry.Dispose();
        animalRenderer.Dispose();
        atlas.Dispose();
    }
}
