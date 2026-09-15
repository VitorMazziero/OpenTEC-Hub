# Aplicativo da bomba peristáltica

Aplicativo Flutter para controle e acompanhamento da bomba. O firmware correspondente está em `../../firmware/peristaltic-pump`.

A calibração por mangueira pertence ao OpenTEC-Hub para Windows, que implementa o contrato
polinomial completo e mantém os perfis no workspace. A antiga tela linear foi removida deste
aplicativo para que ele não possa emitir um comando incompatível com o firmware atual.

```powershell
flutter pub get
flutter analyze
flutter test
flutter run
```

Antes de alterar comandos ou campos, consulte `../../docs/PROTOCOL.md`. A validação sem hardware não comprova parada, vazão ou volume dosado.
