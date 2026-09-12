using System.Globalization;
using System.Text;

namespace OpenTECHub.Protocol;

/// <summary>
/// Builder for a command object sent to the ESP32-S3: a single flat JSON object,
/// no nesting, no arrays.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type exists to make a culture bug impossible.</b> Every value reaching
/// the wire is formatted here with <see cref="CultureInfo.InvariantCulture"/>. On a
/// pt-BR machine, naive formatting emits <c>6,98</c> where the firmware expects
/// <c>6.98</c> and the parse fails. There is deliberately no way to put a raw
/// string fragment into this builder.
/// </para>
/// <para>
/// Insertion order is preserved, and re-setting an existing key updates the value
/// <i>in place</i> without moving it. This mirrors Python's <c>dict.update</c>
/// semantics, which is what v.6 relies on when it merges buffered commands.
/// </para>
/// <para>See <c>docs/PROTOCOL.md</c> section 3 for the key vocabulary.</para>
/// </remarks>
public sealed class OpenTECCommand
{
    private readonly List<KeyValuePair<string, string>> _fields = [];

    /// <summary>Number of keys currently in the command.</summary>
    public int Count => _fields.Count;

    /// <summary>True when nothing has been set; such a command must not be sent.</summary>
    public bool IsEmpty => _fields.Count == 0;

    /// <summary>Keys currently present, in emission order.</summary>
    public IEnumerable<string> Keys => _fields.Select(f => f.Key);

    public static OpenTECCommand Create() => new();

    /// <summary>Sets an integer value, e.g. <c>{"motorSetpoint":790}</c>.</summary>
    public OpenTECCommand Set(string key, int value)
        => SetRaw(key, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Sets a floating-point value, e.g. <c>{"flowSetpoint":2.5}</c>.
    /// </summary>
    /// <remarks>
    /// A whole number is emitted as <c>2.0</c>, not <c>2</c>. That reproduces
    /// Python's <c>repr(float)</c>, which is what v.6 puts on the wire. JSON treats
    /// the two as equal, so this is fidelity rather than necessity - but it keeps
    /// captured v.6 traffic byte-comparable against ours, which is the Phase 0
    /// exit criterion.
    /// </remarks>
    public OpenTECCommand Set(string key, double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(
                nameof(value), value, $"Non-finite value for command key '{key}'.");
        }

        var text = value.ToString("R", CultureInfo.InvariantCulture);

        // Give whole numbers the ".0" a Python float always carries.
        if (text.IndexOfAny(['.', 'e', 'E']) < 0)
        {
            text += ".0";
        }

        return SetRaw(key, text);
    }

    /// <summary>Sets a boolean as the integer <c>1</c> or <c>0</c> - the firmware has no JSON bool inputs.</summary>
    public OpenTECCommand Set(string key, bool value) => SetRaw(key, value ? "1" : "0");

    /// <summary>Sets a string value as a quoted JSON string, e.g. <c>{"pump_command":"reset_volume"}</c>.</summary>
    public OpenTECCommand Set(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SetRaw(key, "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
    }

    /// <summary>
    /// Sets a value as a <b>quoted JSON string</b> with a fixed number of decimals.
    /// </summary>
    /// <remarks>
    /// Required for <c>pHCal</c>, which v.6 sends as <c>{"pHCal":"6.98"}</c> - a
    /// string, not a number. See <c>docs/PROTOCOL.md</c> section 2.2.
    /// </remarks>
    public OpenTECCommand SetFixedString(string key, double value, int decimals)
    {
        var text = value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture),
                                  CultureInfo.InvariantCulture);
        return SetRaw(key, "\"" + text + "\"");
    }

    /// <summary>
    /// Merges <paramref name="other"/> into this command. Keys already present keep
    /// their position but take the new value; new keys append.
    /// </summary>
    public OpenTECCommand Merge(OpenTECCommand other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var (key, value) in other._fields)
        {
            SetRaw(key, value);
        }

        return this;
    }

    /// <summary>True when <paramref name="key"/> is present.</summary>
    public bool Contains(string key) => IndexOf(key) >= 0;

    /// <summary>
    /// The raw JSON fragment stored for <paramref name="key"/>, or null. Used by the
    /// connection manager to recognise the pH echo it just sent.
    /// </summary>
    public string? GetRawValue(string key)
    {
        var i = IndexOf(key);
        return i < 0 ? null : _fields[i].Value;
    }

    /// <summary>Serialises to compact JSON: <c>{"a":1,"b":2}</c>, no whitespace.</summary>
    public string ToJson()
    {
        var sb = new StringBuilder(_fields.Count * 24 + 2);
        sb.Append('{');
        for (var i = 0; i < _fields.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append('"').Append(_fields[i].Key).Append("\":").Append(_fields[i].Value);
        }

        return sb.Append('}').ToString();
    }

    public override string ToString() => ToJson();

    private OpenTECCommand SetRaw(string key, string jsonFragment)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);

        var i = IndexOf(key);
        if (i >= 0)
        {
            _fields[i] = new KeyValuePair<string, string>(key, jsonFragment);
        }
        else
        {
            _fields.Add(new KeyValuePair<string, string>(key, jsonFragment));
        }

        return this;
    }

    private int IndexOf(string key)
    {
        for (var i = 0; i < _fields.Count; i++)
        {
            if (string.Equals(_fields[i].Key, key, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}
