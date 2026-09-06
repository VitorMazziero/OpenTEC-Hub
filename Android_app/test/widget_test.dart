import 'package:flutter_test/flutter_test.dart';
import 'package:tecnal_app/main.dart';

void main() {
  testWidgets('App renders main shell and navigation destinations', (WidgetTester tester) async {
    await tester.pumpWidget(const OpenTECHubApp());

    expect(find.text('OpenTEC-Hub'), findsOneWidget);
    expect(find.text('Dashboard'), findsOneWidget);
    expect(find.text('Controls'), findsOneWidget);
    expect(find.text('Graphs'), findsOneWidget);
  });
}
