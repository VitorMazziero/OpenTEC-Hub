using System.Text.Json.Serialization;

namespace OpenTECHub.Services.KlaTesting;

/// <summary>Physical route confirmations shared by acquisition and offline analysis.</summary>
public enum KlaGasEventKind { GasOffConfirmed, GasOnConfirmed }

public sealed record KlaGasEvent(
    [property: JsonNumberHandling(JsonNumberHandling.AllowNamedFloatingPointLiterals)] double Seconds,
    KlaGasEventKind Kind, string Evidence);
