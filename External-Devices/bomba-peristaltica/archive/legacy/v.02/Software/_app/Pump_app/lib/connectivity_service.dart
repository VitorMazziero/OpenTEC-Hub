import 'package:connectivity_plus/connectivity_plus.dart';
import 'package:network_info_plus/network_info_plus.dart';

class ConnectivityService {
  Future<bool> checkAPConnection() async {
    var connectivityResult = await Connectivity().checkConnectivity();
    if (connectivityResult == ConnectivityResult.wifi) {
      String? ssid = await NetworkInfo().getWifiName();
      return _isDesiredSSID(ssid);
    }
    return false;
  }

  Stream<bool> onConnectivityChanged() {
    return Connectivity().onConnectivityChanged.asyncMap((result) async {
      if (result == ConnectivityResult.wifi) {
        String? ssid = await NetworkInfo().getWifiName();
        return _isDesiredSSID(ssid);
      }
      return false;
    });
  }

  bool _isDesiredSSID(String? ssid) {
    if (ssid == null) return false;

    // Remove leading and trailing quotation marks
    String formattedSSID = ssid;
    if (ssid.startsWith('"') && ssid.endsWith('"')) {
      formattedSSID = ssid.substring(1, ssid.length - 1);
    }

    return formattedSSID.startsWith("PP") && formattedSSID.endsWith("AP");
  }
}
