# bath_app — aplicativo Android do banho termostático

Cliente Flutter que fala **HTTP direto** com o nó `bath` (Contemp C404 + ESP32-S3),
sem passar pelo Hub. É o equivalente Android do `bath_app.py` (desktop) e da página
`/ui` do próprio nó, e a ferramenta de bancada para os gates G1–G9 do
[`../../docs/VALIDATION.md`](../../docs/VALIDATION.md) quando não há PC.

Protocolo consumido: [`../../docs/PROTOCOL.md`](../../docs/PROTOCOL.md) (firmwares r2/r3.x).

## Como conectar

1. No firmware, o nó sobe o AP aberto **`Banho Termostatico`** (canal 6, `192.168.8.1`).
   Conecte o celular a essa rede Wi-Fi.
2. Abra o app. O *badge* de conexão no topo mostra o host, a latência e o estado.
   Toque nele para trocar o IP (atalho para `192.168.8.1`, ou o IP que o nó receber
   na rede do Hub).
3. Se o nó estiver na rede do Hub (`hub_enabled = 1`), use o IP que ele recebeu ali.

O tráfego é HTTP puro (sem TLS); o `AndroidManifest.xml` já permite *cleartext* para
a rede local (`network_security_config.xml`).

## Telas

| Aba | Conteúdo |
|---|---|
| **Operação** | sombra grande do setpoint, "SETPOINT DESCONHECIDO", alvo, campo + Enviar, ±0,1/±0,5/±1,0, cartão de sequência (fase, toques, hold), leituras do painel, traço de 10 min de PV/SP |
| **Modos** | manual/automático, guarda e desvio, gesto ▲+▼, sincronizar a sombra (`sync_sp`) |
| **Bancada** | teclas cruas ×n, ▲/▼ mantidas por N ms (gate G3b), home (com confirmação), display ao vivo (500 ms) |
| **Config** | todas as chaves do PROTOCOL §4 (aplica só o que mudou), `reset_nvs` com confirmação dupla, folha de diagnóstico (`/diag`, versão, `last_cmd_id`, log) |

O botão **Abortar** fica sempre visível, acima da barra de abas.

## Regras de comportamento

- Entrada inválida ou fora da faixa **bloqueia** o envio e mostra o erro; nunca
  substitui por um valor plausível (regra de v.6 do app Windows / plano §5.1).
- Toda ação que aciona relés carrega um `cmd_id` crescente (persistido em
  `shared_preferences`) e é reentregue até 3× em timeout de rede; o nó responde
  `duplicate` a reentregas e nunca reaplica toques.
- `busy` (409) não é erro do operador: o app mostra "sequência em andamento —
  aguarde ou aborte".
- Trocar de host reinicia o poll e limpa o traço; o último host fica salvo.
- Firmware sem os campos de modo/guarda/hold (anterior ao r2) → os cartões
  correspondentes mostram "firmware sem suporte".
- A conexão continua visível, mas o badge avisa **Firmware incompatível** quando
  a versão anunciada não é r2 nem r3.x.

## Posse do Hub (firmware r3.2, app 1.2.0)

Quando a cascata do Hub está ativa, o `/status` traz `hub_owned=true`: o app mostra a faixa
laranja "Controlado pelo Hub", não envia comandos (o nó recusaria com `409 hub_owned`) e deixa
somente **Abortar**. A posse expira sozinha em até 10 s se o Hub parar de responder. Mudar
`hub_enabled` na configuração pede confirmação, pois tira/coloca o banho na cascata do Hub.

## Como rodar / gerar o APK

Requer Flutter (testado com 3.41.x). Da pasta `apps/flutter`:

```bash
flutter pub get
flutter run                 # com o celular conectado por USB (depuração)
flutter build apk --release # gera build/app/outputs/flutter-apk/app-release.apk
```

Instale o APK no celular (habilite "fontes desconhecidas") e conecte-se ao AP do banho.

## Testes

```bash
flutter analyze
flutter test
```

Os testes de modelo usam fixtures reais do `PROTOCOL.md`; o teste de serviço roda
contra um `HttpServer` falso em Dart (equivalente ao `check` do `bath_app.py`),
cobrindo `cmd_id` crescente, reentrega com o mesmo `cmd_id`, `409` como erro visível
e falha de rede persistente.
