"""
Varredura Modbus RTU do Delta ASDA-B2, direto do PC.

Feito para o dongle USB-RS485. O ponto dele nao e ser mais capaz que os
sketches -- e ser INDEPENDENTE: tira o Arduino, o conversor, os jumpers
e o codigo do caminho. Se este script conversar, o drive esta bom e o
problema sempre esteve na nossa montagem. Se ele tambem calar, o
problema nunca esteve la.

Somente leitura: transmite apenas 03H e 04H. Nunca escreve parametro,
nunca habilita o servo, nunca comanda o motor.

Uso:
    python asda_scan.py                 lista as portas e usa a unica, se houver
    python asda_scan.py --port COM7
    python asda_scan.py --port COM7 --baud 9600 --so-variantes

Requer pyserial (ja instalado nesta maquina: 3.5).
"""

from __future__ import annotations

import argparse
import sys
import time

import serial
from serial.tools import list_ports

# ---------------------------------------------------------------- config

# 9600 primeiro: e o que o painel do drive mostra em P3-01 = 0011.
BAUDS = [9600, 38400, 19200, 115200, 57600, 4800]

# Tabela do P3-02: 6, 7 e 8 sao os modos RTU. Sao os unicos que importam.
FORMATOS = [
    ("8N2", serial.PARITY_NONE, serial.STOPBITS_TWO),
    ("8E1", serial.PARITY_EVEN, serial.STOPBITS_ONE),
    ("8O1", serial.PARITY_ODD, serial.STOPBITS_ONE),
]

# Secao 8.2 do manual, na descricao do P3-00: com o pedido endereçado a
# 0xFF o drive responde seja qual for o endereco configurado nele.
CURINGA = 0xFF
CANDIDATOS = [CURINGA, 0x01, 0x7F]

REG_P3_00 = 0x0300

# O P3-07 deste drive esta em 100, entao a janela precisa ser folgada.
JANELA_S = 0.5

PARAMETROS = [
    (0x0300, "P3-00", "endereco Modbus (painel: 0001)"),
    (0x0302, "P3-01", "baud (painel: 0011 = 9600)"),
    (0x0304, "P3-02", "protocolo (painel: 0066 = 8N2 RTU)"),
    (0x030A, "P3-05", "mecanismo (painel: 0000)"),
    (0x030C, "P3-06", "origem das DI - NAO ALTERAR"),
    (0x030E, "P3-07", "atraso de resposta (painel: 100)"),
    (0x0102, "P1-01", "modo de controle - NAO ALTERAR"),
    (0x0000, "P0-00", "versao de firmware"),
]


# ------------------------------------------------------------------- crc


def crc16(dados: bytes) -> int:
    crc = 0xFFFF
    for byte in dados:
        crc ^= byte
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return crc


def montar_pedido(slave: int, funcao: int, endereco: int, quantidade: int) -> bytes:
    corpo = bytes(
        [
            slave,
            funcao,
            endereco >> 8,
            endereco & 0xFF,
            quantidade >> 8,
            quantidade & 0xFF,
        ]
    )
    crc = crc16(corpo)
    return corpo + bytes([crc & 0xFF, crc >> 8])


# ---------------------------------------------------------------- enlace


def transacionar(
    porta: serial.Serial,
    slave: int,
    endereco: int,
    quantidade: int = 1,
    funcao: int = 0x03,
    janela: float = JANELA_S,
) -> tuple[bytes, bytes]:
    """Envia e devolve (pedido, tudo que chegou na janela).

    Nao filtra eco nem descarta lixo: a classificacao e feita depois,
    sobre os bytes crus.
    """
    pedido = montar_pedido(slave, funcao, endereco, quantidade)

    porta.reset_input_buffer()
    # O RTU pede 3,5 tempos de caractere de silencio antes do quadro.
    time.sleep(0.02)

    porta.write(pedido)
    porta.flush()

    recebido = bytearray()
    fim = time.monotonic() + janela
    while time.monotonic() < fim:
        pendente = porta.in_waiting
        if pendente:
            recebido += porta.read(pendente)
        else:
            time.sleep(0.005)

    return pedido, bytes(recebido)


def classificar(
    pedido: bytes, recebido: bytes, slave: int, funcao: int = 0x03
) -> tuple[str, dict]:
    """Devolve (veredito, detalhes).

    Vereditos: silencio, eco, resposta, excecao, lixo.

    O eco precisa sair ANTES da busca por quadro. Um pedido "03H, 1
    registrador" tem 8 bytes e, lido como resposta, da funcao 03H e byte
    count 0x03 -> quadro de 8 bytes cujo CRC nos bytes 6 e 7 e exatamente
    o CRC do proprio pedido. Confere sempre. Sem este descarte, um eco
    viraria "resposta valida com valor 0x0000".
    """
    if not recebido:
        return "silencio", {}

    inicio = 0
    if recebido.startswith(pedido):
        if len(recebido) == len(pedido):
            return "eco", {}
        inicio = len(pedido)

    for offset in range(inicio, max(inicio, len(recebido) - 4)):
        if slave != CURINGA and recebido[offset] != slave:
            continue

        f = recebido[offset + 1]

        if f & 0x80:
            quadro = recebido[offset : offset + 5]
            if len(quadro) == 5:
                lido = quadro[3] | (quadro[4] << 8)
                if lido == crc16(quadro[:3]):
                    return "excecao", {
                        "slave": quadro[0],
                        "codigo": quadro[2],
                    }
            continue

        if f != funcao:
            continue

        n = recebido[offset + 2]
        tamanho = n + 5
        quadro = recebido[offset : offset + tamanho]
        if len(quadro) < 7 or len(quadro) != tamanho:
            continue

        lido = quadro[-2] | (quadro[-1] << 8)
        if lido != crc16(quadro[:-2]):
            continue

        palavras = [
            (quadro[3 + 2 * i] << 8) | quadro[4 + 2 * i] for i in range(n // 2)
        ]
        return "resposta", {"slave": quadro[0], "palavras": palavras}

    return "lixo", {"bytes": recebido}


def abrir(porta_nome: str, baud: int, formato) -> serial.Serial:
    _, paridade, stopbits = formato
    return serial.Serial(
        port=porta_nome,
        baudrate=baud,
        bytesize=serial.EIGHTBITS,
        parity=paridade,
        stopbits=stopbits,
        timeout=0,
    )


# ------------------------------------------------------------------ ações


def varrer(porta_nome: str) -> tuple[int, str, int] | None:
    """Varre baud x formato x endereco. Devolve a combinacao que atender."""
    print("\n=== Varredura: baud x formato x endereco ===")
    print("0xFF e o curinga: o drive atende seja qual for o P3-00.\n")

    for baud in BAUDS:
        for formato in FORMATOS:
            nome = formato[0]
            with abrir(porta_nome, baud, formato) as porta:
                time.sleep(0.05)
                linha = f"{baud:<7}{nome:<5}: "
                achou = None

                for slave in CANDIDATOS:
                    pedido, recebido = transacionar(porta, slave, REG_P3_00)
                    veredito, det = classificar(pedido, recebido, slave)

                    if veredito == "resposta":
                        valor = det["palavras"][0] if det["palavras"] else 0
                        linha += f"0x{slave:02X}=RESPOSTA(de 0x{det['slave']:02X}, 0x{valor:04X})  "
                        achou = (baud, nome, det["slave"])
                    elif veredito == "excecao":
                        linha += f"0x{slave:02X}=EXCECAO(0x{det['codigo']:02X})  "
                        achou = (baud, nome, det["slave"])
                    elif veredito == "lixo":
                        linha += f"0x{slave:02X}=lixo({det['bytes'].hex(' ')})  "
                    else:
                        linha += f"0x{slave:02X}={veredito}  "

                print(linha)
                if achou:
                    print("\n>>> ACHOU. Anote esta combinacao.")
                    return achou

    print("\nNada respondeu, em nenhuma combinacao -- nem no curinga.")
    return None


def variantes(porta_nome: str, baud: int) -> None:
    """Muda a FORMA do pedido, mantendo baud e formato.

    Motivo: toda pagina de parametro do manual lista DOIS enderecos --
    "P3-00 ... Address: 0300H, 0301H". Parametros Delta ocupam dois
    registradores consecutivos. Ler um so pode ser leitura parcial de um
    bloco de 32 bits, e ha implementacoes que respondem a isso com
    excecao ou com nada.

    Uma EXCECAO aqui vale ouro: excecao com CRC valido prova que o drive
    recebeu, entendeu e recusou -- ou seja, que ele nos ouve.
    """
    print(f"\n=== Variacoes de protocolo em {baud} 8N2 ===")
    print("Qualquer resposta, inclusive excecao, prova que o drive ouve.\n")

    casos = [
        (0x03, 0x0300, 1, "03H P3-00 x1  (o que sempre usamos)"),
        (0x03, 0x0300, 2, "03H P3-00 x2  (parametro completo, 32 bits)"),
        (0x03, 0x0000, 1, "03H P0-00 x1  (versao de firmware)"),
        (0x03, 0x0000, 2, "03H P0-00 x2"),
        (0x03, 0x0102, 2, "03H P1-01 x2  (modo de controle)"),
        (0x04, 0x0300, 2, "04H P3-00 x2  (input registers)"),
    ]

    with abrir(porta_nome, baud, FORMATOS[0]) as porta:
        time.sleep(0.05)
        for funcao, endereco, quantidade, rotulo in casos:
            for slave in (CURINGA, 0x01):
                pedido, recebido = transacionar(
                    porta, slave, endereco, quantidade, funcao
                )
                veredito, det = classificar(pedido, recebido, slave, funcao)

                saida = f"  0x{slave:02X}  {rotulo:<42} -> {veredito}"
                if veredito == "resposta":
                    saida += "  " + " ".join(f"0x{p:04X}" for p in det["palavras"])
                elif veredito == "excecao":
                    saida += f" 0x{det['codigo']:02X}  <<< O DRIVE NOS OUVE"
                elif veredito == "lixo":
                    saida += "  " + det["bytes"].hex(" ")
                print(saida)


def ler_parametros(porta_nome: str, baud: int, formato_nome: str, slave: int) -> None:
    formato = next(f for f in FORMATOS if f[0] == formato_nome)
    print(f"\n=== Parametros em 0x{slave:02X} ===\n")
    with abrir(porta_nome, baud, formato) as porta:
        time.sleep(0.05)
        for endereco, nome, nota in PARAMETROS:
            pedido, recebido = transacionar(porta, slave, endereco, 2)
            veredito, det = classificar(pedido, recebido, slave)
            if veredito == "resposta" and det["palavras"]:
                valor = f"0x{det['palavras'][0]:04X}"
            else:
                valor = "  ----"
            print(f"{nome} @0x{endereco:04X} = {valor}   {nota}")


# ------------------------------------------------------------------- cli


def escolher_porta(preferida: str | None) -> str | None:
    portas = list(list_ports.comports())
    if not portas:
        print("Nenhuma porta serial encontrada.")
        return None

    print("Portas disponiveis:")
    for p in portas:
        print(f"  {p.device:<8} {p.description}")

    if preferida:
        return preferida
    if len(portas) == 1:
        print(f"\nUsando a unica disponivel: {portas[0].device}")
        return portas[0].device

    print("\nHa mais de uma porta. Escolha com --port.")
    return None


def main() -> int:
    ap = argparse.ArgumentParser(description="Varredura Modbus RTU do ASDA-B2")
    ap.add_argument("--port", help="porta serial, ex. COM7")
    ap.add_argument("--baud", type=int, default=9600, help="baud das variantes")
    ap.add_argument(
        "--so-variantes",
        action="store_true",
        help="pula a varredura e testa so as formas de pedido",
    )
    args = ap.parse_args()

    porta_nome = escolher_porta(args.port)
    if not porta_nome:
        return 1

    if args.so_variantes:
        variantes(porta_nome, args.baud)
        return 0

    achado = varrer(porta_nome)

    if achado:
        baud, formato_nome, slave = achado
        ler_parametros(porta_nome, baud, formato_nome, slave)
        return 0

    # Silencio na varredura inteira: talvez a FORMA do pedido seja o problema.
    variantes(porta_nome, args.baud)
    return 0


if __name__ == "__main__":
    sys.exit(main())
