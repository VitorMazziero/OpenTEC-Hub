namespace OpenTECHub.Services.KlaMapping;

public interface IKlaMappingEngine
{
    IReadOnlyList<string> Validate(KlaExperimentSnapshot input);

    KlaSurface Reconstruct(KlaExperimentSnapshot input, CancellationToken cancellationToken = default);

    KlaPathResult FindMaximumHeadroomPath(
        KlaSurface surface,
        IProgress<KlaSearchProgress>? progress = null,
        CancellationToken cancellationToken = default);

    IReadOnlyList<KlaPathPoint> GeneratePath(
        KlaSurface surface,
        double startAirflowNormalized,
        double startAgitationNormalized,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Pure numerical implementation of D-008. It never depends on WPF, persistence or the
/// device service, so fitting cannot become an accidental command source.
/// </summary>
public sealed class KlaMappingEngine : IKlaMappingEngine
{
    public const string ImplementationVersion = "opentec-kla-reference-1";

    public IReadOnlyList<string> Validate(KlaExperimentSnapshot input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var issues = new List<string>();
        var domain = input.Domain;
        if (!AllFinite(
                domain.AirflowMinimumLpm,
                domain.AirflowMaximumLpm,
                domain.AgitationMinimumRpm,
                domain.AgitationMaximumRpm) ||
            domain.AirflowMinimumLpm >= domain.AirflowMaximumLpm ||
            domain.AgitationMinimumRpm >= domain.AgitationMaximumRpm)
        {
            issues.Add("Os limites de vazão e agitação devem ser finitos e crescentes.");
        }

        if (input.Anchors.Length < 6)
        {
            issues.Add("Informe ao menos seis pontos experimentais; o desenho 3² do artigo usa nove.");
        }

        var coordinates = new HashSet<(double Airflow, double Agitation)>();
        foreach (var anchor in input.Anchors)
        {
            if (!AllFinite(anchor.AirflowLpm, anchor.AgitationRpm, anchor.KlaPerHour))
            {
                issues.Add("Todos os valores experimentais devem ser finitos.");
                break;
            }

            if (anchor.KlaPerHour < 0)
            {
                issues.Add("kLa experimental não pode ser negativo.");
                break;
            }

            if (domain.AirflowMinimumLpm < domain.AirflowMaximumLpm &&
                domain.AgitationMinimumRpm < domain.AgitationMaximumRpm &&
                (anchor.AirflowLpm < domain.AirflowMinimumLpm ||
                 anchor.AirflowLpm > domain.AirflowMaximumLpm ||
                 anchor.AgitationRpm < domain.AgitationMinimumRpm ||
                 anchor.AgitationRpm > domain.AgitationMaximumRpm))
            {
                issues.Add("Cada ponto experimental deve estar dentro do domínio declarado.");
                break;
            }

            if (!coordinates.Add((anchor.AirflowLpm, anchor.AgitationRpm)))
            {
                issues.Add("Coordenadas experimentais repetidas devem ser consolidadas antes do ajuste.");
                break;
            }
        }

        if (input.Anchors.Length >= 3 &&
            domain.AirflowMinimumLpm < domain.AirflowMaximumLpm &&
            domain.AgitationMinimumRpm < domain.AgitationMaximumRpm)
        {
            var normalized = input.Anchors.Select(anchor => (
                Q: domain.NormalizeAirflow(anchor.AirflowLpm),
                N: domain.NormalizeAgitation(anchor.AgitationRpm))).ToArray();
            var hasArea = false;
            for (var first = 0; first < normalized.Length - 2 && !hasArea; first++)
            {
                for (var second = first + 1; second < normalized.Length - 1 && !hasArea; second++)
                {
                    for (var third = second + 1; third < normalized.Length; third++)
                    {
                        var area = Math.Abs(
                            ((normalized[second].Q - normalized[first].Q) *
                             (normalized[third].N - normalized[first].N)) -
                            ((normalized[second].N - normalized[first].N) *
                             (normalized[third].Q - normalized[first].Q)));
                        if (area > 1e-8)
                        {
                            hasArea = true;
                            break;
                        }
                    }
                }
            }

            if (!hasArea)
            {
                issues.Add("Os pontos experimentais são colineares e não definem uma superfície 2D.");
            }
        }

        issues.AddRange(input.Algorithm.Validate());
        return issues.Distinct(StringComparer.Ordinal).ToArray();
    }

    public KlaSurface Reconstruct(
        KlaExperimentSnapshot input,
        CancellationToken cancellationToken = default)
    {
        var issues = Validate(input);
        if (issues.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", issues), nameof(input));
        }

        var domain = input.Domain;
        // Stable ordering means moving a row in the editor cannot change a cocircular
        // Delaunay tie. It also reproduces the paper script's high-N to low-N ordering.
        var normalized = input.Anchors
            .OrderByDescending(anchor => anchor.AgitationRpm)
            .ThenBy(anchor => anchor.AirflowLpm)
            .Select(anchor => (
                Q: domain.NormalizeAirflow(anchor.AirflowLpm),
                N: domain.NormalizeAgitation(anchor.AgitationRpm),
                Value: anchor.KlaPerHour))
            .ToArray();
        var algorithm = input.Algorithm;
        var interpolator = new CloughTocher2D(
            normalized,
            algorithm.CloughTocherGradientTolerance,
            algorithm.CloughTocherMaximumIterations);

        var resolution = algorithm.SurfaceGridResolution;
        var cubicGrid = new double[resolution * resolution];
        var nearestFilled = 0;
        for (var row = 0; row < resolution; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var n = (double)row / (resolution - 1);
            for (var column = 0; column < resolution; column++)
            {
                var q = (double)column / (resolution - 1);
                if (!interpolator.TryEvaluate(q, n, out var value) || !double.IsFinite(value))
                {
                    value = interpolator.EvaluateNearest(q, n);
                    nearestFilled++;
                }

                cubicGrid[(row * resolution) + column] = value;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var smoothed = ReferenceGaussianFilter.Apply(
            cubicGrid,
            resolution,
            algorithm.GaussianSigmaGridCells);
        if (smoothed.Any(value => !double.IsFinite(value)))
        {
            throw new InvalidOperationException("A reconstrução produziu nós não finitos.");
        }

        var spline = BicubicSurface.Create(smoothed, resolution);
        var residuals = normalized
            .Select(anchor => spline.Evaluate(anchor.Q, anchor.N).Value - anchor.Value)
            .ToArray();
        var warnings = new List<string>();
        var coverage = Math.Clamp(interpolator.HullArea * 100, 0, 100);
        if (coverage < 99.0)
        {
            warnings.Add(
                $"O casco convexo cobre {coverage:F1}% do domínio; {nearestFilled} nós externos usam vizinho mais próximo.");
        }

        if (!interpolator.GradientEstimatorConverged)
        {
            warnings.Add("A estimativa global de gradientes Clough–Tocher atingiu o limite de iterações.");
        }

        var minimum = smoothed.Min();
        var maximum = smoothed.Max();
        if (minimum < 0)
        {
            warnings.Add("A superfície suavizada contém kLa negativo; revise pontos e domínio antes de publicar.");
        }

        var diagnostics = new KlaSurfaceDiagnostics(
            minimum,
            maximum,
            Math.Sqrt(residuals.Sum(value => value * value) / residuals.Length),
            residuals.Max(Math.Abs),
            coverage,
            nearestFilled,
            interpolator.GradientEstimatorConverged,
            warnings);
        var fingerprint = KlaFingerprint.ForDoubles(new
        {
            Version = ImplementationVersion,
            Input = input.ScientificFingerprint(),
            Stage = "clough-tocher-nearest-gaussian-not-a-knot-spline",
        }, smoothed);
        return new KlaSurface(input, smoothed, spline, diagnostics, fingerprint);
    }

    public KlaPathResult FindMaximumHeadroomPath(
        KlaSurface surface,
        IProgress<KlaSearchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        var algorithm = surface.Input.Algorithm;
        var resolution = algorithm.CandidateGridResolution;
        var scores = new double[resolution * resolution];
        var total = scores.Length;
        var completed = 0;
        var bestScore = double.NegativeInfinity;
        var bestQ = 0.5;
        var bestN = 0.5;
        List<NormalizedPathPoint>? bestPath = null;

        for (var row = 0; row < resolution; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var n = Lerp(algorithm.CandidateMinimum, algorithm.CandidateMaximum, row, resolution);
            for (var column = 0; column < resolution; column++)
            {
                var q = Lerp(algorithm.CandidateMinimum, algorithm.CandidateMaximum, column, resolution);
                var candidatePath = GenerateNormalizedPath(surface, q, n, cancellationToken);
                var score = ScoreHeadroom(candidatePath);
                scores[(row * resolution) + column] = score;
                completed++;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestQ = q;
                    bestN = n;
                    bestPath = candidatePath;
                }
            }

            progress?.Report(new KlaSearchProgress(
                completed,
                total,
                row + 1,
                resolution,
                bestScore,
                bestQ,
                bestN));
        }

        if (bestPath is null || bestPath.Count < 2 || !double.IsFinite(bestScore))
        {
            throw new InvalidOperationException("Nenhuma condição inicial produziu uma trajetória válida.");
        }

        return BuildResult(surface, bestPath, scores, resolution, bestQ, bestN, bestScore);
    }

    public IReadOnlyList<KlaPathPoint> GeneratePath(
        KlaSurface surface,
        double startAirflowNormalized,
        double startAgitationNormalized,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(surface);
        var normalized = GenerateNormalizedPath(
            surface,
            Math.Clamp(startAirflowNormalized, 0, 1),
            Math.Clamp(startAgitationNormalized, 0, 1),
            cancellationToken);
        return ToPhysical(surface.Input.Domain, normalized);
    }

    public static string AlgorithmIdentity(KlaAlgorithmSettings settings) => settings.IsPaperReference
        ? "Método do artigo · Clough–Tocher 300×300 → vizinho mais próximo → Gauss σ=5 → spline bicúbica · RK45 · folga 150×150"
        : $"Método parametrizado · Clough–Tocher {settings.SurfaceGridResolution}×{settings.SurfaceGridResolution} → " +
          $"vizinho mais próximo → Gauss σ={settings.GaussianSigmaGridCells:G4} → spline bicúbica · " +
          $"RK45 · folga {settings.CandidateGridResolution}×{settings.CandidateGridResolution}";

    private static KlaPathResult BuildResult(
        KlaSurface surface,
        List<NormalizedPathPoint> rawPath,
        double[] scores,
        int scoreResolution,
        double selectedQ,
        double selectedN,
        double selectedScore)
    {
        if (rawPath[0].Kla > rawPath[^1].Kla)
        {
            rawPath.Reverse();
        }

        var physical = ToPhysical(surface.Input.Domain, rawPath);
        var allocation = new List<KlaAllocationSample>();
        const double monotonicTolerance = 1e-9;
        foreach (var point in physical)
        {
            if (allocation.Count == 0 || point.KlaPerHour > allocation[^1].KlaPerHour + monotonicTolerance)
            {
                allocation.Add(new KlaAllocationSample(
                    point.KlaPerHour,
                    point.AirflowLpm,
                    point.AgitationRpm));
            }
        }

        if (allocation.Count < 2)
        {
            throw new InvalidOperationException("A trajetória não contém uma relação kLa estritamente crescente.");
        }

        var warnings = new List<string>();
        if (allocation.Count != physical.Count)
        {
            warnings.Add(
                $"{physical.Count - allocation.Count} amostras repetidas ou decrescentes foram removidas da relação de alocação.");
        }

        if (surface.Diagnostics.MinimumKlaPerHour < 0)
        {
            warnings.Add("Publicação recusada enquanto a superfície contiver kLa negativo.");
        }

        var length = 0.0;
        for (var index = 1; index < rawPath.Count; index++)
        {
            var dq = rawPath[index].Q - rawPath[index - 1].Q;
            var dn = rawPath[index].N - rawPath[index - 1].N;
            length += Math.Sqrt((dq * dq) + (dn * dn));
        }

        var domain = surface.Input.Domain;
        var diagnostics = new KlaPathDiagnostics(
            selectedQ,
            selectedN,
            domain.DenormalizeAirflow(selectedQ),
            domain.DenormalizeAgitation(selectedN),
            selectedScore,
            scores.Max(),
            length,
            allocation[0].KlaPerHour,
            allocation[^1].KlaPerHour,
            scores.Length,
            allocation.Count,
            warnings);
        var fingerprint = KlaFingerprint.ForDoubles(new
        {
            Version = ImplementationVersion,
            Surface = surface.Fingerprint,
            selectedQ,
            selectedN,
            selectedScore,
        }, allocation.SelectMany(point => new[]
        {
            point.KlaPerHour,
            point.AirflowLpm,
            point.AgitationRpm,
        }));
        return new KlaPathResult(
            physical,
            allocation,
            scores,
            scoreResolution,
            diagnostics,
            surface.Fingerprint,
            fingerprint);
    }

    private static List<NormalizedPathPoint> GenerateNormalizedPath(
        KlaSurface surface,
        double q,
        double n,
        CancellationToken cancellationToken)
    {
        var descending = IntegrateBranch(surface, q, n, -1, cancellationToken);
        var ascending = IntegrateBranch(surface, q, n, +1, cancellationToken);
        descending.Reverse();
        if (ascending.Count > 0)
        {
            ascending.RemoveAt(0);
        }

        descending.AddRange(ascending);
        var result = new List<NormalizedPathPoint>(descending.Count);
        foreach (var state in descending)
        {
            if (result.Count > 0 &&
                Math.Abs(result[^1].Q - state.Q) < 1e-12 &&
                Math.Abs(result[^1].N - state.N) < 1e-12)
            {
                continue;
            }

            var evaluated = surface.EvaluateNormalized(state.Q, state.N);
            result.Add(new NormalizedPathPoint(
                state.Q,
                state.N,
                evaluated.Value,
                Headroom(state.Q, state.N)));
        }

        return result;
    }

    private static List<State> IntegrateBranch(
        KlaSurface surface,
        double startQ,
        double startN,
        int direction,
        CancellationToken cancellationToken)
    {
        var settings = surface.Input.Algorithm;
        var states = new List<State> { new(startQ, startN) };
        var state = states[0];
        var time = 0.0;
        var step = SelectInitialStep(surface, state, direction, settings);

        for (var iteration = 0; iteration < 10000 && time < settings.IntegrationHorizon; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var initialDerivative = Direction(surface, state, direction, settings.GradientTermination);
            if (!initialDerivative.IsUsable)
            {
                break;
            }

            step = Math.Min(step, Math.Min(settings.OdeMaximumStep, settings.IntegrationHorizon - time));
            var trial = DormandPrinceStep(surface, state, step, direction, settings.GradientTermination);
            var scaleQ = settings.OdeAbsoluteTolerance +
                         (settings.OdeRelativeTolerance * Math.Max(Math.Abs(state.Q), Math.Abs(trial.Fifth.Q)));
            var scaleN = settings.OdeAbsoluteTolerance +
                         (settings.OdeRelativeTolerance * Math.Max(Math.Abs(state.N), Math.Abs(trial.Fifth.N)));
            var errorQ = (trial.Fifth.Q - trial.Fourth.Q) / scaleQ;
            var errorN = (trial.Fifth.N - trial.Fourth.N) / scaleN;
            var error = RootMeanSquare(errorQ, errorN);
            if (!double.IsFinite(error))
            {
                throw new InvalidOperationException("A integração RK45 produziu erro não finito.");
            }

            var rejected = false;
            while (error >= 1.0)
            {
                rejected = true;
                step *= Math.Max(0.2, 0.9 * Math.Pow(error, -0.2));
                if (step < 1e-14)
                {
                    throw new InvalidOperationException("O passo RK45 ficou abaixo da precisão numérica.");
                }

                trial = DormandPrinceStep(surface, state, step, direction, settings.GradientTermination);
                scaleQ = settings.OdeAbsoluteTolerance +
                         (settings.OdeRelativeTolerance * Math.Max(Math.Abs(state.Q), Math.Abs(trial.Fifth.Q)));
                scaleN = settings.OdeAbsoluteTolerance +
                         (settings.OdeRelativeTolerance * Math.Max(Math.Abs(state.N), Math.Abs(trial.Fifth.N)));
                errorQ = (trial.Fifth.Q - trial.Fourth.Q) / scaleQ;
                errorN = (trial.Fifth.N - trial.Fourth.N) / scaleN;
                error = RootMeanSquare(errorQ, errorN);
                if (!double.IsFinite(error))
                {
                    throw new InvalidOperationException("A integração RK45 produziu erro não finito.");
                }
            }

            var boundaryAtEnd = BoundaryDistance(trial.Fifth);
            var gradientAtEnd = GradientDistance(surface, trial.Fifth, settings.GradientTermination);
            if (boundaryAtEnd <= 0 || gradientAtEnd <= 0)
            {
                var eventFraction = 1.0;
                if (boundaryAtEnd <= 0)
                {
                    eventFraction = Math.Min(
                        eventFraction,
                        FindEventFraction(trial, BoundaryDistance));
                }

                if (gradientAtEnd <= 0)
                {
                    eventFraction = Math.Min(
                        eventFraction,
                        FindEventFraction(
                            trial,
                            candidate => GradientDistance(
                                surface,
                                candidate,
                                settings.GradientTermination)));
                }

                states.Add(trial.Dense(eventFraction));
                break;
            }

            states.Add(trial.Fifth);
            state = trial.Fifth;
            time += step;

            var factor = error == 0
                ? 10.0
                : Math.Min(10.0, 0.9 * Math.Pow(error, -0.2));
            if (rejected)
            {
                factor = Math.Min(1.0, factor);
            }

            step = Math.Min(settings.OdeMaximumStep, step * factor);
        }

        return states;
    }

    private static double SelectInitialStep(
        KlaSurface surface,
        State state,
        int direction,
        KlaAlgorithmSettings settings)
    {
        var derivative = Direction(surface, state, direction, settings.GradientTermination).Vector;
        var scaleQ = settings.OdeAbsoluteTolerance + (Math.Abs(state.Q) * settings.OdeRelativeTolerance);
        var scaleN = settings.OdeAbsoluteTolerance + (Math.Abs(state.N) * settings.OdeRelativeTolerance);
        var d0 = RootMeanSquare(state.Q / scaleQ, state.N / scaleN);
        var d1 = RootMeanSquare(derivative.Q / scaleQ, derivative.N / scaleN);
        var h0 = d0 < 1e-5 || d1 < 1e-5 ? 1e-6 : 0.01 * d0 / d1;
        h0 = Math.Min(h0, settings.IntegrationHorizon);
        var trialState = Combine(state, h0, (1, derivative));
        var trialDerivative = Direction(
            surface,
            trialState,
            direction,
            settings.GradientTermination).Vector;
        var d2 = RootMeanSquare(
            (trialDerivative.Q - derivative.Q) / scaleQ,
            (trialDerivative.N - derivative.N) / scaleN) / h0;
        var h1 = d1 <= 1e-15 && d2 <= 1e-15
            ? Math.Max(1e-6, h0 * 1e-3)
            : Math.Pow(0.01 / Math.Max(d1, d2), 0.2);
        return Math.Min(
            Math.Min(100 * h0, h1),
            Math.Min(settings.IntegrationHorizon, settings.OdeMaximumStep));
    }

    private static RkTrial DormandPrinceStep(
        KlaSurface surface,
        State y,
        double h,
        int direction,
        double gradientMinimum)
    {
        var k1 = Direction(surface, y, direction, gradientMinimum).Vector;
        var k2 = Direction(surface, Combine(y, h, (1.0 / 5, k1)), direction, gradientMinimum).Vector;
        var k3 = Direction(surface, Combine(y, h, (3.0 / 40, k1), (9.0 / 40, k2)), direction, gradientMinimum).Vector;
        var k4 = Direction(surface, Combine(y, h,
            (44.0 / 45, k1), (-56.0 / 15, k2), (32.0 / 9, k3)), direction, gradientMinimum).Vector;
        var k5 = Direction(surface, Combine(y, h,
            (19372.0 / 6561, k1), (-25360.0 / 2187, k2),
            (64448.0 / 6561, k3), (-212.0 / 729, k4)), direction, gradientMinimum).Vector;
        var k6 = Direction(surface, Combine(y, h,
            (9017.0 / 3168, k1), (-355.0 / 33, k2),
            (46732.0 / 5247, k3), (49.0 / 176, k4),
            (-5103.0 / 18656, k5)), direction, gradientMinimum).Vector;
        var fifth = Combine(y, h,
            (35.0 / 384, k1), (500.0 / 1113, k3),
            (125.0 / 192, k4), (-2187.0 / 6784, k5), (11.0 / 84, k6));
        var k7 = Direction(surface, fifth, direction, gradientMinimum).Vector;
        var fourth = Combine(y, h,
            (5179.0 / 57600, k1), (7571.0 / 16695, k3),
            (393.0 / 640, k4), (-92097.0 / 339200, k5),
            (187.0 / 2100, k6), (1.0 / 40, k7));
        return new RkTrial(y, h, fifth, fourth, k1, k3, k4, k5, k6, k7);
    }

    private static (State Vector, bool IsUsable) Direction(
        KlaSurface surface,
        State state,
        int direction,
        double gradientMinimum)
    {
        if (state.Q is < -0.1 or > 1.1 || state.N is < -0.1 or > 1.1)
        {
            return (new State(0, 0), false);
        }

        var value = surface.EvaluateNormalized(state.Q, state.N);
        var magnitude = value.GradientMagnitude;
        if (!double.IsFinite(magnitude) || magnitude < gradientMinimum)
        {
            return (new State(0, 0), false);
        }

        return (new State(
            direction * value.Dq / magnitude,
            direction * value.Dn / magnitude), true);
    }

    private static State Combine(State origin, double step, params (double Weight, State Vector)[] terms)
    {
        var q = origin.Q;
        var n = origin.N;
        foreach (var (weight, vector) in terms)
        {
            q += step * weight * vector.Q;
            n += step * weight * vector.N;
        }

        return new State(q, n);
    }

    private static double FindEventFraction(RkTrial trial, Func<State, double> eventFunction)
    {
        var left = 0.0;
        var right = 1.0;
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var middle = (left + right) / 2;
            if (eventFunction(trial.Dense(middle)) > 0)
            {
                left = middle;
            }
            else
            {
                right = middle;
            }
        }

        return (left + right) / 2;
    }

    private static double BoundaryDistance(State state)
        => 0.5 - Math.Max(Math.Abs(state.Q - 0.5), Math.Abs(state.N - 0.5));

    private static double GradientDistance(KlaSurface surface, State state, double minimum)
        => surface.EvaluateNormalized(state.Q, state.N).GradientMagnitude - minimum;

    private static IReadOnlyList<KlaPathPoint> ToPhysical(
        KlaDomain domain,
        IReadOnlyList<NormalizedPathPoint> points)
        => points.Select(point => new KlaPathPoint(
            point.Q,
            point.N,
            domain.DenormalizeAirflow(point.Q),
            domain.DenormalizeAgitation(point.N),
            point.Kla,
            point.Headroom)).ToArray();

    private static double ScoreHeadroom(IReadOnlyList<NormalizedPathPoint> path)
        => path.Count < 2 ? 0 : path.Average(point => point.Headroom);

    private static double Headroom(double q, double n)
        => Math.Min(Math.Min(q, 1 - q), Math.Min(n, 1 - n));

    private static double Lerp(double minimum, double maximum, int index, int count)
        => minimum + ((maximum - minimum) * index / (count - 1));

    private static bool AllFinite(params double[] values) => values.All(double.IsFinite);

    private static double RootMeanSquare(double first, double second)
        => Math.Sqrt(((first * first) + (second * second)) / 2);

    private readonly record struct State(double Q, double N);

    private readonly record struct RkTrial(
        State Origin,
        double Step,
        State Fifth,
        State Fourth,
        State K1,
        State K3,
        State K4,
        State K5,
        State K6,
        State K7)
    {
        public State Dense(double fraction)
        {
            var squared = fraction * fraction;
            var cubed = squared * fraction;
            var fourth = cubed * fraction;
            return new State(
                Origin.Q + (Step * Component(
                    K1.Q, K3.Q, K4.Q, K5.Q, K6.Q, K7.Q,
                    fraction, squared, cubed, fourth)),
                Origin.N + (Step * Component(
                    K1.N, K3.N, K4.N, K5.N, K6.N, K7.N,
                    fraction, squared, cubed, fourth)));
        }

        private static double Component(
            double k1,
            double k3,
            double k4,
            double k5,
            double k6,
            double k7,
            double x,
            double x2,
            double x3,
            double x4)
        {
            var q1 = k1;
            var q2 =
                (k1 * (-8048581381.0 / 2820520608.0)) +
                (k3 * (131558114200.0 / 32700410799.0)) +
                (k4 * (-1754552775.0 / 470086768.0)) +
                (k5 * (127303824393.0 / 49829197408.0)) +
                (k6 * (-282668133.0 / 205662961.0)) +
                (k7 * (40617522.0 / 29380423.0));
            var q3 =
                (k1 * (8663915743.0 / 2820520608.0)) +
                (k3 * (-68118460800.0 / 10900136933.0)) +
                (k4 * (14199869525.0 / 1410260304.0)) +
                (k5 * (-318862633887.0 / 49829197408.0)) +
                (k6 * (2019193451.0 / 616988883.0)) +
                (k7 * (-110615467.0 / 29380423.0));
            var q4 =
                (k1 * (-12715105075.0 / 11282082432.0)) +
                (k3 * (87487479700.0 / 32700410799.0)) +
                (k4 * (-10690763975.0 / 1880347072.0)) +
                (k5 * (701980252875.0 / 199316789632.0)) +
                (k6 * (-1453857185.0 / 822651844.0)) +
                (k7 * (69997945.0 / 29380423.0));
            return (q1 * x) + (q2 * x2) + (q3 * x3) + (q4 * x4);
        }
    }

    private sealed record NormalizedPathPoint(double Q, double N, double Kla, double Headroom);
}
