using System.Globalization;
using System.Reflection;

namespace OpenTECHub.Services.PowerTesting;

/// <summary>
/// Field-by-field difference between two <see cref="PowerTestSettings"/>, in the form the event log
/// records when the operator changes the criteria of an open assay (<c>SettingsChanged</c>). The
/// revision number already appears on every row of <c>serie-global.csv</c>; this is what says
/// <em>which</em> field moved, and from what to what.
/// </summary>
public static class PowerTestSettingsDiff
{
    public sealed record Change(string Field, string From, string To)
    {
        public override string ToString() => $"{Field}: {From} → {To}";
    }

    public static IReadOnlyList<Change> Compute(PowerTestSettings before, PowerTestSettings after)
    {
        var changes = new List<Change>();
        foreach (var property in typeof(PowerTestSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
            {
                continue;
            }

            var from = property.GetValue(before);
            var to = property.GetValue(after);
            if (!Equals(from, to))
            {
                changes.Add(new Change(property.Name, Format(from), Format(to)));
            }
        }

        return changes;
    }

    /// <summary>"Field: from → to; Field: from → to" — invariant culture, so the log is diffable.</summary>
    public static string Describe(PowerTestSettings before, PowerTestSettings after)
        => string.Join("; ", Compute(before, after));

    private static string Format(object? value) => value switch
    {
        null => "—",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
