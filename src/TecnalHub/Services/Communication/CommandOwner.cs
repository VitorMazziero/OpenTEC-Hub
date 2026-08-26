namespace TecnalHub.Services.Communication;

/// <summary>
/// Who is allowed to put commands on the wire for a given actuator.
/// </summary>
/// <remarks>
/// <para>
/// <b>One command queue, one owner.</b> A running recipe and an operator must not be
/// able to fight over the link. Making ownership explicit and enforced by
/// <see cref="ICommandArbiter"/> is cheaper than discovering mid-cultivation that two
/// things were writing setpoints. Ownership is tracked per <see cref="TecnalHub.Protocol.ActuatorId"/>,
/// so the cascade can own the oxygen actuators while the operator still holds temperature.
/// </para>
/// <para>
/// Only <see cref="Manual"/> is reachable from the UI today: <see cref="Automatic"/>
/// arrives with live cascade actuation (Phase 2 WP6) and <see cref="Recipe"/> with the
/// recipe engine (Phase 3). The arbiter and this vocabulary ship now so the model the
/// application is built around is real and enforced from the start, not retrofitted onto
/// twelve control surfaces later.
/// </para>
/// </remarks>
public enum CommandOwner
{
    /// <summary>The operator writes setpoints. The default owner of every actuator.</summary>
    Manual,

    /// <summary>The cascade controller owns its actuators. Phase 2 WP6.</summary>
    Automatic,

    /// <summary>The recipe engine owns everything it declares. Phase 3.</summary>
    Recipe,

    /// <summary>The kLa determination test runner owns agitation and airflow/valves. Phase 2.</summary>
    KlaAssay,
}
