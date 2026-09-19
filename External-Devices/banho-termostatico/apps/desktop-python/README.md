# Bancada — `bath_app.py`

Ferramenta de teste do nó `bath` sem o Hub. Só biblioteca padrão do Python 3.10+.

```powershell
python bath_app.py                      # janela (host padrao 192.168.8.1)
python bath_app.py --host 192.168.8.1 status
python bath_app.py sp 31.5
python bath_app.py delta -0.5
python bath_app.py sync 30.0
python bath_app.py home 35.0
python bath_app.py key up 3
python bath_app.py abort
python bath_app.py config press_ms=150 gap_ms=150 step_c=0.1
python bath_app.py display
```

A janela tem três abas: **Operação** (setpoint, ajustes ±, sincronizar, home, teclas cruas,
abortar), **Configuração** (todas as chaves de `PROTOCOL.md` §4) e **Display** (saída crua de
`/display` para a caracterização). O estado é atualizado a cada segundo.
