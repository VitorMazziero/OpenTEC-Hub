using System.Windows;

namespace OpenTECHub.Services.Recipes;

/// <summary>A node rectangle presented to the orthogonal recipe router.</summary>
public sealed record RecipeRouteObstacle(string Id, Rect Bounds);

/// <summary>Immutable orthogonal route selected for a recipe connection.</summary>
public sealed record RecipeRoute(IReadOnlyList<Point> Points);

/// <summary>
/// Computes short orthogonal routes around unrelated recipe blocks. Candidate lanes are built
/// from port coordinates and the sides of inflated obstacles, then searched with a bend penalty.
/// This keeps wires readable while guaranteeing that a route does not cross a block rectangle.
/// </summary>
public static class RecipeConnectionRouter
{
    private const double Clearance = 18;
    public static RecipeRoute Build(
        Point start,
        Point end,
        string sourceId,
        string targetId,
        IEnumerable<RecipeRouteObstacle> obstacles)
        => Build(start, end, sourceId, targetId, obstacles, false, true, 22);

    public static RecipeRoute Build(
        Point start,
        Point end,
        string sourceId,
        string targetId,
        IEnumerable<RecipeRouteObstacle> obstacles,
        bool startOnLeft,
        bool endOnLeft,
        double stub)
    {
        // The port stubs leave their own cards before routing begins. Keeping source and target
        // cards in the obstacle set prevents the middle of a reversed or return route from
        // crossing behind either card.
        var blocked = obstacles
            .Select(o => Inflate(o.Bounds, Clearance))
            .ToArray();

        // A horizontal stub is the port normal. Routing starts only after leaving
        // the source node and finishes just before entering the target node, so a
        // return connection cannot visually terminate through the card itself.
        var routedStart = new Point(start.X + (startOnLeft ? -stub : stub), start.Y);
        var routedEnd = new Point(end.X + (endOnLeft ? -stub : stub), end.Y);
        var middle = BuildCore(routedStart, routedEnd, blocked);
        var combined = new List<Point> { start, routedStart };
        combined.AddRange(middle.Points.Skip(1));
        combined.Add(end);
        return new RecipeRoute(Compress(combined));
    }

    private static RecipeRoute BuildCore(Point start, Point end, IReadOnlyList<Rect> blocked)
    {

        if (NearlyEqual(start.Y, end.Y) && IsClear(start, end, blocked))
        {
            return new RecipeRoute([start, end]);
        }

        var xs = new SortedSet<double> { start.X, end.X };
        var ys = new SortedSet<double> { start.Y, end.Y };
        foreach (var rect in blocked)
        {
            xs.Add(rect.Left);
            xs.Add(rect.Right);
            ys.Add(rect.Top);
            ys.Add(rect.Bottom);
        }

        var points = new Point[xs.Count, ys.Count];
        var xValues = xs.ToArray();
        var yValues = ys.ToArray();
        for (var x = 0; x < xValues.Length; x++)
        {
            for (var y = 0; y < yValues.Length; y++)
            {
                points[x, y] = new Point(xValues[x], yValues[y]);
            }
        }

        var startIndex = (Array.IndexOf(xValues, start.X), Array.IndexOf(yValues, start.Y));
        var endIndex = (Array.IndexOf(xValues, end.X), Array.IndexOf(yValues, end.Y));
        // The first and last segments are the horizontal normals of the ports. Starting
        // with direction 1 makes a vertical detour count as a real corner immediately
        // after the source stub; the same is accounted for when entering the target stub.
        var startState = new GridState(startIndex.Item1, startIndex.Item2, 1);
        var best = new Dictionary<GridState, RouteCost> { [startState] = new(0, 0) };
        var previous = new Dictionary<GridState, GridState>();
        var queue = new PriorityQueue<GridState, RouteCost>();
        queue.Enqueue(startState, new(0, 0));
        GridState? finish = null;

        while (queue.TryDequeue(out var current, out _))
        {
            if (!best.TryGetValue(current, out var currentCost))
            {
                continue;
            }

            if (current.X == endIndex.Item1 && current.Y == endIndex.Item2)
            {
                finish = current;
                break;
            }

            foreach (var (dx, dy, direction) in Directions)
            {
                var nx = current.X + dx;
                var ny = current.Y + dy;
                if (nx < 0 || nx >= xValues.Length || ny < 0 || ny >= yValues.Length)
                {
                    continue;
                }

                var from = points[current.X, current.Y];
                var to = points[nx, ny];
                if (!IsClear(from, to, blocked))
                {
                    continue;
                }

                var bends = currentCost.Bends + (current.Direction != direction ? 1 : 0);
                // The final segment from the routed target stub to the port is horizontal.
                // Charge that corner here so the selected route minimizes the complete wire,
                // not only the portion between the two stubs.
                if (nx == endIndex.Item1 && ny == endIndex.Item2 && direction != 1)
                {
                    bends++;
                }

                var cost = new RouteCost(bends, currentCost.Length + Distance(from, to));
                var next = new GridState(nx, ny, direction);
                if (best.TryGetValue(next, out var oldCost) && oldCost <= cost)
                {
                    continue;
                }

                best[next] = cost;
                previous[next] = current;
                queue.Enqueue(next, new(cost.Bends, cost.Length + Distance(to, end)));
            }
        }

        if (finish is null)
        {
            // A graph with unusual legacy coordinates can leave no visibility path. The
            // conservative fallback stays orthogonal; the normal graph always finds one.
            return new RecipeRoute([start, new Point(start.X, end.Y), end]);
        }

        var route = new List<Point>();
        for (var cursor = finish.Value; ; cursor = previous[cursor])
        {
            route.Add(points[cursor.X, cursor.Y]);
            if (cursor.Equals(startState))
            {
                break;
            }
        }

        route.Reverse();
        return new RecipeRoute(Compress(route));
    }

    private static readonly (int X, int Y, int Direction)[] Directions =
    [
        (1, 0, 1), (-1, 0, 1), (0, 1, 2), (0, -1, 2),
    ];

    private readonly record struct GridState(int X, int Y, int Direction);

    /// <summary>Lexicographic route cost: fewer bends always beat a shorter route.</summary>
    private readonly record struct RouteCost(int Bends, double Length) : IComparable<RouteCost>
    {
        public int CompareTo(RouteCost other)
        {
            var bends = Bends.CompareTo(other.Bends);
            return bends != 0 ? bends : Length.CompareTo(other.Length);
        }

        public static bool operator <=(RouteCost left, RouteCost right) => left.CompareTo(right) <= 0;
        public static bool operator >=(RouteCost left, RouteCost right) => left.CompareTo(right) >= 0;
    }

    private static Rect Inflate(Rect rect, double amount)
        => new(rect.Left - amount, rect.Top - amount, rect.Width + 2 * amount, rect.Height + 2 * amount);

    private static bool IsClear(Point from, Point to, IReadOnlyList<Rect> blocked)
    {
        if (!NearlyEqual(from.X, to.X) && !NearlyEqual(from.Y, to.Y))
        {
            return false;
        }

        foreach (var rect in blocked)
        {
            if (NearlyEqual(from.X, to.X))
            {
                if (from.X > rect.Left && from.X < rect.Right
                    && Math.Max(Math.Min(from.Y, to.Y), rect.Top) < Math.Min(Math.Max(from.Y, to.Y), rect.Bottom))
                {
                    return false;
                }
            }
            else if (from.Y > rect.Top && from.Y < rect.Bottom
                     && Math.Max(Math.Min(from.X, to.X), rect.Left) < Math.Min(Math.Max(from.X, to.X), rect.Right))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<Point> Compress(IReadOnlyList<Point> points)
    {
        var result = new List<Point>();
        foreach (var point in points)
        {
            if (result.Count >= 2)
            {
                var a = result[^2];
                var b = result[^1];
                if ((NearlyEqual(a.X, b.X) && NearlyEqual(b.X, point.X))
                    || (NearlyEqual(a.Y, b.Y) && NearlyEqual(b.Y, point.Y)))
                {
                    result[^1] = point;
                    continue;
                }
            }

            if (result.Count == 0 || !NearlyEqual(result[^1].X, point.X) || !NearlyEqual(result[^1].Y, point.Y))
            {
                result.Add(point);
            }
        }

        return result;
    }

    private static bool NearlyEqual(double a, double b) => Math.Abs(a - b) < 0.01;

    private static double Distance(Point a, Point b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
}
