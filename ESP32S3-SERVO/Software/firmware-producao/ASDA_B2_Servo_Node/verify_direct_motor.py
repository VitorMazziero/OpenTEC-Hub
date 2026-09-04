#!/usr/bin/env python3
from pathlib import Path


SKETCH = Path(__file__).with_name("ASDA_B2_Servo_Node.ino")


def speed_words(rpm: int) -> tuple[int, int]:
    if not 0 <= rpm <= 1000:
        raise ValueError("rpm fora de 0..1000")
    raw = rpm * 10
    return raw & 0xFFFF, raw >> 16


def main() -> None:
    source = SKETCH.read_text(encoding="utf-8")
    required = {
        "P1-09": "REG_INTERNAL_SPEED_1 = 0x0112",
        "P2-30 RAM": "PARAMETER_WRITE_RAM_ONLY = 5",
        "P3-06": "SOFTWARE_DI_MASK_SON_SPEED = 0x000D",
        "P3-06 physical": "SOFTWARE_DI_MASK_PHYSICAL = 0x0000",
        "P4-07 stop": "SOFTWARE_DI_STOP = 0x0004",
        "P4-07 run": "SOFTWARE_DI_RUN = 0x0005",
        "Modbus 10H": "request[1] = 0x10",
        "profile guard": "validateDirectControlProfile",
        "readback": "writeAndConfirmInternalSpeed",
        "lease stop": "MOTOR_FAULT_LEASE_EXPIRED",
        "boot recovery": "recoverStaleDirectControlAtBoot",
        "release UART/CN1": "releaseMotorToUartCn1",
        "route request": "motor_route",
        "route acknowledgement": "motor_route_ack",
        "ack": "motorCommandAck",
    }
    missing = [name for name, token in required.items() if token not in source]
    assert not missing, f"regras ausentes: {missing}"
    assert speed_words(0) == (0x0000, 0x0000)
    assert speed_words(1000) == (0x2710, 0x0000)
    try:
        speed_words(1001)
    except ValueError:
        pass
    else:
        raise AssertionError("1001 rpm deveria ser rejeitado")
    assert source.index("writeAndConfirmInternalSpeed(0)", source.index("releaseMotorToUartCn1")) < source.index(
        "REG_SOFTWARE_DI_MASK, SOFTWARE_DI_MASK_PHYSICAL", source.index("releaseMotorToUartCn1")
    ), "P3-06 deve ser liberado somente depois da parada"
    print("OK: rotas Modbus/UART, guardas, break-before-make e word order de P1-09")


if __name__ == "__main__":
    main()
