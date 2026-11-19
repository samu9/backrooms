namespace Backrooms.Core;

/// <summary>Spawn points, ported from web/src/main.ts.</summary>
public static class Viewpoints
{
    /// <summary>
    /// A viewpoint with a long sight line, chosen deterministically from k (the web's ?view=k).
    /// Same answer as the web client, so screenshots of both can be compared.
    /// </summary>
    public static (double X, double Z, double Yaw) Find(Gen gen, int k)
    {
        var r = gen.Rng(k, 3, 909);
        for (int tries = 0; tries < 40; tries++)
        {
            var (x, z) = gen.FindFree((r.Next() - 0.5) * 600, (r.Next() - 0.5) * 600, 3);
            double best = 0, bestA = 0;
            for (int a = 0; a < 24; a++)
            {
                double t = a / 24.0 * Math.PI * 2;
                double d = gen.FreeDistance(x, z, Math.Cos(t), Math.Sin(t), 50);
                if (d > best)
                {
                    best = d;
                    bestA = t;
                }
            }
            if (best > 12 || tries == 39)
            {
                double t = bestA + (r.Next() - 0.5) * 0.35;
                return (x, z, Math.Atan2(-Math.Cos(t), -Math.Sin(t)));
            }
        }
        return (0, 0, 0);
    }
}
