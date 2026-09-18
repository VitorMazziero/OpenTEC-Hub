"""Check the device catalogue against the repository and the PowerShell publisher.

The updater and ``tools/Publish-OtaFirmware.ps1`` flash the same five devices.
If their default addresses, board definitions or sketch paths ever drift apart,
an operator gets a different result depending on which publisher was used, so
the two tables are compared field by field here.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from updater import firmware  # noqa: E402
from updater.devices import DEVICES, DEVICES_BY_KEY, TOOLS_DIR  # noqa: E402

PUBLISHER = TOOLS_DIR / "Publish-OtaFirmware.ps1"

failures: list[str] = []


def check(condition: bool, message: str) -> None:
    if condition:
        print(f"  ok   {message}")
    else:
        print(f"  FAIL {message}")
        failures.append(message)


def parse_publisher() -> dict[str, dict[str, str]]:
    """Read the $deviceMeta table out of the PowerShell publisher."""
    text = PUBLISHER.read_text(encoding="utf-8", errors="replace")
    entries: dict[str, dict[str, str]] = {}
    # Each entry looks like:  'pump' = @{ Name = '...'  DefaultApIp = '...' ... }
    for match in re.finditer(r"'(\w+)'\s*=\s*@\{(.*?)\n    \}", text, re.S):
        key, body = match.group(1), match.group(2)
        fields = {}
        for field in ("Name", "DefaultApIp", "Fqbn", "RelativePath", "BinPattern"):
            found = re.search(field + r"\s*=\s*'([^']*)'", body)
            if found:
                fields[field] = found.group(1)
        if fields:
            entries[key] = fields
    return entries


def main() -> int:
    print("Catalogo de dispositivos")

    check(len(DEVICES) == 5, "cinco dispositivos no catalogo")

    for device in DEVICES:
        print(f"\n{device.name} [{device.key}]")
        check(device.sketch_dir.is_dir(), f"sketch existe: {device.relative_path}")
        check(device.version_path.is_file(), f"fonte da versao existe: {device.version_file}")

        version = firmware.repo_version(device)
        check(bool(version), f"versao declarada no repositorio: {version!r}")

        binaries = firmware.find_binaries(device)
        excluded = [b for b in binaries if not firmware.is_application_image(b.path)]
        check(not excluded, "nenhuma imagem merged/bootloader/partitions oferecida")

    print("\nParidade com Publish-OtaFirmware.ps1")
    check(PUBLISHER.is_file(), "publicador PowerShell encontrado")
    if PUBLISHER.is_file():
        publisher = parse_publisher()
        check(
            set(publisher) == set(DEVICES_BY_KEY),
            f"mesmas chaves de dispositivo: {sorted(publisher)}",
        )
        for key, fields in publisher.items():
            device = DEVICES_BY_KEY.get(key)
            if device is None:
                continue
            check(
                device.default_ap_ip == fields.get("DefaultApIp"),
                f"{key}: IP do AP padrao {device.default_ap_ip}",
            )
            check(
                device.fqbn == fields.get("Fqbn"),
                f"{key}: FQBN identico ao publicador",
            )
            check(
                device.relative_path.replace("/", "\\") == fields.get("RelativePath"),
                f"{key}: caminho do sketch identico ao publicador",
            )

    print()
    if failures:
        print(f"{len(failures)} verificacao(oes) falharam.")
        return 1
    print("Catalogo consistente.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
