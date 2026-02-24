using Raylib_cs;
using System.Numerics;
using Rl = Raylib_cs.Raylib;

namespace CivSim.Raylib.Rendering;

/// <summary>
/// All procedural sprite drawing functions. Coordinates are world-space pixels (inside BeginMode2D).
/// </summary>
public static class ProceduralSprites
{
    // ── Hash utility ─────────────────────────────────────────────────
    public static uint Hash(uint a, uint b)
    {
        uint h = a ^ (b * 2654435761u);
        h ^= h >> 16;
        h *= 0x85ebca6bu;
        h ^= h >> 13;
        h *= 0xc2b2ae35u;
        h ^= h >> 16;
        return h;
    }

    // ── Biome Backgrounds ────────────────────────────────────────────

    public static void DrawForestBackground(int px, int py, int size, uint seed)
    {
        Rl.DrawRectangle(px, py, size, size, new Color(34, 100, 34, 255));
        for (int i = 0; i < 8; i++)
        {
            uint h = Hash(seed, (uint)i);
            int dx = (int)(h % (uint)(size - 6)) + 3;
            int dy = (int)((h >> 8) % (uint)(size - 6)) + 3;
            int r = 2 + (int)((h >> 16) % 3);
            Rl.DrawCircle(px + dx, py + dy, r, new Color(20, 70 + (int)((h >> 4) % 30), 20, 100));
        }
    }

    public static void DrawPlainsBackground(int px, int py, int size, uint seed)
    {
        Rl.DrawRectangle(px, py, size, size, new Color(180, 170, 60, 255));
        for (int i = 0; i < 5; i++)
        {
            uint h = Hash(seed, (uint)(i + 100));
            int ly = py + (int)(h % (uint)(size - 4)) + 2;
            int lx1 = px + (int)((h >> 8) % (uint)(size / 2));
            int lx2 = lx1 + (int)((h >> 16) % (uint)(size / 3)) + 8;
            Rl.DrawLineEx(new Vector2(lx1, ly), new Vector2(Math.Min(lx2, px + size - 2), ly),
                1, new Color(200, 190, 80, 80));
        }
    }

    public static void DrawMountainBackground(int px, int py, int size, uint seed)
    {
        Rl.DrawRectangle(px, py, size, size, new Color(120, 115, 110, 255));
        for (int i = 0; i < 3; i++)
        {
            uint h = Hash(seed, (uint)(i + 200));
            int cx = px + (int)(h % (uint)(size - 16)) + 8;
            int cy = py + (int)((h >> 8) % (uint)(size - 16)) + 8;
            int s = 4 + (int)((h >> 16) % 4);
            Rl.DrawTriangle(
                new Vector2(cx, cy - s),
                new Vector2(cx + s, cy + s),
                new Vector2(cx - s, cy + s),
                new Color(100, 95, 90, 120));
        }
    }

    public static void DrawWaterBackground(int px, int py, int size, uint seed)
    {
        Rl.DrawRectangle(px, py, size, size, new Color(40, 80, 180, 255));
        for (int i = 0; i < 4; i++)
        {
            uint h = Hash(seed, (uint)(i + 300));
            int ly = py + (int)(h % (uint)(size - 8)) + 4;
            for (int j = 4; j < size - 12; j += 8)
            {
                int y1 = ly + (j % 16 < 8 ? -2 : 2);
                int y2 = ly + ((j + 8) % 16 < 8 ? -2 : 2);
                Rl.DrawLineEx(
                    new Vector2(px + j, y1), new Vector2(px + j + 8, y2),
                    1, new Color(80, 140, 220, 100));
            }
        }
    }

    public static void DrawDesertBackground(int px, int py, int size, uint seed)
    {
        Rl.DrawRectangle(px, py, size, size, new Color(210, 185, 140, 255));
        for (int i = 0; i < 10; i++)
        {
            uint h = Hash(seed, (uint)(i + 400));
            int dx = (int)(h % (uint)(size - 4)) + 2;
            int dy = (int)((h >> 8) % (uint)(size - 4)) + 2;
            Rl.DrawCircle(px + dx, py + dy, 1, new Color(190, 165, 120, 80));
        }
    }

    // ── Resource Sprites ─────────────────────────────────────────────

    public static void DrawTree(int cx, int cy, float scale = 1.0f)
    {
        int trunkW = (int)(4 * scale);
        int trunkH = (int)(10 * scale);
        int canopyR = (int)(8 * scale);
        Rl.DrawRectangle(cx - trunkW / 2, cy, trunkW, trunkH, new Color(101, 67, 33, 255));
        Rl.DrawCircle(cx, cy - (int)(2 * scale), canopyR, new Color(34, 139, 34, 255));
    }

    public static void DrawBush(int cx, int cy, float scale = 1.0f)
    {
        int r = (int)(7 * scale);
        Rl.DrawCircle(cx, cy, r, new Color(50, 140, 50, 255));
        int dotR = Math.Max(1, (int)(2 * scale));
        Rl.DrawCircle(cx - (int)(3 * scale), cy - (int)(2 * scale), dotR, new Color(200, 30, 30, 255));
        Rl.DrawCircle(cx + (int)(3 * scale), cy + (int)(1 * scale), dotR, new Color(200, 30, 30, 255));
        Rl.DrawCircle(cx, cy + (int)(3 * scale), dotR, new Color(200, 30, 30, 255));
    }

    public static void DrawRock(int cx, int cy, float scale = 1.0f)
    {
        int r = (int)(8 * scale);
        Color fill = new Color(150, 145, 140, 255);
        Color outline = new Color(100, 95, 90, 255);

        // Hexagon as 4 triangles
        Vector2 top = new(cx, cy - r);
        Vector2 tl = new(cx - r, cy - r / 2);
        Vector2 bl = new(cx - r, cy + r / 2);
        Vector2 bot = new(cx, cy + r);
        Vector2 br = new(cx + r, cy + r / 2);
        Vector2 tr = new(cx + r, cy - r / 2);

        Rl.DrawTriangle(top, tr, tl, fill);
        Rl.DrawTriangle(tl, tr, bl, fill);
        Rl.DrawTriangle(bl, tr, br, fill);
        Rl.DrawTriangle(bl, br, bot, fill);

        Rl.DrawLineEx(top, tl, 1, outline);
        Rl.DrawLineEx(top, tr, 1, outline);
        Rl.DrawLineEx(tl, bl, 1, outline);
        Rl.DrawLineEx(tr, br, 1, outline);
        Rl.DrawLineEx(bl, bot, 1, outline);
        Rl.DrawLineEx(br, bot, 1, outline);
    }

    public static void DrawOreVein(int cx, int cy, float scale = 1.0f)
    {
        int r = (int)(7 * scale);
        Color fill = new Color(80, 80, 85, 255);

        Vector2 top = new(cx, cy - r);
        Vector2 tl = new(cx - r, cy - r / 2);
        Vector2 bl = new(cx - r, cy + r / 2);
        Vector2 bot = new(cx, cy + r);
        Vector2 br = new(cx + r, cy + r / 2);
        Vector2 tr = new(cx + r, cy - r / 2);

        Rl.DrawTriangle(top, tr, tl, fill);
        Rl.DrawTriangle(tl, tr, bl, fill);
        Rl.DrawTriangle(bl, tr, br, fill);
        Rl.DrawTriangle(bl, br, bot, fill);

        int dotR = Math.Max(1, (int)(2 * scale));
        Rl.DrawCircle(cx - (int)(2 * scale), cy - (int)(1 * scale), dotR, new Color(205, 127, 50, 255));
        Rl.DrawCircle(cx + (int)(3 * scale), cy + (int)(2 * scale), dotR, new Color(205, 127, 50, 255));
    }

    public static void DrawGrainStalks(int cx, int cy, float scale = 1.0f)
    {
        Color stalk = new Color(218, 165, 32, 255);
        Color head = new Color(240, 200, 60, 255);
        int h = (int)(12 * scale);
        int spread = (int)(5 * scale);
        int headR = Math.Max(1, (int)(2.5f * scale));

        for (int i = -1; i <= 1; i++)
        {
            int bx = cx + i * spread;
            int topX = bx + i * (int)(2 * scale);
            Rl.DrawLineEx(new Vector2(bx, cy + h / 2), new Vector2(topX, cy - h / 2), 1.5f, stalk);
            Rl.DrawCircle(topX, cy - h / 2, headR, head);
        }
    }

    public static void DrawFish(int cx, int cy, float scale = 1.0f)
    {
        int bodyW = (int)(10 * scale);
        int bodyH = (int)(5 * scale);
        Color body = new Color(70, 130, 200, 255);

        Rl.DrawEllipse(cx, cy, bodyW, bodyH, body);

        int tailX = cx + bodyW;
        Rl.DrawTriangle(
            new Vector2(tailX, cy - (int)(4 * scale)),
            new Vector2(tailX, cy + (int)(4 * scale)),
            new Vector2(tailX + (int)(5 * scale), cy),
            body);

        Rl.DrawCircle(cx - (int)(4 * scale), cy - (int)(1 * scale), Math.Max(1, (int)(1.5f * scale)), Color.White);
    }

    // ── Structures ───────────────────────────────────────────────────

    public static void DrawShelter(int px, int py, int size, bool completed)
    {
        byte alpha = completed ? (byte)255 : (byte)100;

        int wallW = size * 3 / 4;
        int wallH = size / 3;
        int wallX = px + (size - wallW) / 2;
        int wallY = py + size / 2;
        Rl.DrawRectangle(wallX, wallY, wallW, wallH, new Color(210, 190, 150, (int)alpha));

        int roofPeakY = py + size / 4;
        Rl.DrawTriangle(
            new Vector2(px + size / 2, roofPeakY),
            new Vector2(wallX + wallW + 2, wallY),
            new Vector2(wallX - 2, wallY),
            new Color(139, 90, 43, (int)alpha));

        if (completed)
        {
            int doorW = size / 8;
            int doorH = wallH * 2 / 3;
            Rl.DrawRectangle(px + size / 2 - doorW / 2, wallY + wallH - doorH, doorW, doorH,
                new Color(60, 40, 20, (int)alpha));
        }
    }

    public static void DrawFarm(int px, int py, int size)
    {
        Color furrow = new Color(101, 67, 33, 200);
        int lineCount = 5;
        int spacing = size / (lineCount + 1);
        for (int i = 1; i <= lineCount; i++)
        {
            int ly = py + i * spacing;
            Rl.DrawLineEx(new Vector2(px + 4, ly), new Vector2(px + size - 4, ly), 2, furrow);
        }
    }

    // ── Humanoid Agent ───────────────────────────────────────────────

    public static readonly Color[] AgentPalette =
    {
        new Color(220, 50, 50, 255),    // 01 Red
        new Color(50, 100, 220, 255),   // 02 Blue
        new Color(50, 180, 50, 255),    // 03 Green
        new Color(220, 190, 30, 255),   // 04 Yellow
        new Color(150, 50, 200, 255),   // 05 Purple
        new Color(230, 140, 30, 255),   // 06 Orange
        new Color(0, 180, 180, 255),    // 07 Cyan
        new Color(230, 100, 180, 255),  // 08 Pink
        new Color(130, 220, 30, 255),   // 09 Lime
        new Color(140, 100, 60, 255),   // 10 Brown
        new Color(240, 128, 80, 255),   // 11 Coral
        new Color(0, 160, 160, 255),    // 12 Teal
    };

    public static Color GetAgentColor(int agentId) => AgentPalette[agentId % AgentPalette.Length];

    /// <summary>
    /// Draws a humanoid figure. cx/cy = center bottom of figure.
    /// </summary>
    public static void DrawHumanoid(int cx, int cy, Color color, float scale = 1.0f)
    {
        int headR = Math.Max(2, (int)(4 * scale));
        int bodyW = Math.Max(4, (int)(8 * scale));
        int bodyH = Math.Max(4, (int)(10 * scale));
        int legW = Math.Max(2, (int)(3 * scale));
        int legH = Math.Max(3, (int)(6 * scale));

        // Legs
        Rl.DrawRectangle(cx - bodyW / 2, cy - legH, legW, legH, color);
        Rl.DrawRectangle(cx + bodyW / 2 - legW, cy - legH, legW, legH, color);

        // Body
        int bodyBottom = cy - legH;
        Rl.DrawRectangle(cx - bodyW / 2, bodyBottom - bodyH, bodyW, bodyH, color);

        // Head (skin tone)
        int headCY = bodyBottom - bodyH - headR;
        Rl.DrawCircle(cx, headCY, headR, new Color(230, 190, 150, 255));
    }

    /// <summary>Returns the total height of a humanoid figure at the given scale.</summary>
    public static int HumanoidHeight(float scale = 1.0f)
    {
        return (int)(4 * scale) + (int)(10 * scale) + (int)(6 * scale) + (int)(4 * scale);
    }

    // ── Activity Icons ───────────────────────────────────────────────

    public static void DrawAxeIcon(int cx, int cy)
    {
        Rl.DrawLineEx(new Vector2(cx - 4, cy + 4), new Vector2(cx + 3, cy - 3), 2, new Color(139, 90, 43, 255));
        Rl.DrawTriangle(
            new Vector2(cx + 3, cy - 6),
            new Vector2(cx + 6, cy - 3),
            new Vector2(cx, cy - 3),
            Color.Gray);
    }

    public static void DrawPickaxeIcon(int cx, int cy)
    {
        Rl.DrawLineEx(new Vector2(cx - 4, cy + 4), new Vector2(cx + 3, cy - 3), 2, new Color(139, 90, 43, 255));
        Rl.DrawLineEx(new Vector2(cx - 2, cy - 5), new Vector2(cx + 6, cy - 3), 2, Color.Gray);
    }

    public static void DrawBasketIcon(int cx, int cy)
    {
        Rl.DrawRectangle(cx - 4, cy - 2, 8, 6, new Color(180, 140, 80, 255));
        Rl.DrawLineEx(new Vector2(cx - 4, cy - 2), new Vector2(cx + 4, cy - 2), 1, new Color(140, 100, 50, 255));
    }

    public static void DrawHammerIcon(int cx, int cy)
    {
        Rl.DrawLineEx(new Vector2(cx, cy + 5), new Vector2(cx, cy - 2), 2, new Color(139, 90, 43, 255));
        Rl.DrawRectangle(cx - 4, cy - 5, 8, 3, Color.Gray);
    }

    public static void DrawHeartIcon(int cx, int cy, Color? color = null)
    {
        Color c = color ?? new Color(255, 100, 130, 255);
        Rl.DrawCircle(cx - 3, cy - 2, 3, c);
        Rl.DrawCircle(cx + 3, cy - 2, 3, c);
        Rl.DrawTriangle(
            new Vector2(cx, cy + 5),
            new Vector2(cx - 6, cy - 1),
            new Vector2(cx + 6, cy - 1),
            c);
    }

    public static void DrawSpeechBubbleIcon(int cx, int cy)
    {
        Rl.DrawRectangleRounded(new Rectangle(cx - 6, cy - 6, 12, 8), 0.3f, 4, Color.White);
        Rl.DrawTriangle(
            new Vector2(cx - 2, cy + 2),
            new Vector2(cx + 2, cy + 2),
            new Vector2(cx - 3, cy + 5),
            Color.White);
    }

    public static void DrawQuestionMark(int cx, int cy)
    {
        int textW = Rl.MeasureText("?", 14);
        Rl.DrawText("?", cx - textW / 2, cy - 7, 14, Color.Yellow);
    }

    public static void DrawZzz(int cx, int cy)
    {
        Rl.DrawText("Zzz", cx - 6, cy - 5, 10, new Color(150, 150, 255, 200));
    }

    public static void DrawSkull(int cx, int cy, byte alpha = 255)
    {
        Color c = new Color(200, 50, 50, (int)alpha);
        Rl.DrawCircle(cx, cy - 3, 5, c);
        Rl.DrawRectangle(cx - 3, cy + 2, 6, 4, c);
        Rl.DrawCircle(cx - 2, cy - 4, 1, Color.Black);
        Rl.DrawCircle(cx + 2, cy - 4, 1, Color.Black);
    }
}
