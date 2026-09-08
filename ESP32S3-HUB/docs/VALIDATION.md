# Validação do Hub 10 e comando direto ASDA-B2

## Verificações locais

```powershell
python .\tools\verify_contract.py
.\tools\compile.ps1
git diff --check
git fsck --full
git status --short --branch
```

O script preserva os dez hashes legados e testa endpoints/chaves, fila Servo,
presença e a mailbox latest-wins do motor. Build e fixtures não aprovam hardware.

## Ordem de gravação

1. Motor em zero, eixo sem carga e E-stop acessível.
2. Gravar primeiro `ASDA_B2_Servo_Node` 2.0 no ESP32S3-driver.
3. Confirmar que, diante do Hub antigo, ele permanece passivo e P3-06 continua
   físico.
4. Gravar depois este Hub 10.
5. Antes de mover, exigir capacidade, parada aplicada, ACK igual ao cmd_id e
   falha zero em `/readData`.

## Matriz física obrigatória

1. Confirmar P1-01=2 e conferir no log do driver a linha `Mapa de DIs`, que
   informa onde estão SON, SPD0 e SPD1 e qual máscara P3-06 será usada. As
   posições variam por instalação e não devem ser presumidas.
2. Confirmar setpoint zero no boot do Hub e após reboot de cada ESP32.
3. Ensaiar 100, 250, 500, 750, 950 e 1000 rpm; comparar P1-09, `ServoRpm` e
   painel do drive.
4. Em 1000 rpm, confirmar P1-09 raw `[0x2710,0x0000]` e ausência de comandos
   `1V`/`<N>A` de motor na UART do Hub.
5. Confirmar ACK somente depois do readback e `ServoMotorCommandPending=false`.
6. Comandar zero; confirmar P1-09=0, SON off e eixo parado.
7. Interromper Wi-Fi do driver por mais de 3 s durante baixa rotação: falha 3,
   P1-09=0 e SON off.
8. Reiniciar o driver durante sessão direta: a recuperação pré-Wi-Fi deve tirar
   SON.
9. Religando o drive, confirmar reaplicação de P2-30 e da máscara P3-06 do mapa
   descoberto, com heartbeat válido e SPD1 mantido em zero.
10. Configurar e ensaiar P3-03/P3-10 para falha total do mestre RS-485.
11. Verificar reset de energia, limites de `servoPollMs`, presença e reconexão.
12. Revalidar o ACK revisionado do fluxômetro, que é caminho retido.
13. Executar soak de duas horas, registrando heap, `ServoCommErr`, alarmes e rail
    de 5 V.

## Gate de liberação

Não criar tag de release nem operar sem supervisão enquanto os testes de perda,
timeout interno do drive e E-stop não estiverem aprovados. O procedimento de
rollback está no plano de migração do projeto de potência.
