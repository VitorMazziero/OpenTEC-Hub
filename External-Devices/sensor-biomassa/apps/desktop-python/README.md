# Aplicativo desktop do sensor de biomassa

Entrada do aplicativo Python/PySide6 para controle, gráficos e registro do sensor. A implementação fica em `pc_client/`; o firmware ativo v5.3 está em `../../firmware/biomass-sensor`.

```powershell
python -m pip install -r pc_client/requirements.txt
python app.py
python pc_client/tests/run_all.py
```

A documentação operacional detalhada permanece em `pc_client/README.md`. Resultados transitórios devem ser gravados em `../../tests/evidence`, não dentro do código do aplicativo.
