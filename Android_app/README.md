# tecnal_app

TECNAL control module

## Banho externo C404 (Hub 10.6)

Na aba **Controles › Biorreator**, a linha de temperatura mostra a via informada pelo Hub. Na via
externa o valor enviado é a **referência do reator** para a cascata do Hub; desligar a linha (ou a
parada de emergência) **para a cascata e deixa o C404 em manual no último SP** — o banho não é
desligado por software. O cartão **Banho externo C404** mostra `Tempval`, referência, PV/SP do
C404, saída da cascata, posse do Hub e o motivo de espera/falha, e oferece **Parar banho**,
**Reset falha** e a chave **Guarda automática (C404)**. Troca de via e sintonia continuam no app
Windows. Setpoint inválido é recusado (não vira 25 °C).

## Getting Started

This project is a starting point for a Flutter application.

A few resources to get you started if this is your first Flutter project:

- [Lab: Write your first Flutter app](https://docs.flutter.dev/get-started/codelab)
- [Cookbook: Useful Flutter samples](https://docs.flutter.dev/cookbook)

For help getting started with Flutter development, view the
[online documentation](https://docs.flutter.dev/), which offers tutorials,
samples, guidance on mobile development, and a full API reference.
