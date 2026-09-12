import 'package:flutter_test/flutter_test.dart';
import 'package:app/main.dart';
import 'package:app/pages/agitator_control_page.dart';
import 'package:app/services/agitator_service.dart';

void main() {
  testWidgets('Agitator control surface builds', (WidgetTester tester) async {
    final service = AgitatorService();
    await tester.pumpWidget(AgitatorApp(service: service));
    expect(find.byType(AgitatorControlPage), findsOneWidget);
    service.dispose();
  });
}
