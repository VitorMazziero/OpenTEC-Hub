# Validação — Bomba peristáltica

## Automatizada concluída

- compilação do firmware ativo;
- verificação de preservação do manifesto SHA-256;
- verificação estática de rotas e campos críticos contra o Hub;
- baseline monolítico mantido para auditoria.

- simulação no PC (`tests/host-sim`): `cmd_id` inteiro, validação do quadro inteiro, regras e compensação da dosagem (3.13).

## Portão G0 (3.12.1, com Hub 10.8.1 e app 0.31.1) — pendente

1. 10 comandos seguidos pelo app confirmados, com `PumpCommandPending` apagando em ≤ 3 s.
2. Reinício em repouso mantém `PumpVol`.
3. Perfil, depois `speed` (calibração), depois reinício: o perfil não volta.

## Bancada pendente

1. Gravar a placa correta e capturar versão/toolchain.
2. Confirmar boot, reconexão e ausência de reset cíclico.
3. Comparar telemetria e comandos com o baseline.
4. Testar perda de pacote, reinício do nó e reinício do Hub.
5. Validar sensores/atuadores em limites e estado seguro.
6. Executar soak test e arquivar logs em `tests/evidence`.

A modularização mantém uma única unidade de tradução para preservar globais e inicialização. O perfil de dosagem e a parada física precisam de bancada.
