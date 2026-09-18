"""Upload a firmware image to a local stand-in for the device and inspect it.

This is the test that matters most, because a malformed body is only noticed by
a real ESP32 halfway through erasing its flash. The stand-in server checks the
three things the firmwares depend on: a declared Content-Length rather than a
chunked body, a multipart part named ``firmware``, and image bytes that arrive
byte-identical.

It also covers cancellation and the refusal of merged/bootloader/partition
images, both of which must fail before any flash is touched.
"""

from __future__ import annotations

import re
import sys
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from updater import firmware, ota  # noqa: E402

failures: list[str] = []
received: dict = {}

IMAGE_BYTES = bytes(range(256)) * 400  # 102400 bytes, enough to span many blocks


def check(condition: bool, message: str) -> None:
    if condition:
        print(f"  ok   {message}")
    else:
        print(f"  FAIL {message}")
        failures.append(message)


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def do_GET(self) -> None:  # noqa: N802
        if self.path == "/update":
            body = b"<form>OTA</form>"
            self.send_response(200)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        else:
            self.send_error(404)

    def do_POST(self) -> None:  # noqa: N802
        length = self.headers.get("Content-Length")
        received["content_length"] = length
        received["transfer_encoding"] = self.headers.get("Transfer-Encoding")
        received["expect"] = self.headers.get("Expect")
        received["content_type"] = self.headers.get("Content-Type")

        raw = self.rfile.read(int(length)) if length else b""
        received["raw_length"] = len(raw)

        boundary = re.search(r"boundary=(\S+)", received["content_type"] or "")
        if boundary:
            marker = ("--" + boundary.group(1)).encode()
            head, _, rest = raw.partition(b"\r\n\r\n")
            received["disposition"] = head.decode("utf-8", "replace")
            payload = rest.rsplit(b"\r\n" + marker + b"--\r\n", 1)[0]
            received["payload"] = payload

        body = b'{"status":"ok"}'
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args) -> None:
        pass


class QuietServer(HTTPServer):
    """Stand-in device that does not print a traceback for an aborted upload.

    The cancellation case drops the connection on purpose, which is exactly
    what a cancelled OTA does to a real node; the resulting write failure is
    expected and would otherwise bury the results in a traceback.
    """

    def handle_error(self, request, client_address) -> None:
        exc = sys.exc_info()[1]
        if isinstance(exc, (ConnectionAbortedError, ConnectionResetError, BrokenPipeError)):
            return
        super().handle_error(request, client_address)


def main() -> int:
    temp_dir = Path(__file__).resolve().parent / ".tmp"
    temp_dir.mkdir(exist_ok=True)
    image = temp_dir / "flowmeter.ino.bin"
    image.write_bytes(IMAGE_BYTES)

    server = QuietServer(("127.0.0.1", 0), Handler)
    address = f"127.0.0.1:{server.server_port}"
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()

    try:
        print("Envio da imagem")
        seen: list[tuple] = []
        response = ota.upload(image, address, progress=lambda s, t: seen.append((s, t)))

        check(response == '{"status":"ok"}', f"resposta do dispositivo: {response}")
        check(
            received.get("transfer_encoding") is None,
            "corpo com tamanho declarado, nao chunked",
        )
        check(
            received.get("content_length") == str(received.get("raw_length")),
            f"Content-Length confere com o corpo recebido ({received.get('content_length')})",
        )
        check(received.get("expect") is None, "sem Expect: 100-continue")
        check(
            "multipart/form-data" in (received.get("content_type") or ""),
            f"tipo do corpo: {received.get('content_type', '')[:46]}...",
        )
        check(
            'name="firmware"' in received.get("disposition", ""),
            "parte do formulario chamada firmware",
        )
        check(
            'filename="flowmeter.ino.bin"' in received.get("disposition", ""),
            "nome do arquivo preservado",
        )
        check(
            received.get("payload") == IMAGE_BYTES,
            f"imagem integra: {len(received.get('payload', b''))} de {len(IMAGE_BYTES)} bytes",
        )

        print("\nProgresso")
        check(bool(seen), f"progresso reportado {len(seen)} vezes")
        check(seen[0] == (0, len(IMAGE_BYTES)), f"primeiro evento em zero: {seen[0]}")
        check(
            seen[-1] == (len(IMAGE_BYTES), len(IMAGE_BYTES)),
            f"ultimo evento no total: {seen[-1]}",
        )
        check(
            all(a[0] <= b[0] for a, b in zip(seen, seen[1:])),
            "progresso monotonico",
        )

        print("\nRota /update alcancavel")
        from updater import hub

        check(hub.probe_update_route(address, timeout=2.0), "GET /update respondeu")

        print("\nCancelamento")
        cancel = threading.Event()
        cancel.set()
        try:
            ota.upload(image, address, cancel=cancel)
            check(False, "OtaCancelled levantada quando ja cancelado")
        except ota.OtaCancelled:
            check(True, "cancelamento interrompe antes de enviar a imagem")
        except ota.OtaError as exc:
            check(False, f"esperado OtaCancelled, veio OtaError: {exc}")

        print("\nImagens recusadas")
        for name in ("flowmeter.ino.merged.bin", "bootloader.bin", "app.partitions.bin"):
            check(
                not firmware.is_application_image(Path(name)),
                f"{name} nao e oferecida para envio",
            )
        check(
            firmware.is_application_image(Path("flowmeter.ino.bin")),
            "flowmeter.ino.bin e aceita",
        )

        print("\nDestino inalcancavel")
        try:
            ota.upload(image, "127.0.0.1:1", timeout=2.0)
            check(False, "OtaError levantada para porta fechada")
        except ota.OtaError as exc:
            check(True, f"OtaError levantada: {str(exc)[:58]}...")

    finally:
        server.shutdown()
        server.server_close()
        image.unlink(missing_ok=True)
        try:
            temp_dir.rmdir()
        except OSError:
            pass

    print()
    if failures:
        print(f"{len(failures)} verificacao(oes) falharam.")
        return 1
    print("Envio OTA em conformidade com o contrato dos firmwares.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
