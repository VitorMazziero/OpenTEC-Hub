"""
firmware.py -- locate compiled images and read the version declared in source.

Binary discovery repeats the search order of ``tools/Publish-OtaFirmware.ps1``
so both publishers pick the same image: the updater's own build directory
first, then the sketch's Arduino IDE build folders, then the sketch root.

Images whose name marks them as the merged, bootloader or partition binary are
excluded. That is not cosmetic: the firmwares reject such a filename mid-upload,
so offering one would only fail after the transfer had already started.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Optional

from .devices import REJECTED_BIN_MARKERS, Device


@dataclass(frozen=True)
class BinaryInfo:
    path: Path
    size_bytes: int
    modified: datetime

    @property
    def size_kb(self) -> float:
        return self.size_bytes / 1024.0

    @property
    def summary(self) -> str:
        return "{} ({:.1f} KB)".format(self.path.name, self.size_kb)

    @property
    def modified_text(self) -> str:
        return self.modified.strftime("%d/%m/%Y %H:%M")


def is_application_image(path: Path) -> bool:
    """False for merged/bootloader/partition images, which the node refuses."""
    name = path.name.lower()
    return not any(marker in name for marker in REJECTED_BIN_MARKERS)


def search_paths(device: Device) -> list:
    sketch = device.sketch_dir
    return [
        device.build_dir,
        sketch / "build" / "esp32.esp32.esp32",
        sketch / "build" / "esp32.esp32.esp32s3",
        sketch,
    ]


def find_binaries(device: Device) -> list:
    """Candidate application images for the device, most recently built first."""
    found = {}
    for directory in search_paths(device):
        if not directory.is_dir():
            continue
        for path in directory.glob("*.bin"):
            if not path.is_file() or not is_application_image(path):
                continue
            resolved = path.resolve()
            if resolved in found:
                continue
            stat = path.stat()
            found[resolved] = BinaryInfo(
                path=path,
                size_bytes=stat.st_size,
                modified=datetime.fromtimestamp(stat.st_mtime),
            )
    return sorted(found.values(), key=lambda item: item.modified, reverse=True)


def latest_binary(device: Device) -> Optional[BinaryInfo]:
    binaries = find_binaries(device)
    return binaries[0] if binaries else None


def describe(path: Path) -> BinaryInfo:
    """BinaryInfo for a path the operator chose by hand."""
    stat = path.stat()
    return BinaryInfo(
        path=path,
        size_bytes=stat.st_size,
        modified=datetime.fromtimestamp(stat.st_mtime),
    )


def repo_version(device: Device) -> Optional[str]:
    """Version declared in the firmware source, i.e. what a fresh build reports."""
    path = device.version_path
    if not path.is_file():
        return None
    try:
        text = path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return None
    match = re.search(device.version_regex, text)
    return match.group(1) if match else None
