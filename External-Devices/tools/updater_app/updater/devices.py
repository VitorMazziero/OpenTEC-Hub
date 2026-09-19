"""
devices.py -- catalogue of the six external devices this updater can flash.

Every field here is a contract shared with ``tools/Publish-OtaFirmware.ps1``,
with the Hub's ``/nodes`` route and with the firmware itself:

  * ``key`` is the name the node sends in ``/nodeHello?dev=<key>`` and the value
    the Hub echoes in the ``dev`` field of ``/nodes``. It is also the argument
    the PowerShell publisher accepts.
  * ``default_ap_ip`` is the address the device serves while running as its own
    access point, used whenever the Hub cannot say where the node is.
  * ``fqbn`` selects the board for arduino-cli. The flowmeter pins a full option
    string because its build depends on partition and flash settings.
  * ``bin_pattern`` documents the image arduino-cli writes for that sketch.

Changing any of these breaks compatibility between Hub, device and publisher,
so they are edited only alongside the firmware that defines them.

``version_file``/``version_regex`` point at the single line of firmware source
that declares the version the device will report once flashed. Each device
spells that declaration differently, so the pair is stated per device instead
of guessed by a generic scan.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

# tools/updater_app/updater/devices.py -> updater -> updater_app -> tools -> External-Devices
_HERE = Path(__file__).resolve()
TOOLS_DIR = _HERE.parents[2]
EXTERNAL_ROOT = _HERE.parents[3]

BUILD_ROOT = TOOLS_DIR / ".build"
ARDUINO_CLI = TOOLS_DIR / ".bin" / "arduino-cli.exe"
ARDUINO_CONFIG = TOOLS_DIR / "arduino-cli.local.yaml"

# Same default the PowerShell publisher uses. Overridable in the interface.
DEFAULT_LIBRARIES = Path(r"C:\Users\vitor\OneDrive\Documentos\Arduino\libraries")

HUB_DEFAULT_IP = "192.168.4.1"

# The firmwares refuse an upload whose filename contains any of these, because
# those images are not the application partition. The updater applies the same
# rule before sending so the rejection happens here and not mid-flash.
REJECTED_BIN_MARKERS = ("merged", "bootloader", "partitions")


@dataclass(frozen=True)
class Device:
    key: str
    name: str
    default_ap_ip: str
    fqbn: str
    relative_path: str
    bin_pattern: str
    version_file: str
    version_regex: str

    @property
    def sketch_dir(self) -> Path:
        return EXTERNAL_ROOT / self.relative_path

    @property
    def build_dir(self) -> Path:
        return BUILD_ROOT / self.key

    @property
    def version_path(self) -> Path:
        return EXTERNAL_ROOT / self.version_file


DEVICES: tuple[Device, ...] = (
    Device(
        key="distance",
        name="Sensor de Distancia",
        default_ap_ip="192.168.5.1",
        fqbn="esp32:esp32:esp32",
        relative_path="sensor-distancia/firmware/distance-sensor",
        bin_pattern="distance-sensor*.ino.bin",
        version_file="sensor-distancia/firmware/distance-sensor/src/config/BoardConfig.h",
        version_regex=r'FirmwareTag\s*=\s*"([^"]+)"',
    ),
    Device(
        key="agitator",
        name="Frasco Agitador",
        default_ap_ip="192.168.4.1",
        fqbn="esp32:esp32:esp32",
        relative_path="frasco-agitador/firmware/flask-agitator",
        bin_pattern="flask-agitator*.ino.bin",
        version_file="frasco-agitador/firmware/flask-agitator/src/config/BoardConfig.h",
        version_regex=r'FirmwareVersion\s*=\s*"([^"]+)"',
    ),
    Device(
        key="pump",
        name="Bomba Peristaltica",
        default_ap_ip="192.168.6.1",
        fqbn="esp32:esp32:esp32",
        relative_path="bomba-peristaltica/firmware/peristaltic-pump",
        bin_pattern="peristaltic-pump*.ino.bin",
        version_file="bomba-peristaltica/firmware/peristaltic-pump/src/core/FirmwareApp.cpp",
        version_regex=r'#define\s+PUMP_FW_VERSION\s+"([^"]+)"',
    ),
    Device(
        key="flowmeter",
        name="Fluxometro",
        default_ap_ip="192.168.10.1",
        fqbn=(
            "esp32:esp32:esp32:UploadSpeed=921600,CPUFreq=240,FlashFreq=80,"
            "FlashMode=qio,FlashSize=4M,PartitionScheme=default,DebugLevel=none,"
            "PSRAM=disabled,LoopCore=1,EventsCore=1,EraseFlash=none,"
            "JTAGAdapter=default,ZigbeeMode=default"
        ),
        relative_path="fluxometro/firmware/flowmeter",
        bin_pattern="flowmeter*.ino.bin",
        version_file="fluxometro/firmware/flowmeter/src/core/FirmwareApp.cpp",
        version_regex=r'#define\s+FW_VERSION\s+"([^"]+)"',
    ),
    Device(
        key="biomass",
        name="Sensor de Biomassa",
        default_ap_ip="192.168.7.1",
        fqbn="esp32:esp32:esp32s3",
        relative_path="sensor-biomassa/firmware/biomass-sensor",
        bin_pattern="biomass-sensor*.ino.bin",
        version_file="sensor-biomassa/firmware/biomass-sensor/src/core/FirmwareApp.cpp",
        version_regex=r'FW_VERSION\s*=\s*"([^"]+)"',
    ),
    Device(
        key="bath",
        name="Banho Termostatico",
        default_ap_ip="192.168.8.1",
        fqbn="esp32:esp32:esp32s3",
        relative_path="banho-termostatico/firmware/thermostatic-bath",
        bin_pattern="thermostatic-bath*.ino.bin",
        version_file="banho-termostatico/firmware/thermostatic-bath/src/config/BoardConfig.h",
        version_regex=r'FirmwareTag\s*=\s*"([^"]+)"',
    ),
)

DEVICES_BY_KEY = {device.key: device for device in DEVICES}
