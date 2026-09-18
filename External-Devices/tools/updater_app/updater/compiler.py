"""
compiler.py -- build a device's sketch with the bundled arduino-cli.

The command mirrors ``tools/Publish-OtaFirmware.ps1``: the local config file,
the device's FQBN, an explicit ``--libraries`` path and an output directory
under ``tools/.build/<device>``.

``--libraries`` is passed explicitly and not left to the sketchbook path in
arduino-cli.local.yaml, because the shared libraries live outside the repository
and a sketchbook-relative lookup fails with errors such as a missing AsyncTCP.h
even when the library is installed.
"""

from __future__ import annotations

import subprocess
import sys
import threading
from pathlib import Path
from typing import Callable, Optional

from .devices import ARDUINO_CLI, ARDUINO_CONFIG, Device

# Keeps the console window of every arduino-cli call from flashing over the
# interface on Windows.
_NO_WINDOW = subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0


class CompileError(RuntimeError):
    """arduino-cli could not be run, or returned a non-zero exit code."""


class CompileCancelled(RuntimeError):
    """The operator cancelled the build before it finished."""


def resolve_cli() -> Path:
    """Path to arduino-cli, preferring the copy bundled under tools/.bin."""
    if ARDUINO_CLI.is_file():
        return ARDUINO_CLI
    from shutil import which

    found = which("arduino-cli")
    if found:
        return Path(found)
    raise CompileError(
        "arduino-cli nao encontrado em " + str(ARDUINO_CLI) + " nem no PATH."
    )


def build_command(device: Device, libraries_path: Path) -> list:
    return [
        str(resolve_cli()),
        "compile",
        "--config-file",
        str(ARDUINO_CONFIG),
        "--fqbn",
        device.fqbn,
        "--libraries",
        str(libraries_path),
        "--output-dir",
        str(device.build_dir),
        str(device.sketch_dir),
    ]


def compile_device(
    device: Device,
    libraries_path: Path,
    on_output: Optional[Callable[[str], None]] = None,
    cancel: Optional[threading.Event] = None,
) -> None:
    """Compile the device's sketch, streaming arduino-cli output line by line.

    Raises CompileError on any failure and CompileCancelled when the operator
    stops the run; on cancellation the child process is terminated so a build
    does not keep running unattended.
    """
    if not ARDUINO_CONFIG.is_file():
        raise CompileError("Configuracao do Arduino CLI nao encontrada: " + str(ARDUINO_CONFIG))
    if not device.sketch_dir.is_dir():
        raise CompileError("Diretorio do firmware nao encontrado: " + str(device.sketch_dir))
    if not libraries_path.is_dir():
        raise CompileError("Diretorio de bibliotecas Arduino nao encontrado: " + str(libraries_path))

    device.build_dir.mkdir(parents=True, exist_ok=True)
    command = build_command(device, libraries_path)

    try:
        process = subprocess.Popen(
            command,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            stdin=subprocess.DEVNULL,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
            creationflags=_NO_WINDOW,
        )
    except OSError as exc:
        raise CompileError("Nao foi possivel executar o arduino-cli: " + str(exc)) from exc

    try:
        assert process.stdout is not None
        for line in process.stdout:
            if cancel is not None and cancel.is_set():
                process.terminate()
                raise CompileCancelled("Compilacao cancelada pelo operador.")
            text = line.rstrip()
            if text and on_output:
                on_output(text)
        code = process.wait()
    except CompileCancelled:
        process.wait(timeout=10)
        raise
    finally:
        if process.stdout is not None:
            process.stdout.close()

    if code != 0:
        raise CompileError("Falha na compilacao de " + device.name + " (codigo " + str(code) + ").")
