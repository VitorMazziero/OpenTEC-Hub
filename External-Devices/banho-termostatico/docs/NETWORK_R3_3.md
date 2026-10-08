# Rede e OTA — BathClient r3.3 (2026-10-07)

## Correções

- Sem scan: tentativas diretas em `ModuloTECNAL_1` e `ModuloTECNAL_2`, canal 6,
  alternadas após timeout de 10 s. Ao perder uma associação, tenta primeiro o último
  Hub conectado. O AP local permanece ativo.
- Watchdog de oito falhas reassocia com `WiFi.disconnect(false, false)` e espera
  500 ms. O firmware controla os retries; auto-reconnect do driver desabilitado.
- `/nodeHello` precisa aceitar o registro antes de enviar `/bathData`. Falhas de
  hello contam para o watchdog e têm retry de 2 s; hello periódico continua a 30 s.
  Telemetria 403/404 invalida o registro e a posse; refaz hello sem contar esse
  evento como falha de transporte. O backoff de telemetria permanece para outras falhas.
- Durante OTA, a tarefa de rede pausa sem desconectar a STA, inclusive quando
  a flag ao vivo já mudou e o snapshot ainda não. `checkWifi` também protege OTA.
- NVS vazia usa `hub_enabled=1`. Valores já salvos, incluindo `0`, são preservados.
  Setpoint, modo, limites, calibração e configuração das teclas/display não são migrados.

## Atualização direta pelo AP do banho

Conecte o PC à rede **Banho Termostatico**, sem senha. Execute no PowerShell:

```powershell
& 'D:\OneDrive\PosDoc_Fapesp\_Automacao_e_Controle\ProjetoTECNAL\External-Devices\tools\Publish-OtaFirmware.ps1' bath 192.168.8.1 -BinaryPath 'D:\OneDrive\PosDoc_Fapesp\_Automacao_e_Controle\ProjetoTECNAL\External-Devices\tools\.build\bath\thermostatic-bath.ino.bin'
```

O comando envia apenas a imagem de aplicação compilada para ESP32-S3 a `/update`.
Após o reinício, consulte `http://192.168.8.1/status` e confirme `r3.3` no campo `version`.
Consulte `/config`; se a instalação anterior salvou `hub_enabled=0`, habilite a
integração na configuração local. Alternativa PowerShell, conectado ao banho:

```powershell
Invoke-RestMethod -Uri 'http://192.168.8.1/config' -Method Post -ContentType 'application/json' -Body '{"hub_enabled":1}'
```

## Validação

Compilação ESP32-S3 aprovada: aplicação 1.107.165 bytes (84% da partição), globais
67.296 bytes (20%). Simulação de setpoint, simulação da reconexão e simulação da
tarefa HubLink executam os fontes reais com periféricos simulados. Contratos do Hub
verificam compatibilidade r3.x, campos, comandos e posse.

Validação física pendente: reiniciar a Central e confirmar novo hello/telemetria;
desligar ambos os Hubs e conferir a UI local; religar A/B e confirmar recuperação;
atualizar por AP e conferir versão/configuração após reboot. Os testes no PC não
validam rádio, concorrência entre núcleos, DHCP, relés ou upload real.

`CURRENT_STATUS.md` registra a bancada anterior (r3.2); não é evidência física de r3.3.
