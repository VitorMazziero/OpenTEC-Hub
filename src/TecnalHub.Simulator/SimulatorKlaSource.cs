using System.Globalization;
using System.Text.Json;

namespace TecnalHub.Simulator;

/// <summary>
/// Provides a volumetric oxygen transfer coefficient as a function of agitation and aeration.
/// </summary>
public interface IKlaSource
{
    /// <summary>Evaluates kLa (in 1/s) at the given actuator setpoints.</summary>
    double Evaluate(int rpm, double flowLpm);

    /// <summary>Short description for console logging.</summary>
    string Description { get; }
}

/// <summary>
/// The default power-law placeholder: <c>kLa = 0.055 · (N/1000)^0.62 · (Q/10)^0.38</c> (1/s).
/// Monotonic in both actuators with diminishing returns.
/// </summary>
public sealed class PowerLawKla : IKlaSource
{
    public string Description => "power-law placeholder (kLa = 0.055·N^0.62·Q^0.38 s⁻¹)";

    public double Evaluate(int rpm, double flowLpm)
    {
        var n = Math.Max(rpm, 0) / 1000.0;
        var q = Math.Max(flowLpm, 0.0) / 10.0;
        return 0.055 * Math.Pow(n, 0.62) * Math.Pow(q, 0.38);
    }
}

/// <summary>
/// Evaluates kLa by interpolating an in-app published kLa mapping receipt (<c>KlaPublishedProfile</c>).
/// Converts receipt units (1/h) to simulator dynamics units (1/s).
/// </summary>
public sealed class ProfileKla : IKlaSource
{
    public sealed record Sample(double KlaPerHour, double AirflowLpm, double AgitationRpm);

    private readonly IReadOnlyList<Sample> _allocation;
    private readonly string _profileName;

    public ProfileKla(IReadOnlyList<Sample> allocation, string profileName = "Published Profile")
    {
        if (allocation == null || allocation.Count == 0)
        {
            throw new ArgumentException("Allocation table must not be empty.", nameof(allocation));
        }

        _allocation = allocation.OrderBy(s => s.KlaPerHour).ToList();
        _profileName = profileName;
    }

    public string Description => $"published receipt '{_profileName}' ({_allocation.Count} path samples, {_allocation[0].KlaPerHour:F1}–{_allocation[^1].KlaPerHour:F1} h⁻¹)";

    public double Evaluate(int rpm, double flowLpm)
    {
        var targetRpm = Math.Max(0.0, rpm);
        var targetFlow = Math.Max(0.0, flowLpm);

        var bestDistSq = double.MaxValue;
        var bestKlaPerHour = _allocation[0].KlaPerHour;

        foreach (var sample in _allocation)
        {
            var dFlow = (sample.AirflowLpm - targetFlow) / 50.0;
            var dRpm = (sample.AgitationRpm - targetRpm) / 1200.0;
            var distSq = (dFlow * dFlow) + (dRpm * dRpm);

            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestKlaPerHour = sample.KlaPerHour;
            }
        }

        return bestKlaPerHour / 3600.0;
    }

    public static ProfileKla FromJsonFile(string path)
    {
        var text = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        JsonElement payload;
        if (root.TryGetProperty("payload", out var p))
        {
            payload = p;
        }
        else if (root.TryGetProperty("Payload", out var pUpper))
        {
            payload = pUpper;
        }
        else
        {
            payload = root;
        }

        var name = payload.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? Path.GetFileNameWithoutExtension(path) : Path.GetFileNameWithoutExtension(path);

        JsonElement allocEl;
        if (!payload.TryGetProperty("allocation", out allocEl) && !payload.TryGetProperty("Allocation", out allocEl))
        {
            throw new InvalidOperationException("Profile JSON does not contain an 'allocation' array.");
        }

        var samples = new List<Sample>();
        foreach (var item in allocEl.EnumerateArray())
        {
            var kla = GetDouble(item, "klaPerHour", "KlaPerHour");
            var flow = GetDouble(item, "airflowLpm", "AirflowLpm");
            var rpm = GetDouble(item, "agitationRpm", "AgitationRpm");
            samples.Add(new Sample(kla, flow, rpm));
        }

        return new ProfileKla(samples, name);
    }

    private static double GetDouble(JsonElement element, string propName1, string propName2)
    {
        if (element.TryGetProperty(propName1, out var p1) && p1.TryGetDouble(out var v1))
        {
            return v1;
        }

        if (element.TryGetProperty(propName2, out var p2) && p2.TryGetDouble(out var v2))
        {
            return v2;
        }

        return 0.0;
    }
}
