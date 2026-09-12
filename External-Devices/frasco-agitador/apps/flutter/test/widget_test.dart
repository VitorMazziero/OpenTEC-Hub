import 'package:flutter_test/flutter_test.dart';
import 'package:app/main.dart';

void main() {
  testWidgets('Agitator control surface builds', (WidgetTester tester) async {
    await tester.pumpWidget(const MotorControlApp());
    expect(find.byType(ControlPage), findsOneWidget);
  });
}
