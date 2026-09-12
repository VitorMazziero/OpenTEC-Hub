# Aplicativo da bomba peristáltica

Aplicativo Flutter para controle e acompanhamento da bomba. O firmware correspondente está em `../../firmware/peristaltic-pump`.

```powershell
flutter pub get
flutter analyze
flutter test
flutter run
```

Antes de alterar comandos ou campos, consulte `../../docs/PROTOCOL.md`. A validação sem hardware não comprova parada, vazão ou volume dosado.
