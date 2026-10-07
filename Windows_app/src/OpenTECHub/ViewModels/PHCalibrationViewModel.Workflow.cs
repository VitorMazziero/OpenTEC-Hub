using System.Globalization;
using System.Windows.Input;
using OpenTECHub.Services.Persistence;

namespace OpenTECHub.ViewModels;

public sealed partial class PHCalibrationViewModel
{
    public string SettingsPath => AppPaths.SettingsFile;

    public SensorCalibrationWorkflow Workflow => SensorCalibrationWorkflow.Create(
        Stage switch
        {
            PHCalibrationStage.AwaitingFirstBuffer => SensorCalibrationPhase.AwaitingFirst,
            PHCalibrationStage.StabilizingFirst => SensorCalibrationPhase.StabilizingFirst,
            PHCalibrationStage.AveragingFirst => SensorCalibrationPhase.AveragingFirst,
            PHCalibrationStage.AwaitingSecondBuffer => SensorCalibrationPhase.AwaitingSecond,
            PHCalibrationStage.StabilizingSecond => SensorCalibrationPhase.StabilizingSecond,
            PHCalibrationStage.AveragingSecond => SensorCalibrationPhase.AveragingSecond,
            PHCalibrationStage.Proposed => SensorCalibrationPhase.Review,
            PHCalibrationStage.Applied => SensorCalibrationPhase.Saved,
            PHCalibrationStage.Failed => SensorCalibrationPhase.Failed,
            _ => SensorCalibrationPhase.Setup,
        },
        IsAwaitingOperator || IsAcquiring || Stage is PHCalibrationStage.Proposed or PHCalibrationStage.Applied ? _twoPoint : IsTwoPoint,
        ph: true,
        IsAwaitingOperator || IsAcquiring ? _reference1.ToString(CultureInfo.CurrentCulture) : Reference1Text,
        IsAwaitingOperator || IsAcquiring ? _reference2.ToString(CultureInfo.CurrentCulture) : Reference2Text);

    public ICommand PrimaryActionCommand => Stage switch
    {
        PHCalibrationStage.Proposed => ApplyProposalCommand,
        PHCalibrationStage.AwaitingFirstBuffer or PHCalibrationStage.AwaitingSecondBuffer
            or PHCalibrationStage.StabilizingFirst or PHCalibrationStage.AveragingFirst
            or PHCalibrationStage.StabilizingSecond or PHCalibrationStage.AveragingSecond => ConfirmPointCommand,
        _ => StartProcedureCommand,
    };

    partial void OnReference1TextChanged(string value) => OnPropertyChanged(nameof(Workflow));
    partial void OnReference2TextChanged(string value) => OnPropertyChanged(nameof(Workflow));
}