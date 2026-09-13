from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / "firmware" / "flask-agitator" / "src"


def read(relative: str) -> str:
    return (SRC / relative).read_text(encoding="utf-8")


def test_boot_is_braked_and_potentiometer_starts_locked() -> None:
    setup = read("core/FirmwareApp.cpp")
    context = read("core/AppContext.cpp")
    config = read("config/BoardConfig.h")

    assert "brakeMotor();" in setup
    assert "BoostDuty" not in setup
    assert "BoostDuty" not in config
    assert "volatile bool potEnabled = false;" in context


def test_direction_change_ramps_to_zero_before_switching_leg() -> None:
    motor = read("motor/MotorDriver.cpp")
    config = read("config/BoardConfig.h")

    assert "DirectionRampDurationMs = 200" in config
    assert "if (targetDirRight != appliedDirectionRight)" in motor
    assert "appliedDuty = moveTowards(appliedDuty, 0);" in motor
    assert motor.index("appliedDuty = moveTowards(appliedDuty, 0);") < motor.index(
        "appliedDirectionRight = targetDirRight;"
    )
    assert "writeBridge(appliedDuty, appliedDirectionRight);" in motor


def test_identity_has_one_v10_source_for_hello_diag_and_ota() -> None:
    config = read("config/BoardConfig.h")
    hub = read("network/HubClient.cpp")
    api = read("api/LocalHttpApi.cpp")

    assert 'FirmwareVersion = "v10"' in config
    assert "BoardConfig::FirmwareVersion" in hub
    assert api.count("BoardConfig::FirmwareVersion") >= 2
    assert '"version":"1.0"' not in api


def test_unimplemented_current_sensor_pins_are_not_claimed() -> None:
    config = read("config/BoardConfig.h")

    assert "RightCurrentSensePin" not in config
    assert "LeftCurrentSensePin" not in config
