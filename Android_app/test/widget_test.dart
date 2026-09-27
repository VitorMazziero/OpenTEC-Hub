import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:tecnal_app/main.dart';

void main() {
  testWidgets('App renders main shell and navigation destinations', (WidgetTester tester) async {
    await tester.pumpWidget(const OpenTECHubApp());

    expect(find.text('OpenTEC-Hub'), findsOneWidget);
    expect(find.text('Painel'), findsOneWidget);
    expect(find.text('Controle'), findsOneWidget);
    expect(find.text('Gráficos'), findsOneWidget);
  });

  testWidgets('Controls open on a phone-sized screen without layout errors', (WidgetTester tester) async {
    // Phone width; tall enough that every card of a list is built at once.
    tester.view.physicalSize = const Size(1080, 16000);
    tester.view.devicePixelRatio = 2.75;
    addTearDown(tester.view.reset);

    await tester.pumpWidget(const OpenTECHubApp());
    await tester.tap(find.text('Controle'));
    await tester.pumpAndSettle();

    // Control routes of temperature and agitation are visible with the cards collapsed.
    expect(find.text('Placa'), findsNWidgets(2));
    expect(find.text('Banho externo'), findsOneWidget);
    expect(find.text('Servo'), findsOneWidget);

    // Expand the temperature card: setpoint field.
    await tester.tap(find.text('Temperatura'));
    await tester.pumpAndSettle();
    expect(find.text('Setpoint'), findsOneWidget);

    for (final title in ['Agitação', 'pH', 'Oxigênio dissolvido', 'Pressão',
        'Bomba de nutriente', 'Bomba de antiespumante', 'Espuma automática']) {
      final card = find.text(title);
      await tester.ensureVisible(card.first);
      await tester.pumpAndSettle();
      await tester.tap(card.first);
      await tester.pumpAndSettle();
    }
    expect(find.text('Referência de espuma'), findsOneWidget);

    await tester.tap(find.text('Periféricos'));
    await tester.pumpAndSettle();
    for (final title in ['Bomba peristáltica', 'Fluxômetro (gases)', 'Frasco agitador',
        'Sensor de biomassa', 'Sensor de distância']) {
      final card = find.text(title);
      await tester.ensureVisible(card.first);
      await tester.pumpAndSettle();
      await tester.tap(card.first);
      await tester.pumpAndSettle();
    }
    expect(find.text('Enviar perfil'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });
}
