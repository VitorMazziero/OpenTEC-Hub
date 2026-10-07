using System;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.Persistence;
using OpenTECHub.Services.Recipes;

namespace OpenTECHub.Services.KlaTesting;

public sealed record KlaAcquisitionMetadata(double OxygenCalibrationA, double OxygenCalibrationB,
    DateTimeOffset CapturedUtc, double OxygenSampleTimeoutSeconds,
    double? ReturnAgitationRpm, double? ReturnAirflowLpm);

/// <summary>Owns only the assay actuators; recipes and other producers are never silently displaced.</summary>
public sealed class KlaAssayCoordinator
{
    private readonly ICommandArbiter _arbiter;
    private readonly ICascadeService? _cascade;
    private readonly IOurSoftSensor? _our;
    private bool _resumeCascade;
    private bool _acquired;
    private readonly RecipeAssayResourceLease? _recipeLease;

    public KlaAssayCoordinator(ICommandArbiter arbiter, ICascadeService? cascade, IOurSoftSensor? our,
        RecipeAssayResourceLease? recipeLease = null)
    {
        _arbiter = arbiter; _cascade = cascade; _our = our; _recipeLease = recipeLease;
    }

    public void Validate()
    {
        if (_recipeLease is not null)
        {
            if (!_recipeLease.IsAssayAuthorityCurrent)
                throw new InvalidOperationException("Reserva de receita encerrada ou revogada.");
            return;
        }
        foreach (var actuator in new[] { ActuatorId.Agitation, ActuatorId.Aeration })
        {
            var owner = _arbiter.OwnerOf(actuator);
            if (owner != CommandOwner.Manual && !(owner == CommandOwner.Automatic && _cascade?.IsEngaged == true))
            {
                throw new InvalidOperationException($"{actuator} pertence a {owner}. Encerre a operação concorrente antes do ensaio.");
            }
        }
    }

    public void Acquire(string reason)
    {
        Validate();
        if (_recipeLease is not null)
        {
            _our?.SuspendForKlaAssay();
            _acquired = true;
            return;
        }
        _resumeCascade = _cascade?.IsEngaged == true;
        _cascade?.SuspendForKlaAssay();
        _our?.SuspendForKlaAssay();
        _arbiter.Claim(CommandOwner.KlaAssay, [ActuatorId.Agitation, ActuatorId.Aeration], reason);
        _acquired = true;
    }

    public bool Release(bool restored, double rpm, double flow, string reason)
    {
        if (!_acquired)
        {
            return true;
        }
        if (_recipeLease is not null)
        {
            // Runner terminal/review is not return authority. Full recovery and durable evidence
            // are required before the outer recipe coordinator can release the reservation.
            return _recipeLease.IsAssayAuthorityCurrent;
        }
        _arbiter.Release(CommandOwner.KlaAssay, reason);
        var resumed = true;
        if (restored)
        {
            _cascade?.ResumeAfterKlaAssay(_resumeCascade, rpm, flow);
            resumed = !_resumeCascade || _cascade?.IsEngaged == true;
            if (resumed)
            {
                _our?.ResumeAfterKlaAssay();
            }
        }
        // On an unconfirmed restoration, both observers remain suspended for manual recovery.
        _acquired = false;
        return resumed;
    }

    public void CompleteRecipeReturn()
    {
        if (_recipeLease?.HasReturnedSuccessfully != true)
            throw new InvalidOperationException("Observadores só retomam após recuperação, persistência e devolução da receita.");
        if (_acquired) _our?.ResumeAfterKlaAssay();
        _acquired = false;
    }
}
