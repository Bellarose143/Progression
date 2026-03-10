using System.Numerics;
using Raylib_cs;
using CivSim.Core;
using Rl = Raylib_cs.Raylib;

namespace CivSim.Raylib.Rendering;

/// <summary>
/// GDD v1.8 Section 8: Full-screen tech tree visualization overlay.
/// Toggle with T key. Shows all 44 recipes in a branch×era grid layout.
/// Node states: Discovered (branch color), Available (dimmed), Locked (gray).
/// Scrollable and zoomable. Click for detail panel.
/// </summary>
public class TechTreeRenderer
{
    // Layout constants
    private const int NodeWidth = 140;
    private const int NodeHeight = 44;
    private const int BranchSpacing = 170;
    private const int EraSpacing = 72;
    private const int Padding = 50;
    private const int HeaderHeight = 40;

    // Branch configuration
    private static readonly string[] Branches = { "Tools", "Fire", "Food", "Shelter", "Knowledge" };
    private static readonly string[] EraNames =
    {
        "Era 0: Innate", "Era 1: Survival", "Era 2: Adaptation", "Era 3: Settlement",
        "Era 4: Community", "Era 5: Specialization", "Era 6: Industry", "Era 7: Civilization"
    };

    private static readonly Dictionary<string, Color> BranchColors = new()
    {
        { "Tools", new Color(200, 150, 50, 255) },
        { "Fire", new Color(255, 100, 50, 255) },
        { "Food", new Color(100, 200, 50, 255) },
        { "Shelter", new Color(150, 150, 200, 255) },
        { "Knowledge", new Color(0, 255, 255, 255) }
    };

    // State
    private Vector2 scrollOffset;
    private float zoom = 1.0f;
    private string? selectedRecipeId;
    private bool isDragging;
    private Vector2 dragStart;
    private Vector2 dragScrollStart;

    // Pre-computed node positions (recalculated on layout)
    private Dictionary<string, Vector2> nodePositions = new();
    private bool layoutComputed;

    /// <summary>
    /// Computes layout positions for all recipe nodes.
    /// Branch columns left-to-right, era rows top-to-bottom.
    /// Multiple recipes in the same branch+era are stacked vertically.
    /// </summary>
    private void ComputeLayout()
    {
        nodePositions.Clear();

        // Track how many nodes are already placed in each (branch, era) cell
        var cellCounts = new Dictionary<(string, int), int>();

        foreach (var recipe in RecipeRegistry.AllRecipes)
        {
            int branchIdx = Array.IndexOf(Branches, recipe.Branch);
            if (branchIdx < 0) branchIdx = Branches.Length - 1; // Fallback

            var cellKey = (recipe.Branch, recipe.Tier);
            int cellOffset = cellCounts.GetValueOrDefault(cellKey, 0);
            cellCounts[cellKey] = cellOffset + 1;

            float x = Padding + 100 + branchIdx * BranchSpacing;
            float y = Padding + HeaderHeight + recipe.Tier * EraSpacing + cellOffset * (NodeHeight + 4);

            nodePositions[recipe.Id] = new Vector2(x, y);
        }

        layoutComputed = true;
    }

    /// <summary>
    /// Handles input for the tech tree overlay.
    /// Returns true if the overlay should be closed (Escape or T pressed).
    /// </summary>
    public bool HandleInput(int screenWidth, int screenHeight)
    {
        // Close on Escape or T
        if (Rl.IsKeyPressed(KeyboardKey.Escape) || Rl.IsKeyPressed(KeyboardKey.T))
            return true;

        // Mouse wheel zoom
        float wheel = Rl.GetMouseWheelMove();
        if (wheel != 0)
        {
            float oldZoom = zoom;
            zoom = Math.Clamp(zoom + wheel * 0.1f, 0.5f, 2.0f);

            // Zoom toward mouse position
            Vector2 mousePos = Rl.GetMousePosition();
            float zoomRatio = zoom / oldZoom;
            scrollOffset = mousePos - (mousePos - scrollOffset) * zoomRatio;
        }

        // Middle mouse or left-click drag on empty area for panning
        if (Rl.IsMouseButtonPressed(MouseButton.Middle))
        {
            isDragging = true;
            dragStart = Rl.GetMousePosition();
            dragScrollStart = scrollOffset;
        }
        if (Rl.IsMouseButtonReleased(MouseButton.Middle))
            isDragging = false;

        if (isDragging)
        {
            Vector2 delta = Rl.GetMousePosition() - dragStart;
            scrollOffset = dragScrollStart + delta;
        }

        // Left click — select/deselect node
        if (Rl.IsMouseButtonPressed(MouseButton.Left))
        {
            Vector2 mousePos = Rl.GetMousePosition();
            string? clickedNode = null;

            foreach (var kvp in nodePositions)
            {
                Vector2 nodePos = kvp.Value * zoom + scrollOffset;
                float nw = NodeWidth * zoom;
                float nh = NodeHeight * zoom;

                if (mousePos.X >= nodePos.X && mousePos.X <= nodePos.X + nw &&
                    mousePos.Y >= nodePos.Y && mousePos.Y <= nodePos.Y + nh)
                {
                    clickedNode = kvp.Key;
                    break;
                }
            }

            selectedRecipeId = (clickedNode == selectedRecipeId) ? null : clickedNode;
        }

        return false;
    }

    /// <summary>
    /// Renders the full tech tree overlay.
    /// </summary>
    public void Render(int screenWidth, int screenHeight, HashSet<string> discovered)
    {
        if (!layoutComputed) ComputeLayout();

        // Semi-transparent dark overlay
        Rl.DrawRectangle(0, 0, screenWidth, screenHeight, new Color(10, 10, 20, 220));

        // Title
        string title = "Technology Tree";
        int titleW = Rl.MeasureText(title, 24);
        Rl.DrawText(title, screenWidth / 2 - titleW / 2, 8, 24, Color.White);

        // Count
        int discoveredCount = 0;
        int totalExperimentable = 0;
        foreach (var r in RecipeRegistry.AllRecipes)
        {
            if (r.BaseChance > 0) totalExperimentable++;
            if (discovered.Contains(r.Output)) discoveredCount++;
        }
        string countText = $"{discoveredCount}/{totalExperimentable + 1} discovered"; // +1 for clothing
        int countW = Rl.MeasureText(countText, 12);
        Rl.DrawText(countText, screenWidth / 2 - countW / 2, 34, 12, Color.Gray);

        // Draw era labels (left side)
        for (int era = 0; era < EraNames.Length; era++)
        {
            float y = (Padding + HeaderHeight + era * EraSpacing) * zoom + scrollOffset.Y;
            if (y > -30 && y < screenHeight + 30)
            {
                Rl.DrawText(EraNames[era], 6, (int)y + 4, (int)(11 * zoom), new Color(120, 120, 120, 255));
            }
        }

        // Draw branch headers (top)
        for (int b = 0; b < Branches.Length; b++)
        {
            float x = (Padding + 100 + b * BranchSpacing) * zoom + scrollOffset.X;
            float y = Padding * zoom + scrollOffset.Y;
            if (x > -NodeWidth && x < screenWidth + NodeWidth)
            {
                var color = BranchColors.GetValueOrDefault(Branches[b], Color.White);
                Rl.DrawText(Branches[b], (int)x, (int)y, (int)(14 * zoom), color);
            }
        }

        // Draw prerequisite edges first (behind nodes)
        DrawEdges(screenWidth, screenHeight, discovered);

        // Draw nodes
        foreach (var recipe in RecipeRegistry.AllRecipes)
        {
            if (!nodePositions.ContainsKey(recipe.Id)) continue;
            DrawNode(recipe, screenWidth, screenHeight, discovered);
        }

        // Draw detail panel for selected node
        if (selectedRecipeId != null)
        {
            var selectedRecipe = RecipeRegistry.AllRecipes.FirstOrDefault(r => r.Id == selectedRecipeId);
            if (selectedRecipe != null)
            {
                DrawDetailPanel(selectedRecipe, screenWidth, screenHeight, discovered);
            }
        }

        // Controls hint
        Rl.DrawText("T/Esc: Close  |  Scroll: Zoom  |  Middle-drag: Pan  |  Click: Details", 10, screenHeight - 20, 11, Color.Gray);
    }

    /// <summary>Draws prerequisite connection lines between nodes.</summary>
    private void DrawEdges(int screenWidth, int screenHeight, HashSet<string> discovered)
    {
        foreach (var recipe in RecipeRegistry.AllRecipes)
        {
            if (!nodePositions.ContainsKey(recipe.Id)) continue;
            Vector2 toPos = nodePositions[recipe.Id] * zoom + scrollOffset;
            Vector2 toCenter = new(toPos.X + NodeWidth * zoom * 0.5f, toPos.Y);

            foreach (var prereq in recipe.RequiredKnowledge)
            {
                if (!nodePositions.ContainsKey(prereq)) continue;
                Vector2 fromPos = nodePositions[prereq] * zoom + scrollOffset;
                Vector2 fromCenter = new(fromPos.X + NodeWidth * zoom * 0.5f, fromPos.Y + NodeHeight * zoom);

                // Determine edge color based on state
                bool fromDiscovered = discovered.Contains(prereq);
                bool toDiscovered = discovered.Contains(recipe.Output);
                Color edgeColor;
                if (toDiscovered)
                    edgeColor = new Color(80, 180, 80, 150); // Green — complete
                else if (fromDiscovered)
                    edgeColor = new Color(180, 180, 80, 120); // Yellow — available
                else
                    edgeColor = new Color(60, 60, 60, 80); // Gray — locked

                Rl.DrawLineEx(fromCenter, toCenter, 1.5f * zoom, edgeColor);
            }
        }
    }

    /// <summary>Draws a single recipe node.</summary>
    private void DrawNode(Recipe recipe, int screenWidth, int screenHeight, HashSet<string> discovered)
    {
        Vector2 pos = nodePositions[recipe.Id] * zoom + scrollOffset;
        float nw = NodeWidth * zoom;
        float nh = NodeHeight * zoom;

        // Frustum cull
        if (pos.X > screenWidth || pos.X + nw < 0 || pos.Y > screenHeight || pos.Y + nh < 0)
            return;

        bool isDiscovered = discovered.Contains(recipe.Output);
        bool isAvailable = !isDiscovered && ArePrereqsMet(recipe, discovered);
        bool isSelected = recipe.Id == selectedRecipeId;

        Color branchColor = BranchColors.GetValueOrDefault(recipe.Branch, Color.White);

        if (isDiscovered)
        {
            // Filled with branch color
            Rl.DrawRectangle((int)pos.X, (int)pos.Y, (int)nw, (int)nh,
                new Color((int)branchColor.R, (int)branchColor.G, (int)branchColor.B, 180));
            Rl.DrawRectangleLines((int)pos.X, (int)pos.Y, (int)nw, (int)nh, branchColor);
        }
        else if (isAvailable)
        {
            // Dimmed border with slight glow
            Rl.DrawRectangle((int)pos.X, (int)pos.Y, (int)nw, (int)nh,
                new Color((int)branchColor.R / 4, (int)branchColor.G / 4, (int)branchColor.B / 4, 120));
            Rl.DrawRectangleLines((int)pos.X, (int)pos.Y, (int)nw, (int)nh,
                new Color((int)branchColor.R, (int)branchColor.G, (int)branchColor.B, 150));
        }
        else
        {
            // Locked — gray
            Rl.DrawRectangle((int)pos.X, (int)pos.Y, (int)nw, (int)nh, new Color(30, 30, 35, 150));
            Rl.DrawRectangleLines((int)pos.X, (int)pos.Y, (int)nw, (int)nh, new Color(60, 60, 65, 120));
        }

        // Selection highlight
        if (isSelected)
        {
            Rl.DrawRectangleLines((int)pos.X - 1, (int)pos.Y - 1, (int)nw + 2, (int)nh + 2, Color.White);
        }

        // Milestone indicator
        if (recipe.AnnouncementLevel == "MILESTONE")
        {
            int starSize = (int)(8 * zoom);
            Rl.DrawText("★", (int)(pos.X + nw - starSize - 3), (int)(pos.Y + 2), starSize,
                isDiscovered ? Color.Yellow : new Color(80, 80, 40, 150));
        }

        // Node text
        int fontSize = Math.Max(8, (int)(11 * zoom));
        Color textColor = isDiscovered ? Color.White
            : isAvailable ? new Color(200, 200, 200, 200)
            : new Color(100, 100, 100, 150);

        // Truncate name if too wide
        string displayName = recipe.Name;
        int textW = Rl.MeasureText(displayName, fontSize);
        int maxTextW = (int)(nw - 8);
        if (textW > maxTextW && displayName.Length > 3)
        {
            while (textW > maxTextW && displayName.Length > 3)
            {
                displayName = displayName[..^1];
                textW = Rl.MeasureText(displayName + "..", fontSize);
            }
            displayName += "..";
        }

        Rl.DrawText(displayName, (int)(pos.X + 4), (int)(pos.Y + 4), fontSize, textColor);

        // Era label (small, below name)
        int eraFontSize = Math.Max(7, (int)(9 * zoom));
        string eraLabel = $"Era {recipe.Tier}";
        Color eraColor = isDiscovered ? new Color(200, 200, 200, 180) : new Color(80, 80, 80, 120);
        Rl.DrawText(eraLabel, (int)(pos.X + 4), (int)(pos.Y + nh - eraFontSize - 4), eraFontSize, eraColor);
    }

    /// <summary>Draws a detail panel for the selected recipe.</summary>
    private void DrawDetailPanel(Recipe recipe, int screenWidth, int screenHeight, HashSet<string> discovered)
    {
        int panelW = 280;
        int panelH = 320;
        int panelX = screenWidth - panelW - 10;
        int panelY = 50;

        // Background
        Rl.DrawRectangle(panelX, panelY, panelW, panelH, new Color(15, 15, 25, 240));
        Color branchColor = BranchColors.GetValueOrDefault(recipe.Branch, Color.White);
        Rl.DrawRectangleLines(panelX, panelY, panelW, panelH, branchColor);

        int x = panelX + 10;
        int y = panelY + 10;

        // Name
        Rl.DrawText(recipe.Name, x, y, 18, branchColor);
        y += 22;

        // Status
        bool isDiscovered = discovered.Contains(recipe.Output);
        string status = isDiscovered ? "[DISCOVERED]" : ArePrereqsMet(recipe, discovered) ? "[AVAILABLE]" : "[LOCKED]";
        Color statusColor = isDiscovered ? Color.Green : ArePrereqsMet(recipe, discovered) ? Color.Yellow : Color.Gray;
        Rl.DrawText(status, x, y, 12, statusColor);
        y += 18;

        // Era and Branch
        Rl.DrawText($"Era {recipe.Tier} — {recipe.Branch}", x, y, 12, Color.Gray);
        y += 18;

        if (recipe.AnnouncementLevel == "MILESTONE")
        {
            Rl.DrawText("★ MILESTONE", x, y, 12, Color.Yellow);
            y += 18;
        }

        // Divider
        Rl.DrawLine(x, y, x + panelW - 20, y, new Color(60, 60, 80, 255));
        y += 8;

        // Description
        Rl.DrawText(recipe.Description, x, y, 11, new Color(180, 180, 180, 255));
        y += 20;

        // Inputs
        if (recipe.RequiredResources.Count > 0)
        {
            Rl.DrawText("Inputs:", x, y, 12, Color.White);
            y += 14;
            foreach (var kvp in recipe.RequiredResources)
            {
                Rl.DrawText($"  {kvp.Value}x {kvp.Key}", x, y, 11, new Color(180, 180, 180, 255));
                y += 13;
            }
            y += 4;
        }
        else if (recipe.BaseChance > 0)
        {
            Rl.DrawText("Inputs: None", x, y, 12, new Color(120, 120, 120, 255));
            y += 18;
        }

        // Prerequisites
        if (recipe.RequiredKnowledge.Count > 0)
        {
            Rl.DrawText("Requires:", x, y, 12, Color.White);
            y += 14;
            foreach (var req in recipe.RequiredKnowledge)
            {
                bool met = discovered.Contains(req);
                var reqRecipe = RecipeRegistry.AllRecipes.FirstOrDefault(r => r.Id == req);
                string reqName = reqRecipe?.Name ?? req;
                Color reqColor = met ? Color.Green : new Color(180, 80, 80, 255);
                Rl.DrawText($"  {(met ? "✓" : "✗")} {reqName}", x, y, 11, reqColor);
                y += 13;
            }
            y += 4;
        }

        // Base chance
        if (recipe.BaseChance > 0)
        {
            Rl.DrawText($"Discovery chance: {recipe.BaseChance:P0}", x, y, 11, new Color(150, 150, 150, 255));
            y += 16;
        }
        else
        {
            string trigger = recipe.Id == "clothing" ? "Innate (known at birth)" : "Auto-triggers";
            Rl.DrawText(trigger, x, y, 11, new Color(150, 150, 150, 255));
            y += 16;
        }

        // Effects
        if (recipe.Effects.Count > 0 && y < panelY + panelH - 30)
        {
            Rl.DrawText("Effects:", x, y, 12, Color.White);
            y += 14;
            foreach (var effect in recipe.Effects)
            {
                if (y >= panelY + panelH - 15) break;
                Rl.DrawText($"  • {effect}", x, y, 10, new Color(160, 160, 160, 255));
                y += 12;
            }
        }
    }

    /// <summary>Checks if all prerequisites for a recipe are met.</summary>
    private static bool ArePrereqsMet(Recipe recipe, HashSet<string> discovered)
    {
        if (recipe.RequiredKnowledge.Count == 0) return true;
        foreach (var req in recipe.RequiredKnowledge)
        {
            if (!discovered.Contains(req)) return false;
        }
        return true;
    }
}
