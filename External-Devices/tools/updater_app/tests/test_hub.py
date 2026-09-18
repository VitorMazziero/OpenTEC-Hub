"""Parse synthetic /nodes documents, including the ones a real Hub produces.

The interesting cases are not the happy path but the two ways the Hub reports
"I have never heard from this node": the 0.0.0.0 address and the 999999 value
in age_ms. Treating either as real data would send an upload to nowhere or show
a node as freshly seen.
"""

from __future__ import annotations

import json
import sys
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from updater import hub  # noqa: E402

failures: list[str] = []

PAYLOAD = {
    "hub_time_ms": 100000,
    "nodes": [
        {
            "dev": "pump",
            "ip": "192.168.4.12",
            "mac": "AA:BB:CC:DD:EE:01",
            "version": "3.12",
            "online": True,
            "age_ms": 740,
            "registered": True,
            "last_hello_ms": 95000,
            "last_data_ms": 99260,
        },
        {
            "dev": "flowmeter",
            "ip": "0.0.0.0",
            "mac": "",
            "version": "",
            "online": False,
            "age_ms": 999999,
            "registered": False,
            "last_hello_ms": 0,
            "last_data_ms": 0,
        },
        {
            "dev": "biomass",
            "ip": "192.168.4.23",
            "mac": "AA:BB:CC:DD:EE:03",
            "version": "v11.1",
            "online": False,
            "age_ms": 61000,
            "registered": True,
            "last_hello_ms": 39000,
            "last_data_ms": 0,
        },
    ],
}


def check(condition: bool, message: str) -> None:
    if condition:
        print(f"  ok   {message}")
    else:
        print(f"  FAIL {message}")
        failures.append(message)


class Handler(BaseHTTPRequestHandler):
    def do_GET(self) -> None:  # noqa: N802 - name fixed by BaseHTTPRequestHandler
        if self.path != "/nodes":
            self.send_error(404)
            return
        body = json.dumps(PAYLOAD).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args) -> None:
        pass


def main() -> int:
    server = HTTPServer(("127.0.0.1", 0), Handler)
    port = server.server_port
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()

    try:
        print("Leitura de /nodes")
        nodes = hub.fetch_nodes(f"127.0.0.1:{port}")
        check(len(nodes) == 3, f"tres nos no diretorio ({len(nodes)})")

        pump = nodes["pump"]
        check(pump.ip == "192.168.4.12", f"endereco da bomba: {pump.ip}")
        check(pump.has_address, "endereco da bomba e utilizavel")
        check(pump.version == "3.12", f"versao da bomba: {pump.version}")
        check(pump.online, "bomba online")
        # 100000 - max(95000, 99260) = 740
        check(pump.age_ms == 740, f"idade pela base de tempo do Hub: {pump.age_ms} ms")
        check(pump.age_text == "740 ms", f"idade formatada: {pump.age_text}")

        flow = nodes["flowmeter"]
        check(not flow.has_address, "0.0.0.0 nao e tratado como endereco")
        check(flow.age_ms is None, "sentinela 999999 nao vira duracao")
        check(flow.age_text == "--", f"idade desconhecida exibida como --: {flow.age_text}")
        check(not flow.registered, "fluxometro nao registrado")

        bio = nodes["biomass"]
        # 100000 - 39000 = 61000, sem dados recentes mas com hello antigo
        check(bio.age_ms == 61000, f"idade do sensor de biomassa: {bio.age_ms} ms")
        check(bio.age_text == "1.0 min", f"idade em minutos: {bio.age_text}")
        check(bio.registered and not bio.online, "registrado porem sem dados recentes")

        print("\nHub indisponivel")
        try:
            # Porta fechada: a descoberta precisa falhar de forma tratada.
            hub.fetch_nodes("127.0.0.1:1", timeout=1.0)
            check(False, "HubUnavailable levantada para porta fechada")
        except hub.HubUnavailable as exc:
            check(True, f"HubUnavailable levantada: {str(exc)[:60]}...")

        print("\nRota /update")
        check(
            hub.probe_update_route(f"127.0.0.1:{port}", timeout=1.0) is False,
            "servidor sem /update nao e considerado alcancavel",
        )
    finally:
        server.shutdown()
        server.server_close()

    print()
    if failures:
        print(f"{len(failures)} verificacao(oes) falharam.")
        return 1
    print("Leitura do diretorio de nos correta.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
