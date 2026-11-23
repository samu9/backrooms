// A* on the 0.5 m occupancy grid, in a window around the start (web/src/ai/path.ts). Walls can be
// given a finite cost, so a path may cut through one: that is where the entity phases.
namespace Backrooms.Core.AI;

public readonly record struct Waypoint(double X, double Z, bool Wall);

public sealed class PathFinder(Gen gen)
{
    // ---- cached occupancy (Solid() is cheap, but A* asks for the same cells many times) ----
    const int Block = 64;
    readonly Dictionary<(int, int), byte[]> blocks = []; // 0 = unknown, 1 = open, 2 = wall

    public Gen Gen => gen;

    public bool WallAt(int gx, int gz)
    {
        int bx = (int)Math.Floor((double)gx / Block), bz = (int)Math.Floor((double)gz / Block);
        if (!blocks.TryGetValue((bx, bz), out var b))
        {
            if (blocks.Count > 512) blocks.Clear();
            blocks[(bx, bz)] = b = new byte[Block * Block];
        }
        int i = (gz - bz * Block) * Block + gx - bx * Block;
        if (b[i] == 0) b[i] = gen.Solid(gx, gz) ? (byte)2 : (byte)1;
        return b[i] == 2;
    }

    static int Cell(double m) => (int)Math.Floor(m / Gen.Cell);

    /// <summary>Nothing solid within r metres of the segment (sampled every 0.2 m, with side offsets).</summary>
    public bool ClearLine(double ax, double az, double bx, double bz, double r = 0)
    {
        double dx = bx - ax, dz = bz - az;
        double len = Math.Sqrt(dx * dx + dz * dz);
        int n = Math.Max(1, (int)Math.Ceiling(len / 0.2));
        double px = len > 0 ? -dz / len : 0, pz = len > 0 ? dx / len : 0;
        for (int i = 0; i <= n; i++)
        {
            double t = (double)i / n;
            double x = ax + dx * t, z = az + dz * t;
            if (WallAt(Cell(x), Cell(z))) return false;
            if (r > 0 && (WallAt(Cell(x + px * r), Cell(z + pz * r)) || WallAt(Cell(x - px * r), Cell(z - pz * r)))) return false;
        }
        return true;
    }

    static readonly (int dx, int dz, double step)[] Dirs =
    [
        (1, 0, 1), (-1, 0, 1), (0, 1, 1), (0, -1, 1),
        (1, 1, Math.Sqrt(2)), (1, -1, Math.Sqrt(2)), (-1, 1, Math.Sqrt(2)), (-1, -1, Math.Sqrt(2)),
    ];

    // scratch buffers, reused between searches
    float[] g = [], f = [];
    int[] came = [], heap = [];
    byte[] state = [];

    /// <summary>
    /// Path from (sx, sz) to (tx, tz) in metres. If the goal is outside the window or unreachable,
    /// returns the path to the reachable cell closest to it.
    /// </summary>
    /// <param name="wallCost">cost of entering a wall cell; infinity = walls are impassable</param>
    /// <param name="half">half size of the search window, in cells (120 = 60 m each way)</param>
    public List<Waypoint> FindPath(double sx, double sz, double tx, double tz, double wallCost = double.PositiveInfinity, int half = 120, int maxNodes = 40000)
    {
        int W = half * 2 + 1, N = W * W;
        if (g.Length < N)
        {
            g = new float[N];
            f = new float[N];
            came = new int[N];
            state = new byte[N];
            heap = new int[N];
        }
        int ox = Cell(sx) - half, oz = Cell(sz) - half;
        int gxT = Math.Clamp(Cell(tx) - ox, 0, W - 1), gzT = Math.Clamp(Cell(tz) - oz, 0, W - 1);
        int goal = gzT * W + gxT, start = half * W + half;
        Array.Fill(g, float.PositiveInfinity, 0, N);
        Array.Clear(state, 0, N);
        const double Sqrt2 = 1.4142135623730951;
        double H(int i)
        {
            int dx = Math.Abs(i % W - gxT), dz = Math.Abs(i / W - gzT);
            return Math.Max(dx, dz) + (Sqrt2 - 1) * Math.Min(dx, dz);
        }
        bool infinite = double.IsPositiveInfinity(wallCost);
        int size = 0;
        void Push(int i)
        {
            int k = size++;
            heap[k] = i;
            while (k > 0)
            {
                int p = (k - 1) >> 1;
                if (f[heap[p]] <= f[heap[k]]) break;
                (heap[p], heap[k]) = (heap[k], heap[p]);
                k = p;
            }
        }
        int Pop()
        {
            int top = heap[0];
            heap[0] = heap[--size];
            int k = 0;
            for (;;)
            {
                int l = 2 * k + 1, r = l + 1, m = k;
                if (l < size && f[heap[l]] < f[heap[m]]) m = l;
                if (r < size && f[heap[r]] < f[heap[m]]) m = r;
                if (m == k) break;
                (heap[m], heap[k]) = (heap[k], heap[m]);
                k = m;
            }
            return top;
        }

        g[start] = 0;
        f[start] = (float)H(start);
        came[start] = -1;
        Push(start);
        state[start] = 1;
        int best = start;
        double bestH = H(start);
        int expanded = 0;
        while (size > 0 && expanded < maxNodes)
        {
            int cur = Pop();
            if (state[cur] == 2) continue;
            state[cur] = 2;
            expanded++;
            double hc = H(cur);
            if (hc < bestH)
            {
                bestH = hc;
                best = cur;
            }
            if (cur == goal) break;
            int cx = cur % W, cz = cur / W;
            foreach (var (dx, dz, step) in Dirs)
            {
                int nx = cx + dx, nz = cz + dz;
                if (nx < 0 || nz < 0 || nx >= W || nz >= W) continue;
                int ni = nz * W + nx;
                if (state[ni] == 2) continue;
                bool wall = WallAt(nx + ox, nz + oz);
                if (wall && infinite) continue;
                // no cutting corners, and no diagonal steps into or out of walls
                if (dx != 0 && dz != 0)
                {
                    if (wall || WallAt(cx + ox, cz + oz)) continue;
                    if (WallAt(cx + dx + ox, cz + oz) || WallAt(cx + ox, cz + dz + oz)) continue;
                }
                float ng = (float)(g[cur] + step * (wall ? wallCost : 1));
                if (ng >= g[ni]) continue;
                g[ni] = ng;
                f[ni] = (float)(ng + H(ni));
                came[ni] = cur;
                Push(ni);
                state[ni] = 1;
            }
        }
        int end = state[goal] == 2 ? goal : best;
        if (end == start && start != goal) return [];
        var cells = new List<int>();
        for (int i = end; i != -1; i = came[i]) cells.Add(i);
        cells.Reverse();
        var pts = cells.Select(i =>
        {
            int gx = i % W + ox, gz = i / W + oz;
            return new Waypoint((gx + 0.5) * Gen.Cell, (gz + 0.5) * Gen.Cell, WallAt(gx, gz));
        }).ToList();
        return Smooth(pts);
    }

    /// <summary>String-pull the open stretches: keep a waypoint only where the straight line would clip a wall.</summary>
    List<Waypoint> Smooth(List<Waypoint> pts)
    {
        if (pts.Count <= 2) return pts.Skip(1).ToList();
        var output = new List<Waypoint>();
        int a = 0;
        while (a < pts.Count - 1)
        {
            if (pts[a + 1].Wall || pts[a].Wall)
            {
                output.Add(pts[a + 1]);
                a++;
                continue;
            }
            int b = a + 1;
            while (b + 1 < pts.Count && !pts[b + 1].Wall && ClearLine(pts[a].X, pts[a].Z, pts[b + 1].X, pts[b + 1].Z, 0.22)) b++;
            output.Add(pts[b]);
            a = b;
        }
        return output;
    }

    public static double PathLength(double sx, double sz, IEnumerable<Waypoint> pts)
    {
        double L = 0, x = sx, z = sz;
        foreach (var p in pts)
        {
            L += Math.Sqrt((p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z));
            (x, z) = (p.X, p.Z);
        }
        return L;
    }
}
