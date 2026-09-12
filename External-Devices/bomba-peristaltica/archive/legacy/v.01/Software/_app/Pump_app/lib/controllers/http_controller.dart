import 'package:http/http.dart' as http;

class HttpController {
  final String baseUrl = 'http://192.168.4.1'; // ESP32's IP in AP mode

  // Method to send a command to the ESP32
  Future<void> sendCommand(String message) async {
    //try {
    await http.post( //final response =
      Uri.parse('$baseUrl/command'),
      headers: <String, String>{
        'Content-Type': 'text/plain; charset=UTF-8',
      },
      body: message,
    );

    //   if (response.statusCode == 200) {
    //     // Successful response handling, if needed
    //     print('Command sent successfully');
    //   } else {
    //     // Handle non-200 status code, if needed
    //     print('Failed to send command. Status code: ${response.statusCode}');
    //   }
    // } catch (e) {
    //   print('Error sending command: $e');
    // }
  }

  // Method to read data from the ESP32
  Future<Map<String, double>> readAndParseData() async {
    try {
      final response = await http.get(Uri.parse('$baseUrl/readData'))
          .timeout(const Duration(seconds: 20)); // Set the timeout to 20 seconds

      final String data = response.body;
      //print(data);
      // Parse the data
      final regExp = RegExp(r'\{(\d*\.?\d+),(\d*\.?\d+)\}');
      final match = regExp.firstMatch(data);

      if (match != null && match.groupCount == 2) {
        double stepperSpeed = double.parse(match.group(1) ?? '0');
        double t = double.parse(match.group(2) ?? '0');
        //print({'FlowRate': stepperSpeed, 'Time': t});
        return {'FlowRate': stepperSpeed, 'Time': t};
      } else {
        //print('Failed to parse: $data');
        return {'FlowRate': 0, 'Time': 0};
      }
    } catch (e) {
      // Handle or log the error
      //print('Error fetching data: ${e.toString()}');
      return {'FlowRate': 0, 'Time': 0};
    }
  }

}
