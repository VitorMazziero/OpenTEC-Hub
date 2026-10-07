using System;
using OpenTECHub.Protocol;
using OpenTECHub.Services.Communication;
using OpenTECHub.Services.Control;
using OpenTECHub.Services.Persistence;

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

    public KlaAssayCoordinator(ICommandArbiter arbiter, ICascadeService? cascade, IOurSoftSensor? our)
    {
        _arbiter = arbiter; _cascade = cascade; _our = our;
    }

    public void Validate()
    {
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
}
