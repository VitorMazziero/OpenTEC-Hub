namespace OpenTECHub.Services.KlaTesting;

public sealed partial class KlaTestRunner
{
    /// <summary>Records an interrupted preparation without starting acquisition or requiring a sensor sample.</summary>
    internal KlaTestRun RecordCancelledRecipePreparation(KlaTestDocument document, KlaTestCondition condition, int replicateNumber)
    {
        lock (_gate)
        {
            if (!_recipeAcquisitionSealed || _recipeLease is null || _recipeReturnSnapshot is null || _currentRun is not null)
                throw new InvalidOperationException("Registro sem aquisição requer runner de receita encerrado e sem corrida.");
            _currentTest = document;
            _currentCondition = condition;
            var definition = KlaRunDefinition.Create(document, condition, replicateNumber);
            var run = new KlaTestRun
            {
                Definition = definition, AttemptNumber = definition.AttemptNumber, Context = definition.Context,
                TestId = document.TestId, ConditionId = condition.ConditionId, ReplicateNumber = replicateNumber,
                AgitationRpm = condition.AgitationRpm, AirflowLpm = condition.AirflowLpm,
                CurrentPhase = RunPhase.RestoringCultivation, StartedUtc = _time.GetUtcNow(),
                Outcome = new() { Restoration = KlaRestorationState.Pending, KlaQuality = KlaScientificQuality.Inconclusive,
                    OurQuality = document.EffectiveProtocol == KlaAssayProtocol.Abiotic
                        ? KlaScientificQuality.NotApplicable : KlaScientificQuality.NotEvaluated }
            };
            _currentRun = run;
            run.FolderName = _store.InitializeRunFolder(document.FolderName, run);
            _phase = RunPhase.RestoringCultivation;
            _statusMessage = "Preparação cancelada sem aquisição; restaurando snapshot da receita.";
            _store.SaveTestManifest(document);
            return run;
        }
    }
}
