using Raylib_cs;

namespace CivSim.Raylib.Rendering;

/// <summary>
/// PERF-01: Collects textured quads and flushes them via Rlgl in a single batch.
/// All sprites share one texture (spritesheet.png), making this an ideal batching scenario.
/// Uses collect-and-flush pattern: DrawQuad appends to a buffer, Flush submits all via Rlgl.
/// </summary>
public class SpriteBatch
{
    private struct QuadData
    {
        public Rectangle Src;
        public Rectangle Dest;
        public Color Tint;
    }

    private const int MaxQuads = 4096;
    private readonly QuadData[] _quads = new QuadData[MaxQuads];
    private int _count;
    private uint _textureId;
    private int _texWidth;
    private int _texHeight;

    /// <summary>Begins a new batch for the given texture. Resets the quad count.</summary>
    public void Begin(Texture2D texture)
    {
        _textureId = texture.Id;
        _texWidth = texture.Width;
        _texHeight = texture.Height;
        _count = 0;
    }

    /// <summary>Appends a textured quad to the batch. Auto-flushes when buffer is full.</summary>
    public void DrawQuad(Rectangle src, Rectangle dest, Color tint)
    {
        if (_count >= MaxQuads)
            Flush();

        _quads[_count++] = new QuadData { Src = src, Dest = dest, Tint = tint };
    }

    /// <summary>Submits all collected quads to the GPU via Rlgl and resets the buffer.</summary>
    public void Flush()
    {
        if (_count == 0) return;

        float invW = 1f / _texWidth;
        float invH = 1f / _texHeight;

        Rlgl.SetTexture(_textureId);
        Rlgl.Begin(DrawMode.Quads);

        for (int i = 0; i < _count; i++)
        {
            ref var q = ref _quads[i];

            float u0 = q.Src.X * invW;
            float v0 = q.Src.Y * invH;
            float u1 = (q.Src.X + q.Src.Width) * invW;
            float v1 = (q.Src.Y + q.Src.Height) * invH;

            float x0 = q.Dest.X;
            float y0 = q.Dest.Y;
            float x1 = q.Dest.X + q.Dest.Width;
            float y1 = q.Dest.Y + q.Dest.Height;

            byte r = q.Tint.R, g = q.Tint.G, b = q.Tint.B, a = q.Tint.A;

            // Top-left
            Rlgl.TexCoord2f(u0, v0);
            Rlgl.Color4ub(r, g, b, a);
            Rlgl.Vertex2f(x0, y0);

            // Bottom-left
            Rlgl.TexCoord2f(u0, v1);
            Rlgl.Color4ub(r, g, b, a);
            Rlgl.Vertex2f(x0, y1);

            // Bottom-right
            Rlgl.TexCoord2f(u1, v1);
            Rlgl.Color4ub(r, g, b, a);
            Rlgl.Vertex2f(x1, y1);

            // Top-right
            Rlgl.TexCoord2f(u1, v0);
            Rlgl.Color4ub(r, g, b, a);
            Rlgl.Vertex2f(x1, y0);
        }

        Rlgl.End();
        Rlgl.SetTexture(0);
        _count = 0;
    }
}
