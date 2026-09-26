"""
bath_app.py -- ferramenta de bancada para o no `bath` (ESP32-S3 + Contemp C404).

Fala HTTP direto com o dispositivo (AP `Banho Termostatico`, 192.168.8.1) sem o
Hub. So biblioteca padrao: Tkinter para a janela, urllib para as requisicoes.

Interface grafica:
    python bath_app.py [--host 192.168.8.1]

Linha de comando (uma acao por chamada, imprime a resposta JSON):
    python bath_app.py status
    python bath_app.py sp 31.5
    python bath_app.py delta -0.5
    python bath_app.py sync 30.0
    python bath_app.py home [alvo]
    python bath_app.py key up 3
    python bath_app.py hold up 2000
    python bath_app.py mode auto|manual
    python bath_app.py abort
    python bath_app.py config press_ms=150 gap_ms=150
    python bath_app.py display
    python bath_app.py log                  (ultimas 64 linhas do registro de eventos)
    python bath_app.py capture [n] [us]     (analisador logico do display; salva JSON)
    python bath_app.py scope <gpio> [n] [us] (osciloscopio pelo ADC, GPIO 1..10; salva JSON)
"""

from __future__ import annotations

import argparse
import json
import queue
import sys
import threading
import urllib.error
import urllib.request
from typing import Any, Callable, Optional

DEFAULT_HOST = "192.168.8.1"
TIMEOUT_S = 3.0

# Chaves de configuracao aceitas pelo firmware (PROTOCOL.md §4), com o tipo usado
# para converter o texto digitado. Nome -> (tipo, dica).
CONFIG_FIELDS = [
    ("press_ms", int, "rele fechado (ms)"),
    ("gap_ms", int, "rele aberto entre toques (ms)"),
    ("menu_ms", int, "espera apos a tecla de entrada (ms)"),
    ("settle_ms", int, "espera apos confirmar (ms)"),
    ("step_c", float, "graus por toque"),
    ("sp_min", float, "in.L do C404"),
    ("sp_max", float, "in.H do C404"),
    ("enter_key", int, "0 nenhuma, 1 *, 2 ENTER"),
    ("confirm_key", int, "0 nenhuma, 1 *, 2 ENTER"),
    ("sp_source", int, "0 sombra, 1 display"),
    ("sense_enabled", int, "0/1 leitura das teclas"),
    ("sense_mask", int, "linhas ligadas: bit0 *, bit1 ^, bit2 v, bit3 ENTER (6 = setas)"),
    ("hub_enabled", int, "0/1 STA para o Hub"),
    ("disp_seg_low", int, "0/1 segmento aceso = LOW"),
    ("disp_dig_low", int, "0/1 digito ativo = LOW"),
    ("disp_seg_lead", int, "0/1 segmentos mudam antes do digito"),
    ("disp_mode", int, "0 linhas de digito, 1 janelas por 2DISP"),
    ("disp_slot_us", int, "duracao da janela de digito (us), modo 1"),
    ("disp_sp_bank", int, "nivel de 2DISP do display do SP, modo 1"),
    ("disp_decimals", int, "casas decimais do C404 (d.P); ponto fora disso = leitura invalida"),
    ("home_margin", int, "toques extras no home"),
    ("send_period", int, "periodo do push ao Hub (ms)"),
    ("hold_enabled", int, "0/1 tecla mantida no modo display"),
    ("hold_min_steps", int, "distancia minima (toques) para manter a tecla"),
    ("hold_stop_steps", int, "soltar a esta distancia do alvo (toques)"),
    ("hold_lag_ms", int, "atraso display->rele compensado pela taxa (ms)"),
    ("hold_settle_ms", int, "espera apos soltar antes de reler (ms)"),
    ("hold_stall_ms", int, "display parado com tecla mantida = soltar (ms)"),
    ("mode_hold_ms", int, "^+v mantidas por este tempo alternam o modo (0 desliga)"),
    ("guard_delay_ms", int, "modo auto: painel parado por este tempo antes de reverter (ms)"),
    ("guard_check_ms", int, "modo auto: intervalo entre avaliacoes do display (ms)"),
]


class BathClient:
    """Cliente HTTP minimo para as rotas do firmware."""

    def __init__(self, host: str = DEFAULT_HOST) -> None:
        self.host = host

    def _url(self, path: str) -> str:
        return f"http://{self.host}{path}"

    def get(self, path: str) -> dict[str, Any]:
        with urllib.request.urlopen(self._url(path), timeout=TIMEOUT_S) as resp:
            return json.loads(resp.read().decode("utf-8"))

    def command(self, payload: dict[str, Any]) -> dict[str, Any]:
        data = json.dumps(payload).encode("utf-8")
        req = urllib.request.Request(self._url("/command"), data=data, method="POST",
                                     headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=TIMEOUT_S) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as err:
            # 400/409 trazem o JSON de erro do firmware; e resposta, nao falha.
            body = err.read().decode("utf-8", errors="replace")
            try:
                return json.loads(body)
            except json.JSONDecodeError:
                return {"ok": False, "error": f"http_{err.code}", "body": body}

    def status(self) -> dict[str, Any]:
        return self.get("/status")

    def config(self) -> dict[str, Any]:
        return self.get("/config")

    def display(self) -> dict[str, Any]:
        return self.get("/display")

    def capture(self, n: int = 2000, period_us: int = 10) -> dict[str, Any]:
        url = self._url(f"/capture?n={n}&us={period_us}")
        with urllib.request.urlopen(url, timeout=TIMEOUT_S + 5) as resp:
            return json.loads(resp.read().decode("utf-8"))


def summarize_scope(sc: dict[str, Any], divider: float = 5.1 / (3.3 + 5.1)) -> str:
    """Levels of an ADC trace; C404-side voltage assumes the 3.3k/5.1k divider."""
    mv = sc["mv"]
    lo, hi = min(mv), max(mv)
    mean = sum(mv) / len(mv)
    lines = [f"GPIO {sc['pin']}: {len(mv)} amostras a {sc['period_us']} us "
             f"({sc['elapsed_us'] / 1000:.1f} ms reais)",
             f"no GPIO:  min {lo} mV  media {mean:.0f} mV  max {hi} mV",
             f"no C404 (divisor 3,3k/5,1k): min {lo / divider / 1000:.2f} V  "
             f"media {mean / divider / 1000:.2f} V  max {hi / divider / 1000:.2f} V",
             "histograma (mV no GPIO):"]
    bins = 10
    width = max(1, (hi - lo) // bins + 1)
    counts = [0] * bins
    for v in mv:
        counts[min(bins - 1, (v - lo) // width)] += 1
    for i, c in enumerate(counts):
        bar = "#" * round(50 * c / len(mv))
        lines.append(f"  {lo + i * width:5d}-{lo + (i + 1) * width - 1:5d} {c:5d} {bar}")
    return "\n".join(lines)


def summarize_capture(cap: dict[str, Any]) -> str:
    """Per-line duty, edge count and frequency of a /capture result."""
    data = cap["data"]
    samples = [int(data[i:i + 4], 16) for i in range(0, len(data), 4)]
    seconds = cap["elapsed_us"] / 1e6
    lines = [f"{len(samples)} amostras a {cap['period_us']} us "
             f"({cap['elapsed_us'] / 1000:.1f} ms reais)",
             f"{'linha':6} {'HIGH %':>7} {'bordas':>7} {'Hz':>8}"]
    for bit, name in enumerate(cap["bits"]):
        levels = [(v >> bit) & 1 for v in samples]
        high = 100.0 * sum(levels) / len(levels)
        edges = sum(1 for a, b in zip(levels, levels[1:]) if a != b)
        hz = edges / 2 / seconds if seconds > 0 else 0.0
        lines.append(f"{name:6} {high:7.1f} {edges:7d} {hz:8.0f}")
    return "\n".join(lines)


# ---------------------------------------------------------------------------
# Linha de comando
# ---------------------------------------------------------------------------

def run_cli(client: BathClient, args: list[str]) -> int:
    verb, rest = args[0], args[1:]
    try:
        if verb == "status":
            result = client.status()
        elif verb == "config" and not rest:
            result = client.config()
        elif verb == "config":
            payload: dict[str, Any] = {}
            types = {name: kind for name, kind, _ in CONFIG_FIELDS}
            for item in rest:
                name, _, value = item.partition("=")
                if name not in types:
                    print(f"chave desconhecida: {name}", file=sys.stderr)
                    return 2
                payload[name] = types[name](value)
            result = client.command(payload)
        elif verb == "display":
            result = client.display()
        elif verb == "log":
            with urllib.request.urlopen(client._url("/log"), timeout=TIMEOUT_S) as resp:
                print(resp.read().decode("utf-8", errors="replace"))
            return 0
        elif verb == "scope":
            pin = int(rest[0])
            n = int(rest[1]) if len(rest) > 1 else 2000
            period = int(rest[2]) if len(rest) > 2 else 40
            with urllib.request.urlopen(client._url(f"/scope?pin={pin}&n={n}&us={period}"),
                                        timeout=TIMEOUT_S + 5) as resp:
                sc = json.loads(resp.read().decode("utf-8"))
            if not sc.get("ok"):
                result = sc
            else:
                from datetime import datetime
                path = f"scope_gpio{pin}_{datetime.now():%Y%m%d_%H%M%S}.json"
                with open(path, "w", encoding="utf-8") as fh:
                    json.dump(sc, fh)
                print(summarize_scope(sc))
                print(f"salvo em {path}")
                return 0
        elif verb == "capture":
            n = int(rest[0]) if rest else 2000
            period = int(rest[1]) if len(rest) > 1 else 10
            cap = client.capture(n, period)
            if not cap.get("ok"):
                result = cap
            else:
                from datetime import datetime
                path = f"capture_{datetime.now():%Y%m%d_%H%M%S}.json"
                with open(path, "w", encoding="utf-8") as fh:
                    json.dump(cap, fh)
                print(summarize_capture(cap))
                print(f"salvo em {path}")
                return 0
        elif verb == "sp":
            result = client.command({"setpoint": float(rest[0])})
        elif verb == "delta":
            result = client.command({"delta": float(rest[0])})
        elif verb == "sync":
            result = client.command({"sync_sp": float(rest[0])})
        elif verb == "home":
            payload = {"home": 1}
            if rest:
                payload["setpoint"] = float(rest[0])
            result = client.command(payload)
        elif verb == "key":
            count = int(rest[1]) if len(rest) > 1 else 1
            result = client.command({"key": rest[0], "count": count})
        elif verb == "hold":
            result = client.command({"key": rest[0], "hold_ms": int(rest[1])})
        elif verb == "mode":
            result = client.command({"mode": rest[0]})
        elif verb == "abort":
            result = client.command({"abort": 1})
        else:
            print(__doc__)
            return 2
    except (urllib.error.URLError, OSError, IndexError, ValueError) as err:
        print(f"erro: {err}", file=sys.stderr)
        return 1
    print(json.dumps(result, indent=2, ensure_ascii=False))
    return 0 if result.get("ok", True) else 1


# ---------------------------------------------------------------------------
# Interface grafica
# ---------------------------------------------------------------------------

def run_gui(host: str) -> int:
    import tkinter as tk
    from tkinter import messagebox, ttk

    client = BathClient(host)
    events: "queue.Queue[tuple[str, Any]]" = queue.Queue()

    def in_background(label: str, func: Callable[[], Any]) -> None:
        """Roda a chamada HTTP fora da thread do Tk e devolve pelo queue."""
        def worker() -> None:
            try:
                events.put((label, func()))
            except (urllib.error.URLError, OSError) as err:
                events.put(("error", f"{label}: {err}"))
        threading.Thread(target=worker, daemon=True).start()

    root = tk.Tk()
    root.title("Banho Termostatico - bancada")
    root.minsize(640, 560)

    host_var = tk.StringVar(value=host)
    poll_var = tk.BooleanVar(value=True)

    top = ttk.Frame(root, padding=8)
    top.pack(fill="x")
    ttk.Label(top, text="Host:").pack(side="left")
    ttk.Entry(top, textvariable=host_var, width=18).pack(side="left", padx=4)
    ttk.Checkbutton(top, text="Atualizar 1 s", variable=poll_var).pack(side="left", padx=8)
    conn_lbl = ttk.Label(top, text="sem conexao", foreground="#b00")
    conn_lbl.pack(side="left", padx=8)

    notebook = ttk.Notebook(root)
    notebook.pack(fill="both", expand=True, padx=8, pady=4)

    # --- Aba Operacao -------------------------------------------------------
    op = ttk.Frame(notebook, padding=8)
    notebook.add(op, text="Operacao")

    sp_big = ttk.Label(op, text="--.-", font=("Segoe UI", 34, "bold"))
    sp_big.grid(row=0, column=0, columnspan=2, sticky="w")
    known_lbl = ttk.Label(op, text="", font=("Segoe UI", 11))
    known_lbl.grid(row=0, column=2, columnspan=3, sticky="w")

    info_vars = {k: tk.StringVar(value="--") for k in
                 ("mode", "display", "seq", "progress", "error", "manual", "version")}
    rows = [("Modo", "mode"), ("Display PV / SP", "display"), ("Sequencia", "seq"),
            ("Progresso", "progress"), ("Erro", "error"), ("Toques manuais", "manual"),
            ("Firmware", "version")]
    for i, (label, key) in enumerate(rows, start=1):
        ttk.Label(op, text=label + ":").grid(row=i, column=0, sticky="e", padx=(0, 6))
        ttk.Label(op, textvariable=info_vars[key]).grid(row=i, column=1, columnspan=4, sticky="w")

    r = len(rows) + 1
    modes = ttk.Frame(op)
    modes.grid(row=r, column=0, columnspan=5, sticky="w")
    ttk.Label(modes, text="Modo:").pack(side="left")
    ttk.Button(modes, text="Manual", command=lambda: in_background("mode", lambda: client.command({"mode": "manual"}))).pack(side="left", padx=2)
    ttk.Button(modes, text="Automatico (reverte mudancas no painel)",
               command=lambda: in_background("mode", lambda: client.command({"mode": "auto"}))).pack(side="left", padx=2)
    r += 1
    ttk.Separator(op).grid(row=r, column=0, columnspan=5, sticky="ew", pady=8)
    r += 1

    target_var = tk.StringVar(value="30.0")
    ttk.Label(op, text="Setpoint alvo:").grid(row=r, column=0, sticky="e")
    ttk.Entry(op, textvariable=target_var, width=8).grid(row=r, column=1, sticky="w")

    def send_setpoint() -> None:
        try:
            value = float(target_var.get().replace(",", "."))
        except ValueError:
            messagebox.showerror("Setpoint", "Valor invalido")
            return
        in_background("setpoint", lambda: client.command({"setpoint": value}))

    ttk.Button(op, text="Enviar setpoint", command=send_setpoint).grid(row=r, column=2, sticky="w")
    ttk.Button(op, text="Abortar", command=lambda: in_background("abort", lambda: client.command({"abort": 1}))
               ).grid(row=r, column=3, sticky="w")
    r += 1

    nudges = ttk.Frame(op)
    nudges.grid(row=r, column=0, columnspan=5, sticky="w", pady=4)
    for d in (-1.0, -0.5, -0.1, 0.1, 0.5, 1.0):
        ttk.Button(nudges, text=f"{d:+.1f}", width=6,
                   command=lambda d=d: in_background("delta", lambda: client.command({"delta": d}))
                   ).pack(side="left", padx=2)
    r += 1

    sync_var = tk.StringVar(value="30.0")
    ttk.Label(op, text="SP lido no C404:").grid(row=r, column=0, sticky="e")
    ttk.Entry(op, textvariable=sync_var, width=8).grid(row=r, column=1, sticky="w")

    def send_sync() -> None:
        try:
            value = float(sync_var.get().replace(",", "."))
        except ValueError:
            messagebox.showerror("Sincronizar", "Valor invalido")
            return
        in_background("sync_sp", lambda: client.command({"sync_sp": value}))

    ttk.Button(op, text="Sincronizar sombra", command=send_sync).grid(row=r, column=2, sticky="w")

    def send_home() -> None:
        if not messagebox.askyesno(
                "Home", "O home pressiona ▼ ate saturar o SP em sp_min e depois sobe ate o alvo.\n"
                        "Pode levar minutos e passa pelo SP minimo. Continuar?"):
            return
        payload: dict[str, Any] = {"home": 1}
        try:
            payload["setpoint"] = float(target_var.get().replace(",", "."))
        except ValueError:
            pass
        in_background("home", lambda: client.command(payload))

    ttk.Button(op, text="Home (ate sp_min)", command=send_home).grid(row=r, column=3, sticky="w")
    r += 1

    ttk.Separator(op).grid(row=r, column=0, columnspan=5, sticky="ew", pady=8)
    r += 1
    ttk.Label(op, text="Teclas cruas:").grid(row=r, column=0, sticky="e")
    keys = ttk.Frame(op)
    keys.grid(row=r, column=1, columnspan=4, sticky="w")
    count_var = tk.IntVar(value=1)
    for name, text in (("star", "*"), ("up", "▲"), ("down", "▼"), ("enter", "ENTER")):
        ttk.Button(keys, text=text, width=7,
                   command=lambda n=name: in_background(
                       "key", lambda: client.command({"key": n, "count": int(count_var.get())}))
                   ).pack(side="left", padx=2)
    ttk.Label(keys, text=" x").pack(side="left")
    ttk.Spinbox(keys, from_=1, to=500, textvariable=count_var, width=5).pack(side="left")
    r += 1

    # Tecla mantida por tempo fixo: mede o atraso e a taxa da auto-repeticao do
    # C404 (VALIDATION.md G3b). Manter * devolve a tela principal (manual §7.1).
    ttk.Label(op, text="Manter tecla:").grid(row=r, column=0, sticky="e")
    holds = ttk.Frame(op)
    holds.grid(row=r, column=1, columnspan=4, sticky="w")
    hold_var = tk.IntVar(value=2000)
    for name, text in (("up", "▲"), ("down", "▼")):
        ttk.Button(holds, text=text, width=7,
                   command=lambda n=name: in_background(
                       "hold", lambda: client.command({"key": n, "hold_ms": int(hold_var.get())}))
                   ).pack(side="left", padx=2)
    ttk.Label(holds, text=" por").pack(side="left")
    ttk.Spinbox(holds, from_=100, to=20000, increment=100, textvariable=hold_var, width=6).pack(side="left")
    ttk.Label(holds, text="ms").pack(side="left")

    # --- Aba Configuracao ---------------------------------------------------
    cfg = ttk.Frame(notebook, padding=8)
    notebook.add(cfg, text="Configuracao")
    cfg_vars: dict[str, tk.StringVar] = {}
    for i, (name, _, hint) in enumerate(CONFIG_FIELDS):
        ttk.Label(cfg, text=name).grid(row=i, column=0, sticky="e", padx=(0, 6), pady=1)
        var = tk.StringVar()
        cfg_vars[name] = var
        ttk.Entry(cfg, textvariable=var, width=10).grid(row=i, column=1, sticky="w")
        ttk.Label(cfg, text=hint, foreground="#666").grid(row=i, column=2, sticky="w", padx=6)

    def load_config() -> None:
        in_background("config_get", client.config)

    def apply_config() -> None:
        payload: dict[str, Any] = {}
        for name, kind, _ in CONFIG_FIELDS:
            text = cfg_vars[name].get().strip().replace(",", ".")
            if not text:
                continue
            try:
                payload[name] = kind(float(text)) if kind is int else kind(text)
            except ValueError:
                messagebox.showerror("Configuracao", f"{name}: valor invalido")
                return
        in_background("config_set", lambda: client.command(payload))

    btns = ttk.Frame(cfg)
    btns.grid(row=len(CONFIG_FIELDS), column=0, columnspan=3, pady=8, sticky="w")
    ttk.Button(btns, text="Carregar do dispositivo", command=load_config).pack(side="left", padx=2)
    ttk.Button(btns, text="Aplicar", command=apply_config).pack(side="left", padx=2)
    ttk.Button(btns, text="Restaurar padroes (reset_nvs)",
               command=lambda: messagebox.askyesno("reset_nvs", "Apagar NVS e restaurar padroes?")
               and in_background("reset_nvs", lambda: client.command({"reset_nvs": 1}))
               ).pack(side="left", padx=2)

    # --- Aba Display --------------------------------------------------------
    disp = ttk.Frame(notebook, padding=8)
    notebook.add(disp, text="Display")
    disp_text = tk.Text(disp, height=12, width=70, font=("Consolas", 10))
    disp_text.pack(fill="both", expand=True)
    ttk.Button(disp, text="Ler /display", command=lambda: in_background("display", client.display)).pack(pady=4)

    # --- Log ----------------------------------------------------------------
    log = tk.Text(root, height=8, font=("Consolas", 9))
    log.pack(fill="both", expand=False, padx=8, pady=(0, 8))

    def log_line(text: str) -> None:
        log.insert("end", text + "\n")
        log.see("end")

    def render_status(s: dict[str, Any]) -> None:
        sp = s.get("sp_shadow")
        sp_big.config(text=f"{sp:.1f}" if isinstance(sp, (int, float)) else "--.-")
        known = s.get("sp_known")
        known_lbl.config(text="setpoint conhecido" if known else "SETPOINT DESCONHECIDO - sincronize",
                         foreground="#080" if known else "#b00")
        dev = s.get("deviation_c")
        mode_text = f"{s.get('mode', '--')}  guarda: {s.get('guard', '--')}"
        if s.get("guard_corrections"):
            mode_text += f" ({s['guard_corrections']} correcoes)"
        if dev is not None:
            mode_text += f"  desvio display-alvo: {dev:+.1f} C"
        if s.get("arrows_held_ms"):
            mode_text += f"  [^+v ha {s['arrows_held_ms']} ms]"
        info_vars["mode"].set(mode_text)
        pv = s.get("display_pv")
        dsp = s.get("display_sp")
        alive = "" if s.get("display_alive") else " (sem sinal)"
        info_vars["display"].set(f"{pv if pv is not None else '--'} / {dsp if dsp is not None else '--'}"
                                 f"  [{s.get('display_text', '')}]{alive}")
        phase = s.get("seq_phase") or ""
        info_vars["seq"].set(f"{s.get('seq_state')} / {s.get('seq_kind')}"
                             + (f" / {phase}" if phase else "") + f"  alvo {s.get('sp_target')}")
        total = s.get("presses_total", 0)
        hold_ms = s.get("hold_ms", 0)
        parts = []
        if hold_ms:
            parts.append(f"tecla mantida ha {hold_ms} ms, {s.get('hold_rate', 0)} toques/s")
        if total:
            parts.append(f"{s.get('presses_done', 0)}/{total} toques"
                         f" (nao confirmados: {s.get('presses_unconfirmed', 0)})")
        if s.get("hold_rounds"):
            parts.append(f"holds: {s.get('hold_rounds')}")
        info_vars["progress"].set("  ".join(parts) if parts else "--")
        info_vars["error"].set(s.get("seq_error") or "--")
        age = s.get("manual_age_s", -1)
        info_vars["manual"].set(f"{s.get('manual_presses', 0)}" + (f", ultimo ha {age} s" if age >= 0 else ""))
        info_vars["version"].set(str(s.get("version", "")))

    def render_config(c: dict[str, Any]) -> None:
        for name, _, _ in CONFIG_FIELDS:
            if name in c:
                cfg_vars[name].set(str(c[name]))

    def render_display(d: dict[str, Any]) -> None:
        disp_text.delete("1.0", "end")
        disp_text.insert("end", json.dumps(d, indent=2, ensure_ascii=False))

    def drain_events() -> None:
        while True:
            try:
                label, payload = events.get_nowait()
            except queue.Empty:
                break
            if label == "error":
                conn_lbl.config(text="sem conexao", foreground="#b00")
                log_line(str(payload))
            elif label == "status":
                conn_lbl.config(text="conectado", foreground="#080")
                render_status(payload)
            elif label == "config_get":
                render_config(payload)
                log_line("config carregada")
            elif label == "display":
                render_display(payload)
            else:
                log_line(f"{label}: {json.dumps(payload, ensure_ascii=False)}")
                if label in ("config_set", "reset_nvs"):
                    load_config()
        root.after(100, drain_events)

    def poll() -> None:
        client.host = host_var.get().strip() or DEFAULT_HOST
        if poll_var.get():
            in_background("status", client.status)
        root.after(1000, poll)

    root.after(100, drain_events)
    root.after(200, poll)
    root.after(600, load_config)
    root.mainloop()
    return 0


def main(argv: Optional[list[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Bancada do no bath (C404 + ESP32-S3)", add_help=True)
    parser.add_argument("--host", default=DEFAULT_HOST, help="IP do dispositivo (padrao %(default)s)")
    parser.add_argument("verb", nargs="*", help="acao de linha de comando; vazio abre a janela")
    ns = parser.parse_args(argv)
    if ns.verb:
        return run_cli(BathClient(ns.host), ns.verb)
    return run_gui(ns.host)


if __name__ == "__main__":
    sys.exit(main())
