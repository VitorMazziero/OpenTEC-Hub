using System.Globalization;
using System.Windows.Input;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.ViewModels;

public sealed partial class OxygenCalibrationViewModel
{
    public string SettingsPath => AppPaths.SettingsFile;

    public SensorCalibrationWorkflow Workflow => SensorCalibrationWorkflow.Create(
        Stage switch
        {
            OxygenCalibrationStage.AwaitingFirstStandard => SensorCalibrationPhase.AwaitingFirst,
            OxygenCalibrationStage.StabilizingFirst => SensorCalibrationPhase.StabilizingFirst,
            OxygenCalibrationStage.AveragingFirst => SensorCalibrationPhase.AveragingFirst,
            OxygenCalibrationStage.AwaitingSecondStandard => SensorCalibrationPhase.AwaitingSecond,
            OxygenCalibrationStage.StabilizingSecond => SensorCalibrationPhase.StabilizingSecond,
            OxygenCalibrationStage.AveragingSecond => SensorCalibrationPhase.AveragingSecond,
            OxygenCalibrationStage.Proposed => SensorCalibrationPhase.Review,
            OxygenCalibrationStage.Applied => SensorCalibrationPhase.Saved,
            OxygenCalibrationStage.Failed => SensorCalibrationPhase.Failed,
            _ => SensorCalibrationPhase.Setup,
        },
        IsAwaitingOperator || IsAcquiring || Stage is OxygenCalibrationStage.Proposed or OxygenCalibrationStage.Applied ? _twoPoint : IsTwoPoint,
        ph: false,
        IsAwaitingOperator || IsAcquiring ? _reference1.ToString(CultureInfo.CurrentCulture) : Reference1Text,
        IsAwaitingOperator || IsAcquiring ? _reference2.ToString(CultureInfo.CurrentCulture) : Reference2Text);

    public ICommand PrimaryActionCommand => Stage switch
    {
        OxygenCalibrationStage.Proposed => ApplyProposalCommand,
        OxygenCalibrationStage.AwaitingFirstStandard or OxygenCalibrationStage.AwaitingSecondStandard
            or OxygenCalibrationStage.StabilizingFirst or OxygenCalibrationStage.AveragingFirst
            or OxygenCalibrationStage.StabilizingSecond or OxygenCalibrationStage.AveragingSecond => ConfirmPointCommand,
        _ => StartProcedureCommand,
    };

    partial void OnReference1TextChanged(string value) => OnPropertyChanged(nameof(Workflow));
    partial void OnReference2TextChanged(string value) => OnPropertyChanged(nameof(Workflow));
}