"""
jobs.py -- background workers for discovery, compilation and OTA upload.

Every network call and every arduino-cli run happens on a QThread. The
interface only receives signals, so a node that stopped answering cannot freeze
the window in the middle of a bench run.

Jobs in a batch run one at a time on purpose. The devices share the Hub's
access point, and several simultaneous multipart uploads over that link are
slower and less reliable than the same uploads in sequence; a node that fails
also leaves the others untouched and still flashable.
"""

from __future__ import annotations

import threading
from pathlib import Path
from typing import Optional

from PySide6 import QtCore

from . import compiler, firmware, hub, ota
from .devices import Device


class DiscoveryWorker(QtCore.QThread):
    """Reads /nodes once and reports the directory, or why it is unavailable."""

    finished_ok = QtCore.Signal(dict)
    failed = QtCore.Signal(str)

    def __init__(self, hub_ip: str, parent: Optional[QtCore.QObject] = None) -> None:
        super().__init__(parent)
        self._hub_ip = hub_ip

    def run(self) -> None:
        try:
            self.finished_ok.emit(hub.fetch_nodes(self._hub_ip))
        except hub.HubUnavailable as exc:
            self.failed.emit(str(exc))


class Job:
    """One device's unit of work: compile, upload, or both."""

    def __init__(
        self,
        device: Device,
        ip: str,
        do_compile: bool,
        do_upload: bool,
        binary_path: Optional[Path] = None,
    ) -> None:
        self.device = device
        self.ip = ip
        self.do_compile = do_compile
        self.do_upload = do_upload
        # When set, the operator picked this image by hand and it is used as
        # given; otherwise the newest build is resolved after compiling.
        self.binary_path = binary_path


class JobRunner(QtCore.QThread):
    """Runs a queue of jobs in order, reporting progress per step."""

    log = QtCore.Signal(str, str)              # message, level
    job_started = QtCore.Signal(str, str)      # device key, phase
    job_finished = QtCore.Signal(str, bool, str)  # device key, ok, detail
    upload_progress = QtCore.Signal(str, int, int)  # device key, sent, total
    all_finished = QtCore.Signal(int, int)     # succeeded, failed

    def __init__(
        self,
        jobs: list,
        libraries_path: Path,
        parent: Optional[QtCore.QObject] = None,
    ) -> None:
        super().__init__(parent)
        self._jobs = jobs
        self._libraries_path = libraries_path
        self._cancel = threading.Event()

    def cancel(self) -> None:
        self._cancel.set()

    @property
    def cancelled(self) -> bool:
        return self._cancel.is_set()

    def run(self) -> None:
        succeeded = 0
        failed = 0

        for job in self._jobs:
            if self._cancel.is_set():
                self.log.emit("Fila interrompida pelo operador.", "warn")
                break

            device = job.device
            try:
                if job.do_compile:
                    self._compile(job)
                if job.do_upload:
                    self._upload(job)
                succeeded += 1
                self.job_finished.emit(device.key, True, "Concluido")
            except (compiler.CompileCancelled, ota.OtaCancelled) as exc:
                failed += 1
                self.log.emit(device.name + ": " + str(exc), "warn")
                self.job_finished.emit(device.key, False, "Cancelado")
            except (compiler.CompileError, ota.OtaError) as exc:
                failed += 1
                self.log.emit(device.name + ": " + str(exc), "error")
                self.job_finished.emit(device.key, False, str(exc))
            except Exception as exc:  # noqa: BLE001 - a crash here must not kill the queue
                failed += 1
                self.log.emit(device.name + ": erro inesperado: " + str(exc), "error")
                self.job_finished.emit(device.key, False, str(exc))

        self.all_finished.emit(succeeded, failed)

    def _compile(self, job: Job) -> None:
        device = job.device
        self.job_started.emit(device.key, "Compilando")
        self.log.emit("[" + device.name + "] Compilando " + str(device.sketch_dir), "info")
        compiler.compile_device(
            device,
            self._libraries_path,
            on_output=lambda line: self.log.emit("    " + line, "muted"),
            cancel=self._cancel,
        )
        self.log.emit("[" + device.name + "] Compilacao concluida.", "ok")
        # A fresh build replaces whatever image was selected before, so the
        # upload step always resolves the binary again after compiling.
        job.binary_path = None

    def _upload(self, job: Job) -> None:
        device = job.device
        self.job_started.emit(device.key, "Enviando")

        binary_path = job.binary_path
        if binary_path is None:
            info = firmware.latest_binary(device)
            if info is None:
                raise ota.OtaError(
                    "Nenhuma imagem .bin encontrada. Compile o firmware ou "
                    "selecione um arquivo manualmente."
                )
            binary_path = info.path

        if not binary_path.is_file():
            raise ota.OtaError("Imagem nao encontrada: " + str(binary_path))
        if not firmware.is_application_image(binary_path):
            raise ota.OtaError(
                binary_path.name + " nao e a imagem da aplicacao; o dispositivo "
                "recusa imagens merged, bootloader ou partitions."
            )

        size_kb = binary_path.stat().st_size / 1024.0
        self.log.emit(
            "[" + device.name + "] Enviando " + binary_path.name
            + " ({:.1f} KB) para http://".format(size_kb) + job.ip + "/update",
            "info",
        )

        body = ota.upload(
            binary_path,
            job.ip,
            progress=lambda sent, total: self.upload_progress.emit(device.key, sent, total),
            cancel=self._cancel,
        )
        self.log.emit(
            "[" + device.name + "] Firmware aceito. A placa reinicia em ~2 s.", "ok"
        )
        if body:
            self.log.emit("    resposta: " + body[:200], "muted")
