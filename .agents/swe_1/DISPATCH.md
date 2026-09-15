# Dispatch Log

## 2026-09-12T22:41:57-03:00
Task summary:
Auditar o firmware do sensor de distância (v11) (fontes: ConfigCodec.cpp, FirmwareApp.cpp, PROTOCOL.md), identificar/explicar inconsistências e preencher a seção 2 do arquivo COMANDOS_DISPOSITIVOS_EXTERNOS.md. Além disso, gerar um plano de implementação para corrigir as inconsistências identificadas.

Requirements:
R1. Atualizar Documentação: Preencher a seção 2 do COMANDOS_DISPOSITIVOS_EXTERNOS.md descrevendo o comportamento real do código do Sensor de Distância. Manter a mesma anatomia (tabelas de hardware, catálogo de chaves, recuperação, telemetria e limitações) usada nos outros dispositivos (ex: bomba peristáltica).
R2. Análise de Inconsistências: Explicar claramente na documentação as inconsistências entre o firmware atual (ConfigCodec.cpp, FirmwareApp.cpp) e o contrato (PROTOCOL.md). Se algo não foi implementado no código (ex: deduplicação de cmd_id, NVS só em mudança), documentar a decisão e a justificativa técnica.
R3. Plano de Correção: Criar um artefato separado de plano de implementação listando as mudanças necessárias no código-fonte (se houver) para corrigir os desvios entre o que está implementado e o que é esperado no contrato.

Acceptance Criteria:
- O arquivo COMANDOS_DISPOSITIVOS_EXTERNOS.md tem sua seção 2 preenchida seguindo estritamente os mesmos tópicos numéricos (2.0 a 2.11) do dispositivo 1.
- Todas as chaves mapeadas pelo Hub (distanceOffsetMm, distanceSamplePeriodMs, distanceSendPeriodMs, distanceResetNvs) estão explicadas em tabelas.
- O tratamento de falha do VL53L0X (push distance=-1, escada de recuperação) está documentado.
- Há um checklist claro gerado para ensaios em bancada física.
- O revisor humano pode cruzar as explicações da documentação com o código listado e confirmar sua veracidade.
