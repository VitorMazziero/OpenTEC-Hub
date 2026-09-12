# Aplicativo do fluxômetro

Aplicativo Flutter ativo (linha v05) para controle do fluxômetro. O firmware correspondente está em `../../firmware/flowmeter`.

Direct WebSocket commands carry a session ID and command ID. The app waits for
the firmware acknowledgement and makes up to five delivery attempts at 400 ms
intervals. The firmware ignores duplicate or stale retries.

```powershell
flutter pub get
flutter analyze
flutter test
flutter run
```

O cliente anterior foi preservado em `../../archive/apps/flutter-pre-v05`.
