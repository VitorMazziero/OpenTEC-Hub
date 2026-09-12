import 'package:connectivity_plus/connectivity_plus.dart';

class ConnectivityService {
  Future<bool> checkWifiConnection() async {
    var connectivityResult = await Connectivity().checkConnectivity();
    return connectivityResult == ConnectivityResult.wifi;
  }

  Stream<bool> onConnectivityChanged() {
    return Connectivity().onConnectivityChanged.map((result) => result == ConnectivityResult.wifi);
  }
}