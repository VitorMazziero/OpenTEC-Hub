import 'package:http/http.dart' as http;

class HttpController {
  static final HttpController _instance = HttpController._internal();

  factory HttpController() {
    return _instance;
  }

  HttpController._internal();

  final String baseUrl = 'http://192.168.4.1'; // ESP32's IP in AP mode

  // Method to send a command to the ESP32
  Future<void> sendCommand(String message) async {
    //try {
    print(message);
    await http.post(
      Uri.parse('$baseUrl/command'),
      headers: <String, String>{
        'Content-Type': 'text/plain; charset=UTF-8',
      },
      body: message,
    );
  }

  // Method to read data from the ESP32
  Future<Map<String, double>> readAndParseData() async {
    try {
      final response = await http.get(Uri.parse('$baseUrl/readData'))
          .timeout(const Duration(seconds: 20)); // Set the timeout to 20 seconds

      final String data = response.body;
      // Parse the data
      final regExp = RegExp(r'\{(\d*\.?\d+),(\d*\.?\d+)\}');
      final match = regExp.firstMatch(data);
      print(data);
      if (match != null && match.groupCount == 2) {
        double stepperSpeed = double.parse(match.group(1) ?? '0');
        double t = double.parse(match.group(2) ?? '0');
        print(stepperSpeed);
        print(t);
        return {'FlowRate': stepperSpeed, 'Time': t};
      } else {
        return {'FlowRate': 0, 'Time': 0};
      }
    } catch (e) {
      // Handle or log the error
      return {'FlowRate': 0, 'Time': 0};
    }
  }
}
