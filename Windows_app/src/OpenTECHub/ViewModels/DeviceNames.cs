namespace OpenTECHub.ViewModels;

/// <summary>
/// The one place each device is named, for every surface that shows or matches that name.
/// </summary>
/// <remarks>
/// <para>
/// These strings are not only labels. Three separate mechanisms match on them, and a rename
/// that touched one and missed another would fail silently rather than loudly:
/// </para>
/// <list type="bullet">
/// <item>
/// <b>Routing-mismatch keys.</b> <c>ControlViewModel</c> reports what the operator's switch
/// says under a name, and <c>AlarmService</c> compares it against the Hub's echo under the
/// same name. Spelled differently in the two files, the comparison simply never happens and
/// the alarm goes quiet - which is the failure it exists to catch.
/// </item>
/// <item>
/// <b>Synoptic drag tags.</b> A card carries its name as its drag payload, and the drop
/// handler routes on it. A tag the handler does not recognise falls through to a keyword
/// match and lands on whatever it happens to contain.
/// </item>
/// <item>
/// <b>Recipe device names</b> and the asset tests that pin them.
/// </item>
/// </list>
/// <para>
/// <b>What each name used to be</b>, so an old screenshot, log line or recipe still reads:
/// </para>
/// <list type="table">
/// <item><term>Nutrientes</term><description>era "Dosagem de Nutrientes"</description></item>
/// <item><term>Antiespumante</term><description>era "Dosagem de Antiespumante"</description></item>
/// <item><term>Bomba Externa</term><description>era "Bomba Dosadora Externa" / "Bomba externa"</description></item>
/// <item><term>Distância</term><description>era "Sensor de Distância"</description></item>
/// <item><term>Absorbância</term><description>era "Sensor de Biomassa"</description></item>
/// </list>
/// <para>
/// The last one is not a shortening. The sensor reports <b>absorbance</b> - <c>BiomassAbs</c>
/// on the wire, charted in <c>Abs</c> - and the card now says so. Transmittance is a
/// different number, <c>A = −log₁₀(T)</c>, so 0.5 Abs is 31.6 % transmittance; labelling the
/// one as the other would misname the measurement rather than rename the card.
/// </para>
/// </remarks>
public static class DeviceNames
{
    // ---- Internal process variables --------------------------------------

    public const string Agitation = "Agitação";
    public const string Temperature = "Temperatura";
    public const string PH = "pH";
    public const string Oxygen = "Oxigênio";
    public const string Pressure = "Alívio de Pressão";

    /// <summary>Was "Dosagem de Nutrientes".</summary>
    public const string Nutrient = "Nutrientes";

    /// <summary>Was "Dosagem de Antiespumante".</summary>
    public const string Antifoam = "Antiespumante";

    // ---- External devices -------------------------------------------------

    public const string Airflow = "Vazão de Ar";

    /// <summary>Was "Sensor de Distância".</summary>
    public const string Distance = "Distância";

    /// <summary>
    /// Was "Sensor de Biomassa". Named for the quantity it reports, which is absorbance.
    /// </summary>
    public const string Absorbance = "Absorbância";

    /// <summary>Was "Bomba Dosadora Externa" and, in the alarm keys, "Bomba externa".</summary>
    public const string ExternalPump = "Bomba Externa";

    public const string FlaskAgitator = "Frasco Agitador";

    public const string ExternalBath = "Banho externo C404";

    /// <summary>
    /// The ASDA-B2 drive. Not an external device: it lives in the Agitação drawer.
    /// </summary>
    public const string ServoDrive = "Servo drive";

    // ---- Routing-mismatch keys -------------------------------------------
    //
    // The pairing that must not drift. Both halves - the operator's switch in
    // ControlViewModel and the Hub's echo in AlarmService - read from here, so the two can
    // no longer be spelled differently in two files.

    public static class Routing
    {
        public const string Airflow = DeviceNames.Airflow;
        public const string Absorbance = DeviceNames.Absorbance;
        public const string ExternalPump = DeviceNames.ExternalPump;
        public const string Distance = DeviceNames.Distance;
        public const string ServoDrive = DeviceNames.ServoDrive;
        public const string FlaskAgitator = DeviceNames.FlaskAgitator;
        public const string ExternalBath = DeviceNames.ExternalBath;
    }
}
