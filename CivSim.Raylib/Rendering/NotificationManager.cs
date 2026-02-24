using Raylib_cs;
using CivSim.Core;
using Rl = Raylib_cs.Raylib;

namespace CivSim.Raylib.Rendering;

/// <summary>
/// Manages top-center notification banners for important events.
/// GDD v1.8 Section 9: MILESTONE discoveries get large centered popup overlay.
/// STANDARD discoveries get toast notifications (auto-dismiss after 3.5 seconds, max 3 stacked).
/// </summary>
public class NotificationManager
{
    private class Notification
    {
        public string Text;
        public Color AccentColor;
        public float TimeRemaining;
        public float Age; // for slide-in animation

        public Notification(string text, Color color)
        {
            Text = text;
            AccentColor = color;
            TimeRemaining = 3.5f;
            Age = 0f;
        }
    }

    /// <summary>GDD v1.8 Section 9: Large centered milestone popup.</summary>
    private class MilestonePopup
    {
        public string Title;
        public string Subtitle;
        public Color AccentColor;
        public float Duration;
        public float Age;

        public MilestonePopup(string title, string subtitle, Color color)
        {
            Title = title;
            Subtitle = subtitle;
            AccentColor = color;
            Duration = 6.0f;
            Age = 0f;
        }

        public float TimeRemaining => Duration - Age;
    }

    private readonly List<Notification> active = new();
    private MilestonePopup? activeMilestone;
    private const int MaxVisible = 3;
    private float burstCooldown;

    /// <summary>Branch colors for milestone display.</summary>
    private static readonly Dictionary<string, Color> BranchColors = new()
    {
        { "Tools", new Color(200, 150, 50, 255) },     // Gold
        { "Fire", new Color(255, 100, 50, 255) },      // Orange-red
        { "Food", new Color(100, 200, 50, 255) },      // Green
        { "Shelter", new Color(150, 150, 200, 255) },   // Blue-gray
        { "Knowledge", new Color(0, 255, 255, 255) }    // Cyan
    };

    public void ProcessTickEvents(List<SimulationEvent> events)
    {
        if (burstCooldown > 0) return;

        // Burst collapse: if too many events, summarize
        int births = 0, deaths = 0, discoveries = 0;
        foreach (var evt in events)
        {
            switch (evt.Type)
            {
                case EventType.Birth: births++; break;
                case EventType.Death: deaths++; break;
                case EventType.Discovery: discoveries++; break;
            }
        }

        int total = births + deaths + discoveries;
        if (total >= 10)
        {
            var parts = new List<string>();
            if (births > 0) parts.Add($"{births} births");
            if (deaths > 0) parts.Add($"{deaths} deaths");
            if (discoveries > 0) parts.Add($"{discoveries} discoveries");
            Add(string.Join(", ", parts) + " this tick", Color.Yellow);
            burstCooldown = 5.0f;
            return;
        }

        foreach (var evt in events)
        {
            switch (evt.Type)
            {
                case EventType.Birth:
                    Add(FormatBirth(evt.Message), Color.Green);
                    break;
                case EventType.Death:
                    Add(FormatDeath(evt.Message), Color.Red);
                    break;
                case EventType.Discovery:
                    HandleDiscovery(evt);
                    break;
                case EventType.Milestone:
                    Add(evt.Message, Color.Yellow);
                    break;
            }
        }
    }

    /// <summary>
    /// GDD v1.8 Section 9: Routes discovery events to either milestone popup or standard toast
    /// based on the recipe's AnnouncementLevel field.
    /// </summary>
    private void HandleDiscovery(SimulationEvent evt)
    {
        // Look up the recipe to check announcement level
        Recipe? recipe = null;
        if (evt.RecipeId != null)
        {
            recipe = RecipeRegistry.AllRecipes.FirstOrDefault(r => r.Id == evt.RecipeId);
        }

        if (recipe != null && recipe.AnnouncementLevel == "MILESTONE")
        {
            // Show large centered milestone popup
            var color = BranchColors.GetValueOrDefault(recipe.Branch, new Color(255, 215, 0, 255));
            string subtitle = recipe.Effects.Count > 0 ? recipe.Effects[0] : recipe.Description;
            activeMilestone = new MilestonePopup(
                $"{recipe.Name} Discovered!",
                subtitle,
                color
            );
        }
        else
        {
            // Standard toast notification
            Add(evt.Message, new Color(0, 255, 255, 255));
        }
    }

    public void Add(string text, Color color)
    {
        active.Add(new Notification(text, color));
        // Trim oldest if over max
        while (active.Count > MaxVisible + 2)
            active.RemoveAt(0);
    }

    public void Update(float deltaTime)
    {
        if (burstCooldown > 0)
            burstCooldown -= deltaTime;

        for (int i = active.Count - 1; i >= 0; i--)
        {
            active[i].TimeRemaining -= deltaTime;
            active[i].Age += deltaTime;
            if (active[i].TimeRemaining <= 0)
                active.RemoveAt(i);
        }

        // Update milestone popup
        if (activeMilestone != null)
        {
            activeMilestone.Age += deltaTime;
            if (activeMilestone.TimeRemaining <= 0)
                activeMilestone = null;
        }
    }

    public void Render(int screenWidth)
    {
        // Render standard toast notifications
        int visibleCount = Math.Min(active.Count, MaxVisible);
        int startIndex = Math.Max(0, active.Count - visibleCount);

        for (int i = 0; i < visibleCount; i++)
        {
            var notif = active[startIndex + i];
            int textW = Rl.MeasureText(notif.Text, 14);
            int boxW = textW + 24;
            int boxH = 28;
            int bx = screenWidth / 2 - boxW / 2;

            // Slide-in animation
            float slideT = Math.Min(notif.Age / 0.3f, 1.0f);
            int targetY = 10 + i * (boxH + 6);
            int by = (int)(-boxH + (targetY + boxH) * slideT);

            // Fade-out
            byte alpha = notif.TimeRemaining < 0.5f
                ? (byte)(255 * (notif.TimeRemaining / 0.5f))
                : (byte)255;

            // Background
            Rl.DrawRectangle(bx, by, boxW, boxH, new Color(20, 20, 30, (int)alpha * 220 / 255));
            // Accent bar
            Rl.DrawRectangle(bx, by, 4, boxH,
                new Color((int)notif.AccentColor.R, (int)notif.AccentColor.G, (int)notif.AccentColor.B, (int)alpha));
            // Text
            Rl.DrawText(notif.Text, bx + 12, by + 7, 14,
                new Color((int)notif.AccentColor.R, (int)notif.AccentColor.G, (int)notif.AccentColor.B, (int)alpha));
        }

        // GDD v1.8 Section 9: Render milestone popup overlay
        if (activeMilestone != null)
        {
            RenderMilestonePopup(screenWidth, activeMilestone);
        }
    }

    /// <summary>
    /// Renders a large centered milestone discovery popup with fade-in/out animation.
    /// </summary>
    private static void RenderMilestonePopup(int viewportWidth, MilestonePopup milestone)
    {
        int screenHeight = Rl.GetScreenHeight();
        float fadeIn = Math.Min(milestone.Age / 0.8f, 1.0f);
        float fadeOut = milestone.TimeRemaining < 1.0f ? milestone.TimeRemaining : 1.0f;
        float alpha = Math.Min(fadeIn, fadeOut);
        byte a = (byte)(255 * Math.Clamp(alpha, 0f, 1f));

        // Semi-transparent dark overlay
        Rl.DrawRectangle(0, 0, viewportWidth, screenHeight,
            new Color(0, 0, 0, (int)(a * 0.45f)));

        // Center the popup
        int centerX = viewportWidth / 2;
        int centerY = screenHeight / 2 - 30;

        // Title (large)
        int titleSize = 32;
        int titleW = Rl.MeasureText(milestone.Title, titleSize);

        // Background box
        int boxW = Math.Max(titleW + 60, 400);
        int boxH = 100;
        int boxX = centerX - boxW / 2;
        int boxY = centerY - boxH / 2;

        Rl.DrawRectangle(boxX, boxY, boxW, boxH, new Color(15, 15, 25, (int)(a * 0.9f)));
        Rl.DrawRectangleLines(boxX, boxY, boxW, boxH,
            new Color((int)milestone.AccentColor.R, (int)milestone.AccentColor.G,
                       (int)milestone.AccentColor.B, (int)a));

        // Accent bars (top and bottom)
        Rl.DrawRectangle(boxX, boxY, boxW, 3,
            new Color((int)milestone.AccentColor.R, (int)milestone.AccentColor.G,
                       (int)milestone.AccentColor.B, (int)a));
        Rl.DrawRectangle(boxX, boxY + boxH - 3, boxW, 3,
            new Color((int)milestone.AccentColor.R, (int)milestone.AccentColor.G,
                       (int)milestone.AccentColor.B, (int)a));

        // Title text
        Rl.DrawText(milestone.Title, centerX - titleW / 2, centerY - 20, titleSize,
            new Color((int)milestone.AccentColor.R, (int)milestone.AccentColor.G,
                       (int)milestone.AccentColor.B, (int)a));

        // Subtitle (smaller, below title)
        int subSize = 14;
        int subW = Rl.MeasureText(milestone.Subtitle, subSize);
        Rl.DrawText(milestone.Subtitle, centerX - subW / 2, centerY + 20, subSize,
            new Color(200, 200, 200, (int)a));
    }

    private static string FormatBirth(string msg)
    {
        // "Agent 5 (Agent_5) spawned at (30,32)" → "A child was born: Agent_5"
        int nameStart = msg.IndexOf('(');
        int nameEnd = msg.IndexOf(')');
        if (nameStart >= 0 && nameEnd > nameStart)
        {
            string name = msg.Substring(nameStart + 1, nameEnd - nameStart - 1);
            return $"A child was born: {name}";
        }
        return msg;
    }

    private static string FormatDeath(string msg)
    {
        // "Agent 5 died of starvation at age 45" → keep as-is, it's readable
        return msg;
    }
}
