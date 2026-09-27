# OpenTEC-Hub — aplicativo Android

Aplicativo Flutter para ajustar setpoints do biorreator pelo celular quando o aplicativo Windows não
está em uso. Conecta-se ao TECNAL-Hub (ESP32-S3, firmware 10.6, protocolo 10) pela rede Wi-Fi do Hub
(`192.168.4.1` por padrão).

## Telas

| Aba | Conteúdo |
|---|---|
| **Painel** | Leituras com o setpoint em uso: temperatura, agitação, pH, OD, pressão, espuma; resumo do banho C404; periféricos ligados |
| **Controle** | Um cartão recolhível por variável, com liga/desliga, setpoint e **Aplicar**. *Biorreator*: temperatura (via placa ou banho externo, comunicação com o banho, cartão do C404), agitação (via servo, o padrão, ou placa; a chave liga/desliga e desligar para o motor na hora), pH, OD, pressão, nutriente, antiespumante e espuma automática. *Periféricos*: bomba peristáltica, fluxômetro, frasco agitador, biomassa e distância |
| **Gráficos** | Histórico recente das leituras |

O botão vermelho no topo é a parada geral (`resetVariables`). Com o banho externo, parar a
temperatura não desliga o C404: ele fica em manual no último SP.

As vias de controle da temperatura (`tempControlMode`) e da agitação (`motorControlMode`) ficam no
cabeçalho dos cartões, sempre visíveis. Trocar a via muda o atuador que o Hub usa, fica gravado no Hub e
desliga a variável: é preciso enviar um novo setpoint.

O frasco agitador não tem chave de comunicação no Hub (ele aparece online quando o nó envia dados);
a chave do cartão liga e desliga o agitador.

Ficam só no aplicativo Windows: sintonia da cascata e dos controladores, calibrações, controle de OD, kLa, potência, receitas e diagnósticos de bancada.

## Código

- `lib/screens/controls/`: uma seção por variável (`temperature_section.dart`, `process_sections.dart`,
  `dosing_sections.dart`, `peripheral_sections.dart`, `pump_section.dart`).
- `lib/providers/device_control_provider.dart`: todos os comandos enviados a `POST /command`.
- `lib/models/`: leitura da telemetria de `GET /readData`.
- `lib/theme/app_theme.dart`: tema claro/escuro e cores das variáveis.

## Compilar e testar

```bash
flutter test
flutter build apk --release
```

O APK sai em `build/app/outputs/flutter-apk/app-release.apk`. Para publicar, veja
[DOWNLOADS.md](../DOWNLOADS.md) e `tools/Build-ReleaseAssets.ps1` na raiz do repositório.
