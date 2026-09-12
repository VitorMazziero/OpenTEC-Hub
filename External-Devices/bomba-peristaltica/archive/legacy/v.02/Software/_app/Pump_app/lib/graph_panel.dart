import 'package:flutter/material.dart';
// You might need to add a package for graph plotting, like fl_chart

class GraphPanel extends StatelessWidget {
  const GraphPanel({super.key});

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(10),
      child: Column(
        children: <Widget>[
          const Text('Graph 1'),
          // Placeholder for Graph 1
          Container(
            height: 150,
            color: Colors.grey[300],
          ),
          const Text('Graph 2'),
          // Placeholder for Graph 2
          Container(
            height: 150,
            color: Colors.grey[300],
          ),
        ],
      ),
    );
  }
}
