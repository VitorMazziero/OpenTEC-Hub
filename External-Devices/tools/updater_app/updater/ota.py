"""
ota.py -- multipart upload of a firmware image to a device's /update route.

The six firmwares expose the same OTA contract: ``POST /update`` with a
multipart body whose single part is named ``firmware``. This reproduces the
exact request the PowerShell publisher makes through curl.

The body is written with http.client instead of requests so the socket is fed
in small blocks and the interface can show real byte progress, and so the
request carries an explicit Content-Length. ESPAsyncWebServer's upload handler
needs a declared length; a chunked body, which requests would use for a
streaming iterable, would not be parsed as a multipart upload.

``Expect: 100-continue`` is deliberately not sent. curl adds it for large
bodies and the ESP32 stack does not answer it, which costs a one-second stall
on every upload.
"""

from __future__ import annotations

import http.client
import socket
import threading
import uuid
from pathlib import Path
from typing import Callable, Optional

# Block size fed to the socket. Small enough for a responsive progress bar,
# large enough not to fragment the transfer over the ESP32's TCP stack.
CHUNK_BYTES = 8192

# The device erases and writes flash while it answers, so the read timeout is
# far longer than any ordinary HTTP call.
DEFAULT_TIMEOUT_S = 120.0


class OtaError(RuntimeError):
    """The upload did not complete, or the device refused the image."""


class OtaCancelled(RuntimeError):
    """The operator cancelled the upload before it finished."""


def split_address(address: str) -> tuple:
    """Split "host" or "host:port" into (host, port), defaulting to port 80.

    The devices always serve OTA on port 80, but the address field accepts
    whatever the operator types, and a pasted "192.168.4.12:80" must not be
    resolved as a host name.
    """
    host = address.strip()
    if ":" in host:
        host, _, port_text = host.rpartition(":")
        try:
            return host, int(port_text)
        except ValueError as exc:
            raise OtaError("Porta invalida em " + address) from exc
    return host, 80


def _multipart_frame(filename: str, boundary: str) -> tuple:
    """Head and tail of the multipart body surrounding the raw image bytes."""
    head = (
        "--" + boundary + "\r\n"
        'Content-Disposition: form-data; name="firmware"; filename="' + filename + '"\r\n'
        "Content-Type: application/octet-stream\r\n"
        "\r\n"
    ).encode("utf-8")
    tail = ("\r\n--" + boundary + "--\r\n").encode("utf-8")
    return head, tail


def upload(
    binary_path: Path,
    ip: str,
    progress: Optional[Callable[[int, int], None]] = None,
    cancel: Optional[threading.Event] = None,
    timeout: float = DEFAULT_TIMEOUT_S,
) -> str:
    """Send the image and return the device's response body.

    ``progress`` receives (bytes sent, total bytes) as the body is written.
    ``cancel`` is polled between blocks; setting it aborts the connection,
    which the firmware sees as UPLOAD_FILE_ABORTED and recovers from.
    """
    total = binary_path.stat().st_size
    if total <= 0:
        raise OtaError("A imagem esta vazia: " + str(binary_path))

    boundary = "----OpenTECUpdater" + uuid.uuid4().hex
    head, tail = _multipart_frame(binary_path.name, boundary)
    content_length = len(head) + total + len(tail)

    host, port = split_address(ip)
    connection = http.client.HTTPConnection(host, port, timeout=timeout)
    try:
        connection.connect()
        connection.putrequest("POST", "/update", skip_accept_encoding=True)
        connection.putheader("Content-Type", "multipart/form-data; boundary=" + boundary)
        connection.putheader("Content-Length", str(content_length))
        connection.putheader("Connection", "close")
        connection.endheaders()

        connection.send(head)
        sent = 0
        if progress:
            progress(0, total)

        with binary_path.open("rb") as image:
            while True:
                if cancel is not None and cancel.is_set():
                    raise OtaCancelled("Envio cancelado pelo operador.")
                block = image.read(CHUNK_BYTES)
                if not block:
                    break
                connection.send(block)
                sent += len(block)
                if progress:
                    progress(sent, total)

        if sent != total:
            raise OtaError(
                "Enviados {} de {} bytes; o arquivo mudou durante o envio.".format(sent, total)
            )

        connection.send(tail)

        try:
            response = connection.getresponse()
            body = response.read().decode("utf-8", errors="replace").strip()
            status = response.status
        except (http.client.HTTPException, socket.timeout, OSError) as exc:
            # The board reboots roughly two seconds after validating the image,
            # so a dropped connection here is a plausible success and must not
            # be reported as one. The caller confirms by re-reading /nodes.
            raise OtaError(
                "Imagem enviada, mas a placa fechou a conexao antes de responder "
                "(" + str(exc) + "). Confirme a versao apos o reinicio."
            ) from exc

        if status >= 400:
            raise OtaError("O dispositivo recusou a imagem (HTTP " + str(status) + "): " + body)
        return body or "HTTP " + str(status)

    except OtaCancelled:
        raise
    except (socket.timeout, TimeoutError) as exc:
        raise OtaError("Tempo esgotado ao falar com " + ip + ": " + str(exc)) from exc
    except OSError as exc:
        raise OtaError("Falha de rede com " + ip + ": " + str(exc)) from exc
    finally:
        connection.close()
