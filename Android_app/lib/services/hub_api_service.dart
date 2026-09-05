import 'dart:async';
import 'dart:convert';
import 'package:http/http.dart' as http;
import '../constants/api_constants.dart';

class HubApiService {
  String targetIp;
  final http.Client _client;

  HubApiService({String? ip, http.Client? client})
      : targetIp = ip ?? ApiConstants.defaultIp,
        _client = client ?? http.Client();

  Uri _buildUri(String path) => Uri.parse("http://$targetIp$path");

  /// Pings the Hub to verify network reachability and measure round-trip latency.
  Future<({bool success, int rttMs, String error})> ping() async {
    final stopwatch = Stopwatch()..start();
    try {
      final response = await _client
          .get(_buildUri(ApiConstants.pingEndpoint))
          .timeout(ApiConstants.pingTimeout);
      stopwatch.stop();

      if (response.statusCode == 200 && response.body.trim().contains("pong")) {
        return (success: true, rttMs: stopwatch.elapsedMilliseconds, error: "");
      }
      return (
        success: false,
        rttMs: stopwatch.elapsedMilliseconds,
        error: "HTTP ${response.statusCode}: ${response.body}"
      );
    } catch (e) {
      stopwatch.stop();
      return (success: false, rttMs: stopwatch.elapsedMilliseconds, error: e.toString());
    }
  }

  /// Fetches sensor and servo telemetry with ETag caching.
  /// If [cachedEtag] matches server sample, returns [notModified] = true.
  Future<({bool success, bool notModified, Map<String, dynamic>? data, String? etag, String error})>
      fetchTelemetry({String? cachedEtag}) async {
    try {
      final headers = <String, String>{};
      if (cachedEtag != null && cachedEtag.isNotEmpty) {
        headers["If-None-Match"] = cachedEtag;
      }

      final response = await _client
          .get(_buildUri(ApiConstants.readDataEndpoint), headers: headers)
          .timeout(ApiConstants.readTimeout);

      if (response.statusCode == 304) {
        return (
          success: true,
          notModified: true,
          data: null,
          etag: cachedEtag,
          error: ""
        );
      }

      if (response.statusCode == 200) {
        final body = response.body.trim();
        final serverEtag = response.headers['etag'];

        // If firmware buffered an ACK (e.g. "OK" or "Queued") instead of JSON
        if (!body.startsWith('{') || !body.endsWith('}')) {
          return (
            success: true,
            notModified: true,
            data: null,
            etag: cachedEtag,
            error: ""
          );
        }

        final decoded = jsonDecode(body) as Map<String, dynamic>;
        return (
          success: true,
          notModified: false,
          data: decoded,
          etag: serverEtag ?? cachedEtag,
          error: ""
        );
      }

      return (
        success: false,
        notModified: false,
        data: null,
        etag: null,
        error: "HTTP ${response.statusCode}"
      );
    } catch (e) {
      return (
        success: false,
        notModified: false,
        data: null,
        etag: null,
        error: e.toString()
      );
    }
  }

  /// Sends a command JSON object to /command.
  Future<({bool success, int statusCode, String response})> sendCommand(
      Map<String, dynamic> command) async {
    try {
      final jsonPayload = jsonEncode(command);
      final response = await _client
          .post(
            _buildUri(ApiConstants.commandEndpoint),
            headers: {'Content-Type': 'text/plain'},
            body: jsonPayload,
          )
          .timeout(ApiConstants.commandTimeout);

      final success = response.statusCode == 200;
      return (
        success: success,
        statusCode: response.statusCode,
        response: response.body.trim()
      );
    } catch (e) {
      return (
        success: false,
        statusCode: 0,
        response: e.toString()
      );
    }
  }

  void dispose() {
    _client.close();
  }
}
