# pH and oxygen calibration verification

Images are rendered from the shared SensorCalibrationView inside CalibrationView,
using in-memory test readings. They are not hardware measurements.

- ph.png / oxygen.png: waiting for the first standard.
- *-change.png: point 1 complete, explicit buffer/standard change and confirmation.
- *-review.png: proposed curve, before settings change.
- *-saved.png: applied curve and completed steps.

SensorCalibrationWorkflowTests verifies that incoming telemetry cannot acquire
point 2 before operator confirmation, and that each applied curve survives saving
and reopening settings and is used by the telemetry parser. Calibration and
compact-layout checks cover both sensor tabs and the supported window sizes.
