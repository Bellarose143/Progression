namespace CivSim.Core;

/// <summary>
/// Perlin noise generator for procedural terrain generation.
/// Creates smooth, natural-looking random patterns.
/// </summary>
public class PerlinNoise
{
    private readonly int[] permutation;
    private readonly Random random;

    public PerlinNoise(int seed)
    {
        random = new Random(seed);

        // Create permutation table for noise generation
        permutation = new int[512];
        var p = new int[256];

        for (int i = 0; i < 256; i++)
            p[i] = i;

        // Shuffle using Fisher-Yates
        for (int i = 255; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (p[i], p[j]) = (p[j], p[i]);
        }

        // Duplicate the permutation table
        for (int i = 0; i < 512; i++)
            permutation[i] = p[i % 256];
    }

    /// <summary>
    /// Generate 2D Perlin noise value at the given coordinates.
    /// Returns a value between 0.0 and 1.0.
    /// </summary>
    public double GetValue(double x, double y)
    {
        // Find unit grid cell containing point
        int xi = (int)Math.Floor(x) & 255;
        int yi = (int)Math.Floor(y) & 255;

        // Get relative xy coordinates within cell
        double xf = x - Math.Floor(x);
        double yf = y - Math.Floor(y);

        // Compute fade curves
        double u = Fade(xf);
        double v = Fade(yf);

        // Hash coordinates of the 4 cube corners
        int aa = permutation[permutation[xi] + yi];
        int ab = permutation[permutation[xi] + yi + 1];
        int ba = permutation[permutation[xi + 1] + yi];
        int bb = permutation[permutation[xi + 1] + yi + 1];

        // Blend results from the 4 corners
        double x1 = Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1, yf), u);
        double x2 = Lerp(Grad(ab, xf, yf - 1), Grad(bb, xf - 1, yf - 1), u);

        // Return value normalized to 0-1 range
        return (Lerp(x1, x2, v) + 1) / 2;
    }

    private static double Fade(double t)
    {
        // 6t^5 - 15t^4 + 10t^3
        return t * t * t * (t * (t * 6 - 15) + 10);
    }

    private static double Lerp(double a, double b, double t)
    {
        return a + t * (b - a);
    }

    private static double Grad(int hash, double x, double y)
    {
        // Convert low 2 bits of hash into gradient direction
        int h = hash & 3;
        double u = h < 2 ? x : y;
        double v = h < 2 ? y : x;
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }
}
