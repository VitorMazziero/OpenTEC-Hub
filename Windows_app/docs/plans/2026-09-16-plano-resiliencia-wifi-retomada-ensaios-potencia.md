# Plano de Resiliência a Quedas de Conexão e Retomada de Ensaios de Potência

**Data:** 2026-09-16  
**Autor:** Antigravity / Equipe OpenTEC-Hub  
**Estado:** Proposta técnica para revisão e aprovação  
**Escopo:** `Windows_app/src/OpenTECHub` (Módulo de Potência, Árbitro de Comandos, Gerenciador de Conexão) e firmware `ESP32S3-HUB`.

---

## 1. Contexto e Diagnóstico

Durante a realização de um ensaio cinético de potência de impelidores via conexão Wi-Fi, ocorreu uma desconexão transitória do enlace (*Wi-Fi drop*). O sistema comportou-se da seguinte forma:

1. **Aborto Seguro do Link (Comportamento Previsto):**
   - O `ConnectionManager` detectou ausência de telemetria no timeout de 8 segundos (`TelemetrySilenceTimeout`) ou falha na requisição HTTP, transitando o estado para `ConnectionState.Reconnecting`.
   - O `CommandArbiter.OnInnerStateChanged` identificou a queda do enlace com atuadores físicos sob controle da automação (`CommandOwner.PowerAssay`).
   - Conforme diretriz de segurança de bancada ([ADR D-035/D-036](../DECISIONS.md)), o árbitro revogou a posse dos atuadores para `CommandOwner.Manual` com `isSafeAbort: true`.
   - O `PowerTestRunner.OnOwnershipRevoked` capturou o evento e executou `FaultWithoutSafeCommand("A posse dos atuadores foi revogada pelo aborto seguro do link.")`.
   - O ensaio foi interrompido (`PowerTestStatus.Interrupted`), a corrida atual foi descartada e o servomotor parou.

2. **Comportamento Pós-Reconexão (Problema Identificado):**
   - Quando o Wi-Fi restabeleceu a conexão (`ConnectionState.Connected`), o ensaio **não retornou automaticamente** e permaneceu no estado `Interrompido · edição liberada`.
   - O operador precisa intervir manualmente na interface para tentar retomar o ensaio.
   - Caso tente clicar em `▶ Iniciar/continuar` imediatamente, o botão pode estar desabilitado temporariamente até que a primeira telemetria válida do servo seja decodificada (`HasValidServoMeasurement`), gerando incerteza sobre a integridade dos pontos já medidos.

---

## 2. Soluções Propostas

Apresentam-se três frentes de solução complementares:

---

### Solução 1: Operacional e Imediata (Recomendação de Bancada)

Para ensaios cinéticos automatizados de alta precisão (curvas de potência $N_p \times Re$, aeração $P_G / P_0$ e ensaios de $k_L a$):

* **Priorizar Enlace USB:** Recomenda-se utilizar cabo USB blindado de alta qualidade com filtro de ferrite nas extremidades. A comunicação serial direta via USB CDC/CH343 opera com latência inferior a 10 ms e é imune a flutuações de radiofrequência, interferências eletromagnéticas de roteadores e concorrência de banda do ambiente de laboratório.
* **Se o uso de Wi-Fi for mandatório:**
  1. Utilizar Access Point dedicado na bancada, fixado em canal de 2.4 GHz com menor ruído (ex.: Canais 1, 6 ou 11) ou banda de 5 GHz (se suportado pelo hardware).
  2. Desativar economia de energia no adaptador Wi-Fi do Windows (*Energy Efficient Ethernet / Wi-Fi Sleep*).
  3. No firmware do ESP32-S3, garantir `esp_wifi_set_ps(WIFI_PS_NONE)` para eliminar picos de latência por sono de rádio.

---

### Solução 2: Aplicação Windows (Resiliência do OpenTEC-Hub)

Propõem-se três níveis de evolução no aplicativo C# / WPF:

#### Nível A: Janela de Tolerância a Quedas Transitórias (*Grace Period / Transient Pause*)
* **Conceito:** Uma queda curta de sinal Wi-Fi (ex.: 2 a 10 segundos) não deve condenar imediatamente todo o ensaio de bancada nem forçar o reinício da campanha inteira.
* **Mecanismo:**
  1. Ao detectar queda de enlace durante o ensaio (`change.State != Connected`), o árbitro revoga a posse para `Manual` para garantia estrita de segurança física (desengatando o envio de comandos).
  2. No entanto, em vez de transitar terminalmente para `PowerRunPhase.Faulted` e `PowerTestStatus.Interrupted`, o `PowerTestRunner` transita para uma nova fase intermediária: `PowerRunPhase.PausedForLinkRecovery`.
  3. Dispara-se um temporizador de tolerância (*Grace Timeout*, configurável entre 10 e 30 segundos).
  4. Se o link reconectar dentro da janela e a telemetria do servo estiver íntegra (`HasValidServoMeasurement`):
     - O ensaio re-assume a posse (`Claim(CommandOwner.PowerAssay)`).
     - A réplica que estava em execução é limpa e reiniciada a partir do zero (para evitar dados corrompidos por perda de rotação).
     - O ensaio prossegue sem intervenção manual.
  5. Se o temporizador expirar sem restabelecimento do enlace, o runner consolida o ensaio como `PowerTestStatus.Interrupted` e notifica o operador.

#### Nível B: Opção Explícita de Retomada Automática (*Auto-Resume on Link Restore*)
* **Conceito:** Para ensaios de longa duração onde o operador não está em frente ao computador, permitir que ensaios interrompidos por perda de link retomem automaticamente assim que a conexão se estabilize.
* **Mecanismo:**
  1. Adicionar na tela de configuração e no documento de ensaio a propriedade:
     `AutoResumeOnLinkRestore: bool` (padrão: desativado por segurança, ativável pelo operador).
  2. Quando ativado: se o documento estiver em `PowerTestStatus.Interrupted` devido à perda de conexão, o evento `ConnectionState.Connected` monitora o retorno de telemetria válida.
  3. Assim que `CanStart(CurrentTest)` retornar `true`, o sistema agenda a execução de `StartOrContinueAsync()` após um tempo de acomodação (ex.: 3 segundos de telemetria estável).
  4. A corrida em andamento no momento da queda é reiniciada na mesma condição pendente; os pontos já aceitos anteriormente são 100% preservados.

#### Nível C: Aprimoramento de UX / UI no `PowerView`
* **Conceito:** O operador deve entender imediatamente o que aconteceu e ter um botão de ação único e óbvio.
* **Mecanismo:**
  1. Quando o ensaio for interrompido por queda de enlace, exibir um cartão/banner de alerta âmbar em destaque no topo da tabela de condições:
     > **⚠️ Ensaio pausado por perda de sinal Wi-Fi.**  
     > *A conexão foi restabelecida. 4 de 10 condições já foram concluídas com sucesso.*  
     > `[ ▶ Retomar Ensaio a partir da condição atual ]`
  2. Enquanto a telemetria pós-reconexão ainda não estiver válida (primeiros 2 segundos), o botão exibe o motivo detalhado em seu ToolTip: *"Aguardando primeira leitura estável do servo drive..."*.

---

### Solução 3: Firmware ESP32-S3 (DESCARTADA / NÃO EXECUTADA)

> [!NOTE]
> **Decisão do Usuário (2026-09-16):** Nenhuma alteração no firmware do ESP32 ou implementação de watchdog local de hardware (4s) deve ser executada. Todas as soluções devem ser 100% contidas na aplicação desktop (`Windows_app`).

* ~~**Watchdog de Comunicação no Hub:** O firmware deve implementar um *Command Watchdog*: se o Hub deixar de receber requisições do PC por mais de $T_{watchdog}$ (ex.: 4 segundos) enquanto o motor estiver girando, o próprio ESP32 comanda a parada do servomotor via Modbus/relé localmente.~~ *(Excluído do escopo)*
* ~~**Desativação de Economia de Energia Wi-Fi:** Configurar `esp_wifi_set_ps(WIFI_PS_NONE)` na inicialização da rede Wi-Fi do ESP32.~~ *(Excluído do escopo)*

---

## 3. Matriz Comparativa de Soluções

| Solução | Esforço de Implementação | Impacto na Segurança | Resolução do Problema | Status |
| :--- | :--- | :--- | :--- | :--- |
| **1. Uso de Cabo USB** | Imediato (sem alteração de código) | Máxima (enlace determinístico) | Elimina 100% das quedas por instabilidade Wi-Fi | Em uso pelo operador |
| **2. Retomada Automática (3s estável)** | Médio (~2 dias) | Muito Alto (motor seguro, ensaio aguarda até 30s) | Recupera quedas momentâneas sem perder o ensaio | **Implementada** |
| **3. Banner UI + Retomada Manual** | Baixo (~1 dia) | Excelente (decisão explícita do operador) | Resolve a ambiguidade caso auto-resume esteja desativado | **Implementada** |
| **4. Alterações no Firmware ESP32** | N/A | N/A | N/A | **Descartada pelo usuário** |

---

## 4. Roteiro de Implementação

1. **Fase 1 (Imediata):** Utilizar conexão USB para a campanha de ensaios atual enquanto as alterações de software são validadas. *(Ativa)*
2. **Fase 2 (App - UX & Resiliência):**
   - Implementar propriedades `AutoResumeOnLinkRestore` e `LinkRecoveryTimeoutSeconds` em `PowerTestSettings` e `PowerTestViewModel`. *(Concluída)*
   - Integrar comandos de retomada no `PowerTestViewModel` (`StartOrContinueAsync` / `PauseResumeAsync`). *(Concluída)*
3. **Fase 3 (Core - Grace Period e Estabilização de Telemetria de 3s):**
   - Implementar a fase `PausedForLinkRecovery` no `PowerTestRunner`. *(Concluída)*
   - Implementar janela de estabilização estrita de 3 segundos de telemetria válida do servo drive pós-reconexão antes do auto-resume. *(Concluída)*
   - Criar testes unitários em `OpenTECHub.Tests` cobrindo queda, janela de 3s, timeout de 30s e retomada manual. *(Concluída)*
4. **Fase 4 (Firmware ESP32):**
   - *(Descartada)* Sem alterações de firmware no ESP32. O hardware permanece intacto.
