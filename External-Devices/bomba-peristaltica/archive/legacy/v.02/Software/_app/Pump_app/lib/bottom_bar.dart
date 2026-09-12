import 'package:fl_chart/fl_chart.dart';
import 'package:flutter/material.dart';
import 'dart:math';
import 'dart:async';
import 'http_controller.dart';

class BottomBar extends StatefulWidget {
  final bool isDirectFlow;
  final int flowType;
  final TextEditingController initialTimeController;
  final TextEditingController finalTimeController;
  final TextEditingController initialVolumeController;
  final TextEditingController finalVolumeController;
  final TextEditingController initialFlowController;
  final TextEditingController cx0Controller;
  final TextEditingController seController;
  final TextEditingController yxsController;
  final TextEditingController pumpSlope;
  final TextEditingController pumpIntercept;
  final TextEditingController directFlowController;

  const BottomBar({
    super.key,
    this.isDirectFlow = false,
    required this.flowType,
    required this.initialTimeController,
    required this.finalTimeController,
    required this.initialVolumeController,
    required this.finalVolumeController,
    required this.initialFlowController,
    required this.cx0Controller,
    required this.seController,
    required this.yxsController,
    required this.pumpSlope,
    required this.pumpIntercept,
    required this.directFlowController
  });


  @override
  State<BottomBar> createState() => _BottomBarState();
}

class _BottomBarState extends State<BottomBar> {
  bool isSwitchEnabled = true;
  bool isMlPerMin = true;

  void setSwitchState(bool state) {
    setState(() {
      isSwitchEnabled = state;
    });
  }

  void setIsMlPerMin(bool value) { // Add this
    setState(() {
      isMlPerMin = value;
    });
  }

  @override
  Widget build(BuildContext context) {
    return Container(
      height: 100,
      color: Theme.of(context).secondaryHeaderColor,
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: <Widget>[
          TextSection(isSwitchEnabled: isSwitchEnabled, setIsMlPerMin: setIsMlPerMin ),
          ButtonSection(
            isDirectFlow: widget.isDirectFlow,
            setSwitchState: setSwitchState,
            isMlPerMin: isMlPerMin,
            flowType: widget.flowType,
            initialTimeController: widget.initialTimeController,
            finalTimeController: widget.finalTimeController,
            initialVolumeController: widget.initialVolumeController,
            initialFlowController: widget.initialFlowController,
            finalVolumeController: widget.finalVolumeController,
            directFlowController: widget.directFlowController,
            cx0Controller: widget.cx0Controller,
            seController: widget.seController,
            yxsController: widget.yxsController,
            pumpSlope: widget.pumpSlope,
            pumpIntercept: widget.pumpIntercept),
        ],
      ),
    );
  }
}

class TextSection extends StatefulWidget {
  final Function(bool) setIsMlPerMin;
  final bool isSwitchEnabled;
  const TextSection({super.key, required this.isSwitchEnabled, required this.setIsMlPerMin});

  @override
  TextSectionState createState() => TextSectionState();
}

class TextSectionState extends State<TextSection> {
  final HttpController httpController = HttpController();
  bool isMlPerMin = true;
  bool isSwitchEnabled = true;
  double flowRate = 0;
  double time = 0;
  Timer? _timer; // Timer instance

  @override
  void initState() {
    super.initState();
    fetchData(); // Fetch initial data
    _startPeriodicFetch(); // Start periodic data fetching
  }

  void _startPeriodicFetch() {
    _timer = Timer.periodic(const Duration(seconds: 5), (Timer t) => fetchData());
  }

  @override
  void dispose() {
    _timer?.cancel(); // Cancel the timer when the widget is disposed
    super.dispose();
  }

  void fetchData() async {
    Map<String, double> parsedData = await httpController.readAndParseData();
    if (mounted) { // Check if the widget is still in the widget tree
      setState(() {
        flowRate = parsedData['FlowRate'] ?? 0;
        time = parsedData['Time'] ?? 0;
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    return Row(
      mainAxisAlignment: MainAxisAlignment.spaceEvenly,
      children: <Widget>[
        const SizedBox(width: 20),
        Expanded(
          child: Text(
              isMlPerMin
                  ? 'Flow Rate: \n${flowRate.toStringAsFixed(2)} mL/s'
                  : 'Flow Rate: \n${flowRate.toStringAsFixed(2)} stp/s',
              textAlign: TextAlign.center
          ),
        ),
        Expanded(
          child: Text('Time:\n${time.toStringAsFixed(0)} s', textAlign: TextAlign.center),
        ),
        const SizedBox(width: 20),
        Switch(
          value: isMlPerMin,
          onChanged: widget.isSwitchEnabled ? (bool value) {
            setState(() {
              isMlPerMin = value;
            });
            widget.setIsMlPerMin(value); // Use the callback to set the value
          } : null, // Disable the switch when isSwitchEnabled is false
        ),
        const Text('mL/min'),
        const SizedBox(width: 20),
      ],
    );
  }
}

class ButtonSection extends StatelessWidget {
  final Function(bool) setSwitchState;
  final bool isMlPerMin;
  final bool isDirectFlow;
  final HttpController httpController = HttpController();
  final int flowType;
  final TextEditingController initialTimeController;
  final TextEditingController finalTimeController;
  final TextEditingController initialVolumeController;
  final TextEditingController finalVolumeController;
  final TextEditingController initialFlowController;
  final TextEditingController directFlowController;
  final TextEditingController cx0Controller;
  final TextEditingController seController;
  final TextEditingController yxsController;
  final TextEditingController pumpSlope;
  final TextEditingController pumpIntercept;

  ButtonSection({
    super.key,
    this.isDirectFlow = false,
    required this.setSwitchState,
    required this.isMlPerMin,
    required this.flowType,
    required this.initialTimeController,
    required this.finalTimeController,
    required this.initialVolumeController,
    required this.finalVolumeController,
    required this.initialFlowController,
    required this.directFlowController,
    required this.cx0Controller,
    required this.seController,
    required this.yxsController,
    required this.pumpSlope,
    required this.pumpIntercept
  });

  double objective(double w, double deltaT, double vi, double vf, double cxo, double se, double yxs) {
    double alpha = w;
    double qe = (cxo * vi * alpha) / (yxs * se);
    double v = vi + qe / alpha * (exp(alpha * deltaT) - 1);
    return v - vf; // Return the difference between final volume and vf
  }

  double solveForAlpha(double ti, double tf, double vi, double vf, double cxo, double se, double yxs) {
    double initialGuess;
    if (tf < 1) {
      initialGuess = 0.1;
    } else {
      initialGuess = 10;
    }
    double alpha = initialGuess;
    double tolerance = 0.0000001;
    double deltaT = tf - ti;
    int maxIterations = 1000000; // Set a maximum number of iterations to prevent infinite loops
    int iterationCount = 0;

    double error = objective(alpha, deltaT, vi, vf, cxo, se, yxs);

    while (error.abs() > tolerance && iterationCount < maxIterations) {
      double newAlpha;
      if (tf > 1) {
        newAlpha = alpha - error / 1000; // Simple adjustment
      }
      else{
        newAlpha = alpha - error / 10; // Simple adjustment
      }

      // Prevent alpha from becoming negative
      if (newAlpha <= 0) {
        // Set to a small positive value, ensuring alpha remains valid
        alpha = 0.000001;
      } else {
        alpha = newAlpha;
      }

      error = objective(alpha, deltaT, vi, vf, cxo, se, yxs);
      iterationCount++;
    }

    if (iterationCount == maxIterations) {
      print("Warning: Reached maximum iterations without converging to the desired tolerance.");
    }

    return alpha;
  }


  void _startPump() {
    double pumpSlopevalue = double.tryParse(pumpSlope.text) ?? 0;
    double pumpInterceptvalue = double.tryParse(pumpIntercept.text) ?? 0;
    double ti = (double.tryParse(initialTimeController.text) ?? 0) * 60;
    double tf = (double.tryParse(finalTimeController.text) ?? 0) * 60;
    double vi = (double.tryParse(initialVolumeController.text) ?? 0) * 1000;
    double vf = (double.tryParse(finalVolumeController.text) ?? 0) * 1000;
    double flow = (double.tryParse(directFlowController.text) ?? 0);
    int rawValue = isMlPerMin ? 0 : 1; // Use isMlPerMin here
    double deltaT = tf - ti;

    // Declare the variables outside the if blocks
    double b0 = 0;
    double b1 = 0;
    double b2 = 0;

    if (flowType == 1) {
      if (isDirectFlow){
        b0 = flow;
      } else{
        b0 = (vf - vi) / (deltaT);
      }
      b0 = double.parse(b0.toStringAsFixed(6));
    } else if (flowType == 2) {
      b0 = double.tryParse(initialFlowController.text) ?? 0;
      b1 = (vf - vi - (b0 * deltaT)) / (pow(deltaT, 2) / 2);
      b0 = double.parse(b0.toStringAsFixed(6));
      b1 = double.parse(b1.toStringAsFixed(6));
    } else if (flowType == 3) {
      double cx0 = double.tryParse(cx0Controller.text) ?? 0;
      double se = double.tryParse(seController.text) ?? 0;
      double yxs = double.tryParse(yxsController.text) ?? 0;
      double alpha_h = solveForAlpha(
          ti/60,
          tf/60,
          vi/1000,
          vf/1000,
          cx0,
          se,
          yxs);
      b0 = cx0 * vi * alpha_h/60 / (yxs * se);
      b1 = alpha_h/60;
      b0 = double.parse(b0.toStringAsFixed(6));
      b1 = double.parse(b1.toStringAsFixed(6));
    }

    String message = "{$flowType,$ti,$tf,$b0,$b1,$b2,$pumpSlopevalue,$pumpInterceptvalue,$rawValue,}";
    httpController.sendCommand(message);
    setSwitchState(false);
  }

  void _stopPump() {
    double pumpSlopevalue = double.tryParse(pumpSlope.text) ?? 0;
    double pumpInterceptvalue = double.tryParse(pumpIntercept.text) ?? 0;
    double ti = (double.tryParse(initialTimeController.text) ?? 0) * 60;
    double tf = (double.tryParse(finalTimeController.text) ?? 0) * 60;
    double vi = (double.tryParse(initialVolumeController.text) ?? 0) * 1000;
    double vf = (double.tryParse(finalVolumeController.text) ?? 0) * 1000;
    double b0 = (vf - vi) / (tf - ti);
    double b1 = 0;
    double b2 = 0;
    int rawValue = isMlPerMin ? 0 : 1; // Use isMlPerMin here
    String message = "{0,$ti,$tf,$b0,$b1,$b2,$pumpSlopevalue,$pumpInterceptvalue,$rawValue,}";
    httpController.sendCommand(message);
    setSwitchState(true);
  }

  Widget _buildGraphs() {
    double ti = (double.tryParse(initialTimeController.text) ?? 0) * 60;
    double tf = (double.tryParse(finalTimeController.text) ?? 0) * 60;
    double vi = (double.tryParse(initialVolumeController.text) ?? 0) * 1000;
    double vf = (double.tryParse(finalVolumeController.text) ?? 0) * 1000;
    double flow = (double.tryParse(directFlowController.text) ?? 0);
    double ti_h = ti / 60;
    double tf_h = tf / 60;
    double vi_h = vi / 1000;
    double vf_h = vf / 1000;
    // Initialize volumeData and flowData as empty lists.
    List<FlSpot> volumeData = [];
    List<FlSpot> flowData = [];

    if (flowType == 1) {
      double alpha = 0;
      double alpha_h = 0;
      if (isDirectFlow) {
        alpha = flow;
        alpha_h = flow/60000;
      }else{
        alpha = (vf - vi) / (tf - ti);
        alpha_h = (vf_h - vi_h) / (tf_h - ti_h);
      }
      volumeData = List.generate(
        (tf_h * 250).toInt(),
            (index) {
          final t = index / 250;
          return FlSpot(t, t < ti_h ? vi_h : (vi_h + alpha_h * (t - ti_h)));
        },
      );
      flowData = List.generate(
        (tf_h * 250).toInt(),
            (index) {
          final t = index / 250;
          return FlSpot(t, t < ti_h ? 0 : alpha);
        },
      );
    } else if (flowType == 2) {
      double qe = double.tryParse(initialFlowController.text) ?? 0;
      double qe_h = qe / 60000;
      double deltaT = tf - ti;
      double deltaT_h = tf_h - ti_h;
      double alpha = (vf - vi - (qe * deltaT)) / (pow(deltaT, 2) / 2);
      double alpha_h = (vf_h - vi_h - (qe_h * deltaT_h)) / (pow(deltaT_h, 2) / 2);
      volumeData = List.generate(
        (tf_h * 250).toInt(),
            (index) {
          final t = index / 250;
          return FlSpot(t, t < ti_h ? vi_h : vi_h + qe_h * (t - ti_h) +
              alpha_h * pow((t - ti_h), 2) / 2);
        },
      );
      flowData = List.generate(
        (tf_h * 100).toInt(),
            (index) {
          final t = index / 100;
          return FlSpot(t, t < ti_h ? 0 : qe + alpha * (t*60 - ti));
        },
      );
    } else if (flowType == 3) {
      double cx0 = double.tryParse(cx0Controller.text) ?? 0;
      double se = double.tryParse(seController.text) ?? 0;
      double yxs = double.tryParse(yxsController.text) ?? 0;
      double alpha_h = solveForAlpha(ti_h, tf_h, vi_h, vf_h, cx0, se, yxs);
      print("alpha");
      print(alpha_h);
      double alpha = alpha_h/60;
      double qe = cx0 * vi * alpha / (yxs * se);
      double qe_h = cx0*1000 * vi_h * alpha_h / (yxs * se*1000);
      double listSize;
      if (tf_h < 1){
        listSize = tf_h * 750;
      }
      else{
        listSize = tf_h * 100;
      }
      volumeData = List.generate(
        (listSize).toInt(),
            (index) {
          final t = index / (listSize/tf_h);
          return FlSpot(
              t, t < ti_h ? vi_h : vi_h + qe_h / alpha_h * (exp(alpha_h * (t - ti_h)) - 1));
        },
      );
      flowData = List.generate(
        (listSize).toInt(),
            (index) {
          final t = index / (listSize/tf_h);
          return FlSpot(t, t < ti_h ? 0 : qe * exp(alpha * (t*60 - ti)));
        },
      );
    }

    return SingleChildScrollView(
      child: Padding(
        padding: const EdgeInsets.all(8.0), // Add padding to all sides
        child: Column(
          children: [
            if (volumeData.isNotEmpty)
              Padding(
                padding: const EdgeInsets.only(top: 16, right: 16, left: 16, bottom: 8), // Adjust the padding as needed
                child: _buildGraphWithAxisTitles(volumeData, 'Volume (L)'),
              ),
            const SizedBox(height: 20),
            if (flowData.isNotEmpty)
              Padding(
                padding: const EdgeInsets.only(top: 8, right: 16, left: 16, bottom: 16), // Adjust the padding as needed
                child: _buildGraphWithAxisTitles(flowData, 'Flow Rate (mL/min)'),
              ),
          ],
        ),
      ),
    );
  }

  Widget _buildGraphWithAxisTitles(List<FlSpot> data, String yAxisLabel) {
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Padding(
          padding: const EdgeInsets.all(16.0),
          child: Center(child: Text("$yAxisLabel x Time (h)", style: const TextStyle(fontSize: 16, fontWeight: FontWeight.bold))),
        ),
        const SizedBox(height: 8),
        // Directly use SizedBox without Expanded for a specific size
        SizedBox(
          width: double.infinity, // Use the full width available
          height: 250, // Specify a fixed height for the chart
          child: SingleChildScrollView(
            scrollDirection: Axis.horizontal,
            child: SizedBox(
              width: 325, // Specify the width of the inner content if it's larger than the screen
              child: _buildLineChart(data, yAxisLabel),
            ),
          ),
        ),
      ],
    );
  }

  Widget _buildLineChart(List<FlSpot> data, String yAxisLabel) {
    // Calculate the min and max for Y values
    final minYData = data.map((e) => e.y).reduce(min);
    final maxYData = data.map((e) => e.y).reduce(max);
    final adjustedMinY = minYData > 0 ? minYData * 0.85 : minYData * 1.15;
    final adjustedMaxY = maxYData > 0 ? maxYData * 1.25 : maxYData * 0.75;

// Calculate the min and max for X values
    final minXData = data.map((e) => e.x).reduce(min);
    final maxXData = data.map((e) => e.x).reduce(max);
    final adjustedMinX = minXData > 0 ? minXData * 0.85 : minXData * 1.15;
    final adjustedMaxX = maxXData > 0 ? maxXData * 1.25 : maxXData * 0.75;


    return SizedBox(
      height: 250,
      child: LineChart(
        LineChartData(
          lineTouchData: LineTouchData(
            touchTooltipData: LineTouchTooltipData(
              tooltipBgColor: Colors.blueAccent,
              getTooltipItems: (List<LineBarSpot> touchedBarSpots) {
                return touchedBarSpots.map((barSpot) {
                  return LineTooltipItem(
                    barSpot.y.toStringAsFixed(3), // Format to 2 decimal places
                    const TextStyle(color: Colors.white),
                  );
                }).toList();
              },
            ),
          ),
          lineBarsData: [
            LineChartBarData(
              spots: data,
              isCurved: false, // Make the lines curved
              color: Colors.blueAccent,
              barWidth: 4, // Line thickness
              isStrokeCapRound: true,
              dotData: const FlDotData(show: false), // You might want to show the dots for tooltips to work
              belowBarData: BarAreaData(
                show: true,
                color: Colors.blueAccent.withOpacity(0.5),
              ),
            ),
          ],
          gridData: FlGridData(
            show: true,
            drawVerticalLine: true,
            horizontalInterval: (adjustedMaxY - adjustedMinY) != 0 ? (adjustedMaxY - adjustedMinY) / 5 : null,
            verticalInterval: (adjustedMaxX - adjustedMinX) != 0 ? (adjustedMaxX - adjustedMinX) / 5 : null,
          ),
          borderData: FlBorderData(
            show: true,
            border: Border.all(color: Colors.grey, width: 1),
          ),
          minY: adjustedMinY,
          minX: adjustedMinX,
          maxY: adjustedMaxY,
          maxX: adjustedMaxX,
          titlesData: FlTitlesData(
            leftTitles: AxisTitles(
              sideTitles: SideTitles(
                showTitles: true,
                reservedSize: 40,
                getTitlesWidget: (double value, TitleMeta meta) {
                  // Check if the value is the first or last one and hide it
                  if (value == adjustedMinY || value == adjustedMaxY) {
                    return const SizedBox.shrink();
                  }
                  return Padding(
                    padding: const EdgeInsets.only(right: 6.0),
                    child: Text(
                      value.toStringAsFixed(1),
                      style: const TextStyle(
                        color: Colors.blueGrey,
                        fontWeight: FontWeight.bold,
                        fontSize: 14,
                      ),
                      textAlign: TextAlign.right,
                    ),
                  );
                },
              ),
            ),
            bottomTitles: AxisTitles(
              sideTitles: SideTitles(
                showTitles: true,
                reservedSize: 30,
                //interval: (adjustedMaxY - adjustedMinY) / 5,
                getTitlesWidget: (double value, TitleMeta meta) {
                  // Check if the value is the first or last one and hide it
                  if (value == adjustedMinX || value == adjustedMaxX) {
                    return const SizedBox.shrink();
                  }
                  return Padding(
                    padding: const EdgeInsets.only(top: 10.0),
                    child: Text(
                      value.toStringAsFixed(1),
                      style: const TextStyle(
                        color: Colors.blueGrey,
                        fontWeight: FontWeight.bold,
                        fontSize: 14,
                      ),
                      textAlign: TextAlign.center,
                    ),
                  );
                },
              ),
            ),
            // Disable top and right titles
            topTitles: const AxisTitles(
              sideTitles: SideTitles(showTitles: false),
            ),
            rightTitles: const AxisTitles(
              sideTitles: SideTitles(showTitles: false),
            ),
          ),
          // ... [Rest of your configurations] ...
        ),
      ),
    );
  }

  @override
  Widget build(BuildContext context) {
    return ListView(
      shrinkWrap: true,
      children: <Widget>[
        Row(
          mainAxisAlignment: MainAxisAlignment.spaceEvenly,
          children: <Widget>[
            const SizedBox(width: 20),
            Expanded(
              child: ElevatedButton(
                onPressed: _startPump,
                child: const Text('Start'),
              ),
            ),
            const SizedBox(width: 20),
            Expanded(
              child: ElevatedButton(
                onPressed: _stopPump,
                child: const Text('Stop'),
              ),
            ),
            const SizedBox(width: 20),
            Expanded(
              child: ElevatedButton(
                child: const Text('Simulate'),
                onPressed: () {
                  showModalBottomSheet(
                    context: context,
                    builder: (context) => _buildGraphs(),
                  );
                },
              ),
            ),
            const SizedBox(width: 20),
          ],
        ),
      ],
    );
  }
}
