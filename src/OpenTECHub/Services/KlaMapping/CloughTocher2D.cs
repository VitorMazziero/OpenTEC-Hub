namespace OpenTECHub.Services.KlaMapping;

/// <summary>
/// Two-dimensional, C1 Clough-Tocher interpolator. The coefficient construction and
/// global curvature-minimizing gradient iteration follow SciPy's BSD-licensed
/// <c>_interpnd.pyx</c>; triangulation and point location are native managed code.
/// </summary>
internal sealed class CloughTocher2D
{
    private const double GeometryTolerance = 1e-12;

    private readonly Vertex[] _vertices;
    private readonly Triangle[] _triangles;
    private readonly double[,] _gradients;

    public CloughTocher2D(
        IReadOnlyList<(double Q, double N, double Value)> points,
        double gradientTolerance,
        int maximumGradientIterations)
    {
        if (points.Count < 3)
        {
            throw new ArgumentException("At least three points are required.", nameof(points));
        }

        _vertices = points.Select(point => new Vertex(point.Q, point.N, point.Value)).ToArray();
        _triangles = Triangulate(_vertices);
        if (_triangles.Length == 0)
        {
            throw new ArgumentException("The points do not form a usable two-dimensional triangulation.", nameof(points));
        }

        BuildTriangleNeighbours(_triangles);
        (_gradients, GradientEstimatorConverged) = EstimateGradients(
            _vertices,
            _triangles,
            gradientTolerance,
            maximumGradientIterations);
        HullArea = _triangles.Sum(triangle => Math.Abs(SignedArea(
            _vertices[triangle.A], _vertices[triangle.B], _vertices[triangle.C])));
    }

    public bool GradientEstimatorConverged { get; }

    public double HullArea { get; }

    public bool TryEvaluate(double q, double n, out double value)
    {
        for (var triangleIndex = 0; triangleIndex < _triangles.Length; triangleIndex++)
        {
            var triangle = _triangles[triangleIndex];
            if (!TryBarycentric(triangle, q, n, out var b0, out var b1, out var b2))
            {
                continue;
            }

            value = EvaluateTriangle(triangleIndex, b0, b1, b2);
            return true;
        }

        value = double.NaN;
        return false;
    }

    public double EvaluateNearest(double q, double n)
    {
        var bestDistance = double.PositiveInfinity;
        var bestValue = _vertices[0].Value;
        foreach (var vertex in _vertices)
        {
            var distance = ((vertex.Q - q) * (vertex.Q - q)) +
                           ((vertex.N - n) * (vertex.N - n));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestValue = vertex.Value;
            }
        }

        return bestValue;
    }

    private double EvaluateTriangle(int triangleIndex, double b0, double b1, double b2)
    {
        var triangle = _triangles[triangleIndex];
        var vertex1 = _vertices[triangle.A];
        var vertex2 = _vertices[triangle.B];
        var vertex3 = _vertices[triangle.C];

        var e12x = vertex2.Q - vertex1.Q;
        var e12y = vertex2.N - vertex1.N;
        var e23x = vertex3.Q - vertex2.Q;
        var e23y = vertex3.N - vertex2.N;
        var e31x = vertex1.Q - vertex3.Q;
        var e31y = vertex1.N - vertex3.N;

        var df12 = (_gradients[triangle.A, 0] * e12x) + (_gradients[triangle.A, 1] * e12y);
        var df21 = -((_gradients[triangle.B, 0] * e12x) + (_gradients[triangle.B, 1] * e12y));
        var df23 = (_gradients[triangle.B, 0] * e23x) + (_gradients[triangle.B, 1] * e23y);
        var df32 = -((_gradients[triangle.C, 0] * e23x) + (_gradients[triangle.C, 1] * e23y));
        var df31 = (_gradients[triangle.C, 0] * e31x) + (_gradients[triangle.C, 1] * e31y);
        var df13 = -((_gradients[triangle.A, 0] * e31x) + (_gradients[triangle.A, 1] * e31y));

        var c3000 = vertex1.Value;
        var c2100 = (df12 + (3 * c3000)) / 3;
        var c2010 = (df13 + (3 * c3000)) / 3;
        var c0300 = vertex2.Value;
        var c1200 = (df21 + (3 * c0300)) / 3;
        var c0210 = (df23 + (3 * c0300)) / 3;
        var c0030 = vertex3.Value;
        var c1020 = (df31 + (3 * c0030)) / 3;
        var c0120 = (df32 + (3 * c0030)) / 3;

        var c2001 = (c2100 + c2010 + c3000) / 3;
        var c0201 = (c1200 + c0300 + c0210) / 3;
        var c0021 = (c1020 + c0120 + c0030) / 3;

        Span<double> g = stackalloc double[3];
        for (var side = 0; side < 3; side++)
        {
            var neighbourIndex = triangle.Neighbours[side];
            if (neighbourIndex < 0)
            {
                g[side] = -0.5;
                continue;
            }

            var neighbour = _triangles[neighbourIndex];
            var centroidQ = (_vertices[neighbour.A].Q + _vertices[neighbour.B].Q + _vertices[neighbour.C].Q) / 3;
            var centroidN = (_vertices[neighbour.A].N + _vertices[neighbour.B].N + _vertices[neighbour.C].N) / 3;
            _ = TryBarycentricUnbounded(triangle, centroidQ, centroidN, out var c0, out var c1, out var c2);
            g[side] = side switch
            {
                0 => SafeRatio((2 * c2) + c1 - 1, 2 - (3 * c2) - (3 * c1)),
                1 => SafeRatio((2 * c0) + c2 - 1, 2 - (3 * c0) - (3 * c2)),
                _ => SafeRatio((2 * c1) + c0 - 1, 2 - (3 * c1) - (3 * c0)),
            };
        }

        var c0111 = (g[0] * (-c0300 + (3 * c0210) - (3 * c0120) + c0030) +
                     (-c0300 + (2 * c0210) - c0120 + c0021 + c0201)) / 2;
        var c1011 = (g[1] * (-c0030 + (3 * c1020) - (3 * c2010) + c3000) +
                     (-c0030 + (2 * c1020) - c2010 + c2001 + c0021)) / 2;
        var c1101 = (g[2] * (-c3000 + (3 * c2100) - (3 * c1200) + c0300) +
                     (-c3000 + (2 * c2100) - c1200 + c2001 + c0201)) / 2;

        var c1002 = (c1101 + c1011 + c2001) / 3;
        var c0102 = (c1101 + c0111 + c0201) / 3;
        var c0012 = (c1011 + c0111 + c0021) / 3;
        var c0003 = (c1002 + c0102 + c0012) / 3;

        var minimum = Math.Min(b0, Math.Min(b1, b2));
        var x1 = b0 - minimum;
        var x2 = b1 - minimum;
        var x3 = b2 - minimum;
        var x4 = 3 * minimum;

        return (Cube(x1) * c3000) +
               (3 * Square(x1) * x2 * c2100) +
               (3 * Square(x1) * x3 * c2010) +
               (3 * Square(x1) * x4 * c2001) +
               (3 * x1 * Square(x2) * c1200) +
               (6 * x1 * x2 * x4 * c1101) +
               (3 * x1 * Square(x3) * c1020) +
               (6 * x1 * x3 * x4 * c1011) +
               (3 * x1 * Square(x4) * c1002) +
               (Cube(x2) * c0300) +
               (3 * Square(x2) * x3 * c0210) +
               (3 * Square(x2) * x4 * c0201) +
               (3 * x2 * Square(x3) * c0120) +
               (6 * x2 * x3 * x4 * c0111) +
               (3 * x2 * Square(x4) * c0102) +
               (Cube(x3) * c0030) +
               (3 * Square(x3) * x4 * c0021) +
               (3 * x3 * Square(x4) * c0012) +
               (Cube(x4) * c0003);
    }

    private static (double[,] Gradients, bool Converged) EstimateGradients(
        IReadOnlyList<Vertex> vertices,
        IReadOnlyList<Triangle> triangles,
        double tolerance,
        int maximumIterations)
    {
        var neighbours = Enumerable.Range(0, vertices.Count)
            .Select(_ => new SortedSet<int>())
            .ToArray();
        foreach (var triangle in triangles)
        {
            AddPair(triangle.A, triangle.B);
            AddPair(triangle.B, triangle.C);
            AddPair(triangle.C, triangle.A);
        }

        var gradients = new double[vertices.Count, 2];
        for (var iteration = 0; iteration < maximumIterations; iteration++)
        {
            var error = 0.0;
            for (var pointIndex = 0; pointIndex < vertices.Count; pointIndex++)
            {
                var q00 = 0.0;
                var q01 = 0.0;
                var q11 = 0.0;
                var s0 = 0.0;
                var s1 = 0.0;
                var point = vertices[pointIndex];

                foreach (var neighbourIndex in neighbours[pointIndex])
                {
                    var neighbour = vertices[neighbourIndex];
                    var ex = neighbour.Q - point.Q;
                    var ey = neighbour.N - point.N;
                    var length = Math.Sqrt((ex * ex) + (ey * ey));
                    var lengthCubed = length * length * length;
                    var projectedAway =
                        (-ex * gradients[neighbourIndex, 0]) -
                        (ey * gradients[neighbourIndex, 1]);
                    var scalar = (6 * (point.Value - neighbour.Value)) - (2 * projectedAway);

                    q00 += 4 * ex * ex / lengthCubed;
                    q01 += 4 * ex * ey / lengthCubed;
                    q11 += 4 * ey * ey / lengthCubed;
                    s0 += scalar * ex / lengthCubed;
                    s1 += scalar * ey / lengthCubed;
                }

                var determinant = (q00 * q11) - (q01 * q01);
                if (Math.Abs(determinant) < GeometryTolerance)
                {
                    continue;
                }

                var r0 = ((q11 * s0) - (q01 * s1)) / determinant;
                var r1 = ((-q01 * s0) + (q00 * s1)) / determinant;
                var change = Math.Max(
                    Math.Abs(gradients[pointIndex, 0] + r0),
                    Math.Abs(gradients[pointIndex, 1] + r1));
                gradients[pointIndex, 0] = -r0;
                gradients[pointIndex, 1] = -r1;
                change /= Math.Max(1.0, Math.Max(Math.Abs(r0), Math.Abs(r1)));
                error = Math.Max(error, change);
            }

            if (error < tolerance)
            {
                return (gradients, true);
            }
        }

        return (gradients, false);

        void AddPair(int left, int right)
        {
            neighbours[left].Add(right);
            neighbours[right].Add(left);
        }
    }

    private static Triangle[] Triangulate(IReadOnlyList<Vertex> input)
    {
        if (TryTriangulateThreeByThree(input, out var factorial))
        {
            return factorial;
        }

        // Bowyer-Watson is sufficient here: experimental designs contain tens of points,
        // not millions. Including cocircular points in the cavity gives deterministic
        // diagonal selection for the paper's regular factorial design.
        var vertices = input.ToList();
        var superA = vertices.Count;
        vertices.Add(new Vertex(-32, -16, 0));
        var superB = vertices.Count;
        vertices.Add(new Vertex(32, -16, 0));
        var superC = vertices.Count;
        vertices.Add(new Vertex(0, 32, 0));

        var working = new List<Triangle> { CreateOriented(superA, superB, superC, vertices) };
        for (var pointIndex = 0; pointIndex < input.Count; pointIndex++)
        {
            var point = vertices[pointIndex];
            var bad = working.Where(triangle => CircumcircleContains(triangle, point, vertices)).ToArray();
            if (bad.Length == 0)
            {
                bad = working.Where(triangle =>
                    TryBarycentric(triangle, point.Q, point.N, vertices, out _, out _, out _)).ToArray();
            }

            var edges = new Dictionary<Edge, (int A, int B, int Count)>();
            foreach (var triangle in bad)
            {
                AddEdge(triangle.A, triangle.B);
                AddEdge(triangle.B, triangle.C);
                AddEdge(triangle.C, triangle.A);
            }

            working.RemoveAll(bad.Contains);
            foreach (var edge in edges.Values.Where(edge => edge.Count == 1))
            {
                working.Add(CreateOriented(edge.A, edge.B, pointIndex, vertices));
            }

            void AddEdge(int first, int second)
            {
                var key = new Edge(Math.Min(first, second), Math.Max(first, second));
                if (edges.TryGetValue(key, out var existing))
                {
                    edges[key] = (existing.A, existing.B, existing.Count + 1);
                }
                else
                {
                    edges[key] = (first, second, 1);
                }
            }
        }

        return working
            .Where(triangle => triangle.A < input.Count && triangle.B < input.Count && triangle.C < input.Count)
            .Where(triangle => Math.Abs(SignedArea(
                vertices[triangle.A], vertices[triangle.B], vertices[triangle.C])) > GeometryTolerance)
            .ToArray();
    }

    private static bool TryTriangulateThreeByThree(
        IReadOnlyList<Vertex> input,
        out Triangle[] triangles)
    {
        triangles = [];
        if (input.Count != 9)
        {
            return false;
        }

        var qAxis = input.Select(vertex => vertex.Q).Distinct().Order().ToArray();
        var nAxis = input.Select(vertex => vertex.N).Distinct().OrderDescending().ToArray();
        if (qAxis.Length != 3 || nAxis.Length != 3)
        {
            return false;
        }

        var indices = new int[3, 3];
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                var matches = Enumerable.Range(0, input.Count)
                    .Where(index =>
                        Math.Abs(input[index].Q - qAxis[column]) < GeometryTolerance &&
                        Math.Abs(input[index].N - nAxis[row]) < GeometryTolerance)
                    .ToArray();
                if (matches.Length != 1)
                {
                    return false;
                }

                indices[row, column] = matches[0];
            }
        }

        // Qhull's deterministic triangulation of a 3² factorial design forms the
        // diamond between the four edge-midpoints. Preserving that cocircular tie is
        // necessary for byte-stable agreement with scipy.interpolate.griddata.
        var topLeft = indices[0, 0];
        var topMiddle = indices[0, 1];
        var topRight = indices[0, 2];
        var middleLeft = indices[1, 0];
        var centre = indices[1, 1];
        var middleRight = indices[1, 2];
        var bottomLeft = indices[2, 0];
        var bottomMiddle = indices[2, 1];
        var bottomRight = indices[2, 2];
        triangles =
        [
            CreateOriented(topMiddle, middleLeft, centre, input),
            CreateOriented(middleLeft, topMiddle, topLeft, input),
            CreateOriented(middleLeft, bottomMiddle, centre, input),
            CreateOriented(bottomMiddle, middleLeft, bottomLeft, input),
            CreateOriented(middleRight, topMiddle, centre, input),
            CreateOriented(topMiddle, middleRight, topRight, input),
            CreateOriented(bottomMiddle, middleRight, centre, input),
            CreateOriented(middleRight, bottomMiddle, bottomRight, input),
        ];
        return true;
    }

    private static Triangle CreateOriented(int a, int b, int c, IReadOnlyList<Vertex> vertices)
        => SignedArea(vertices[a], vertices[b], vertices[c]) >= 0
            ? new Triangle(a, b, c)
            : new Triangle(b, a, c);

    private static bool CircumcircleContains(
        Triangle triangle,
        Vertex point,
        IReadOnlyList<Vertex> vertices)
    {
        var a = vertices[triangle.A];
        var b = vertices[triangle.B];
        var c = vertices[triangle.C];
        var ax = a.Q - point.Q;
        var ay = a.N - point.N;
        var bx = b.Q - point.Q;
        var by = b.N - point.N;
        var cx = c.Q - point.Q;
        var cy = c.N - point.N;
        var determinant =
            (((ax * ax) + (ay * ay)) * ((bx * cy) - (cx * by))) -
            (((bx * bx) + (by * by)) * ((ax * cy) - (cx * ay))) +
            (((cx * cx) + (cy * cy)) * ((ax * by) - (bx * ay)));
        return determinant > GeometryTolerance;
    }

    private static void BuildTriangleNeighbours(IReadOnlyList<Triangle> triangles)
    {
        var sides = new Dictionary<Edge, (int Triangle, int Side)>();
        for (var triangleIndex = 0; triangleIndex < triangles.Count; triangleIndex++)
        {
            var triangle = triangles[triangleIndex];
            Register(triangle.B, triangle.C, 0);
            Register(triangle.C, triangle.A, 1);
            Register(triangle.A, triangle.B, 2);

            void Register(int first, int second, int side)
            {
                var edge = new Edge(Math.Min(first, second), Math.Max(first, second));
                if (sides.TryGetValue(edge, out var other))
                {
                    triangle.Neighbours[side] = other.Triangle;
                    triangles[other.Triangle].Neighbours[other.Side] = triangleIndex;
                }
                else
                {
                    sides.Add(edge, (triangleIndex, side));
                }
            }
        }
    }

    private bool TryBarycentric(
        Triangle triangle,
        double q,
        double n,
        out double b0,
        out double b1,
        out double b2)
        => TryBarycentric(triangle, q, n, _vertices, out b0, out b1, out b2);

    private bool TryBarycentricUnbounded(
        Triangle triangle,
        double q,
        double n,
        out double b0,
        out double b1,
        out double b2)
    {
        _ = TryBarycentricCore(triangle, q, n, _vertices, out b0, out b1, out b2);
        return true;
    }

    private static bool TryBarycentric(
        Triangle triangle,
        double q,
        double n,
        IReadOnlyList<Vertex> vertices,
        out double b0,
        out double b1,
        out double b2)
    {
        if (!TryBarycentricCore(triangle, q, n, vertices, out b0, out b1, out b2))
        {
            return false;
        }

        return b0 >= -GeometryTolerance && b1 >= -GeometryTolerance && b2 >= -GeometryTolerance;
    }

    private static bool TryBarycentricCore(
        Triangle triangle,
        double q,
        double n,
        IReadOnlyList<Vertex> vertices,
        out double b0,
        out double b1,
        out double b2)
    {
        var a = vertices[triangle.A];
        var b = vertices[triangle.B];
        var c = vertices[triangle.C];
        var denominator = ((b.N - c.N) * (a.Q - c.Q)) +
                          ((c.Q - b.Q) * (a.N - c.N));
        if (Math.Abs(denominator) < GeometryTolerance)
        {
            b0 = b1 = b2 = double.NaN;
            return false;
        }

        b0 = (((b.N - c.N) * (q - c.Q)) + ((c.Q - b.Q) * (n - c.N))) / denominator;
        b1 = (((c.N - a.N) * (q - c.Q)) + ((a.Q - c.Q) * (n - c.N))) / denominator;
        b2 = 1 - b0 - b1;
        return true;
    }

    private static double SignedArea(Vertex a, Vertex b, Vertex c)
        => 0.5 * (((b.Q - a.Q) * (c.N - a.N)) - ((b.N - a.N) * (c.Q - a.Q)));

    private static double SafeRatio(double numerator, double denominator)
        => Math.Abs(denominator) < GeometryTolerance ? -0.5 : numerator / denominator;

    private static double Square(double value) => value * value;

    private static double Cube(double value) => value * value * value;

    private sealed record Vertex(double Q, double N, double Value);

    private sealed class Triangle(int a, int b, int c)
    {
        public int A { get; } = a;
        public int B { get; } = b;
        public int C { get; } = c;
        public int[] Neighbours { get; } = [-1, -1, -1];
    }

    private readonly record struct Edge(int A, int B);
}
