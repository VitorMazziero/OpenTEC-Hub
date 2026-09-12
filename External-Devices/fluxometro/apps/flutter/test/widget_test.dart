import 'package:flutter_test/flutter_test.dart';
import 'package:flowmeter_desktop/main.dart';

void main() {
  testWidgets('Flowmeter control surface builds', (WidgetTester tester) async {
    await tester.pumpWidget(const MyApp());
    expect(find.byType(MyHomePage), findsOneWidget);
  });
}
