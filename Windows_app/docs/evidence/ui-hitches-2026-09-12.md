# Engasgos da thread da UI — medição de 2026-09-12 (após o plano de 11/09)

Instrumento: `UiHitchMonitor` (DEBUG), `DispatcherTimer` em `DispatcherPriority.Input` a cada
50 ms; registra todo tick que chega > 30 ms atrasado. Resumo no log ao sair.

## Corrida 1 — página de Potência, conectado ao simulador por Wi-Fi, 1 Hz, 3 min

Comando: `opentec-simulator http --port 8080 --data-delay 1000` +
`OpenTECHub.exe --workspace <ws> --no-workspace-prompt --nav power --exit-after-ms 180000`
(workspace com `PreferredMedium = WiFi`, `IpAddress = 127.0.0.1:8080`).

```text
2026-09-12 00:27:52.871 [INF] : === OpenTEC-Hub starting ===
2026-09-12 00:27:54.391 [INF] OpenTECHub.App: First frame after 1745 ms (budget 2000 ms)
2026-09-12 00:27:54.393 [INF] OpenTECHub.ViewModels.ShellViewModel: Auto-connect starting ("WiFi")
2026-09-12 00:27:54.487 [DBG] : UI hitch: tick 44 ms late
2026-09-12 00:27:54.528 [INF] OpenTECHub.Protocol.ConnectionManager: State -> "Connected" ["WiFi" 127.0.0.1:8080] 
2026-09-12 00:27:54.672 [DBG] : UI hitch: tick 136 ms late
2026-09-12 00:27:54.774 [DBG] : UI hitch: tick 53 ms late
2026-09-12 00:30:54.404 [INF] : UI hitches > 30 ms: 3 in 2872 ticks (3,0 min); worst 136 ms; total 233 ms; buckets 30-60=2 60-100=0 100-200=1 200-500=0 >500=0
2026-09-12 00:30:54.404 [INF] : === OpenTEC-Hub exiting ===
```

Leitura: **3 atrasos em 2872 ticks (3,0 min)**, todos nos 300 ms após o enlace subir — a
primeira telemetria populando a página (44, 136 e 53 ms). Depois disso, **zero** atrasos
> 30 ms durante os ~3 min restantes de telemetria a 1 Hz com a página de Potência visível.

## O que ainda não foi medido

O plano (§5.1) pede um **ensaio simulado de 10 min** (potência e kLa) com a captura correndo —
é o caminho que exercita as grades, os appends e as mudanças de fase (§§A–D). Iniciar um
ensaio exige o operador (não há automação de UI para "Iniciar/continuar"); fica para a
bancada: abrir o ensaio, iniciar com o aceite automático, deixar 10 min, fechar o
aplicativo e copiar a linha `UI hitches > 30 ms: …` do log para este arquivo. Meta: zero
atrasos > 30 ms durante a captura e nenhum em mudança de fase.

## Antes (referência, medido em 11/09 por leitura de código e I/O nesta máquina)

- 50–400 ms por quadro de telemetria na thread da UI (grades reconstruídas ×2, dois
  `File.AppendAllText`, gráficos redesenhados inclusive ocultos, despacho em `DataBind`).
- 55 ms médios, picos de 330 ms, a cada mudança de fase (reescrita de `ensaio.json` de 832 KB).
