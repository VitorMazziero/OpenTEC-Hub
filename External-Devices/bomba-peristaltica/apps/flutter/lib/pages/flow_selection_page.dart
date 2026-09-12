import 'package:flutter/material.dart';
import 'package:shared_preferences/shared_preferences.dart';
import '../services/pump_connection_service.dart';
import 'flow_modes/constant_mode_page.dart';
import 'flow_modes/linear_mode_page.dart';
import 'flow_modes/exponential_mode_page.dart';
import 'flow_modes/polynomial_mode_page.dart';
import 'flow_modes/piecewise_mode_page.dart';

class FlowSelectionPage extends StatefulWidget {
  final PumpConnectionService connectionService;

  const FlowSelectionPage({Key? key, required this.connectionService}) : super(key: key);

  @override
  _FlowSelectionPageState createState() => _FlowSelectionPageState();
}

class _FlowSelectionPageState extends State<FlowSelectionPage> {
  int _currentIndex = 0;
  late PageController _pageController;
  late List<Widget> _pages;

  @override
  void initState() {
    super.initState();
    _pageController = PageController(initialPage: _currentIndex);
    _pages = [
      ConstantModePage(connectionService: widget.connectionService),
      LinearModePage(connectionService: widget.connectionService),
      ExponentialModePage(connectionService: widget.connectionService),
      PolynomialModePage(connectionService: widget.connectionService),
      PiecewiseModePage(connectionService: widget.connectionService),
    ];
    _loadLastPageIndex();
  }

  Future<void> _loadLastPageIndex() async {
    SharedPreferences prefs = await SharedPreferences.getInstance();
    int savedIndex = prefs.getInt('lastPageIndex') ?? 0;
    if (savedIndex >= _pages.length) savedIndex = 0;
    if (mounted) {
      setState(() {
        _currentIndex = savedIndex;
      });
      _pageController.jumpToPage(savedIndex);
    }
  }

  Future<void> _saveLastPageIndex(int index) async {
    SharedPreferences prefs = await SharedPreferences.getInstance();
    await prefs.setInt('lastPageIndex', index);
  }

  @override
  Widget build(BuildContext context) {
    return Column(
      children: [
        FlowSelectionNavBar(
          currentIndex: _currentIndex,
          onIndexChanged: (index) {
            _pageController.animateToPage(
              index,
              duration: const Duration(milliseconds: 200),
              curve: Curves.easeInOut,
            );
            setState(() {
              _currentIndex = index;
            });
            _saveLastPageIndex(index);
          },
        ),
        Expanded(
          child: PageView(
            controller: _pageController,
            onPageChanged: (index) {
              setState(() {
                _currentIndex = index;
              });
              _saveLastPageIndex(index);
            },
            children: _pages,
          ),
        ),
      ],
    );
  }
}

class FlowSelectionNavBar extends StatelessWidget {
  final int currentIndex;
  final ValueChanged<int> onIndexChanged;

  const FlowSelectionNavBar({
    Key? key,
    required this.currentIndex,
    required this.onIndexChanged,
  }) : super(key: key);

  final List<String> _labels = const [
    'Constant',
    'Linear',
    'Exponential',
    'Polynomial',
    'Piecewise'
  ];

  @override
  Widget build(BuildContext context) {
    return Container(
      color: const Color(0xFF161A22), // Matches modern dark theme background
      child: SingleChildScrollView(
        scrollDirection: Axis.horizontal,
        child: Row(
          mainAxisAlignment: MainAxisAlignment.spaceAround,
          children: List.generate(_labels.length, (index) {
            return _buildNavBarItem(_labels[index], index);
          }),
        ),
      ),
    );
  }

  Widget _buildNavBarItem(String label, int index) {
    final isSelected = currentIndex == index;
    return GestureDetector(
      onTap: () => onIndexChanged(index),
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 20.0, vertical: 16.0),
        decoration: BoxDecoration(
          border: Border(
            bottom: BorderSide(
              color: isSelected ? Colors.greenAccent : Colors.transparent,
              width: 3.0,
            ),
          ),
        ),
        child: Text(
          label,
          style: TextStyle(
            fontSize: 16,
            fontWeight: isSelected ? FontWeight.bold : FontWeight.normal,
            color: isSelected ? Colors.white : Colors.white54,
          ),
        ),
      ),
    );
  }
}
