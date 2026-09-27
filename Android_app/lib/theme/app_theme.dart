import 'package:flutter/material.dart';

/// Material 3 theme shared by light and dark modes. Colours of individual variables
/// (temperature, pH…) come from [AppColors] so a reading keeps its colour on every tab.
class AppTheme {
  static const Color seed = Color(0xFF0B6E79);

  static ThemeData light() => _build(Brightness.light);
  static ThemeData dark() => _build(Brightness.dark);

  static ThemeData _build(Brightness brightness) {
    final scheme = ColorScheme.fromSeed(seedColor: seed, brightness: brightness);
    return ThemeData(
      useMaterial3: true,
      colorScheme: scheme,
      scaffoldBackgroundColor: scheme.surfaceContainerLowest,
      appBarTheme: AppBarTheme(
        backgroundColor: scheme.surface,
        surfaceTintColor: Colors.transparent,
        scrolledUnderElevation: 1,
      ),
      cardTheme: CardThemeData(
        elevation: 0,
        margin: EdgeInsets.zero,
        color: scheme.surfaceContainerLow,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(16),
          side: BorderSide(color: scheme.outlineVariant.withValues(alpha: 0.6)),
        ),
      ),
      inputDecorationTheme: InputDecorationTheme(
        filled: true,
        fillColor: scheme.surfaceContainerHighest.withValues(alpha: 0.35),
        isDense: true,
        border: OutlineInputBorder(borderRadius: BorderRadius.circular(10)),
        enabledBorder: OutlineInputBorder(
          borderRadius: BorderRadius.circular(10),
          borderSide: BorderSide(color: scheme.outlineVariant),
        ),
      ),
      segmentedButtonTheme: const SegmentedButtonThemeData(
        style: ButtonStyle(visualDensity: VisualDensity.compact),
      ),
      snackBarTheme: const SnackBarThemeData(behavior: SnackBarBehavior.floating),
      navigationBarTheme: NavigationBarThemeData(
        backgroundColor: scheme.surface,
        indicatorColor: scheme.primaryContainer,
      ),
    );
  }
}

class AppColors {
  static const temperature = Color(0xFFE5533D);
  static const ph = Color(0xFF3B6FD8);
  static const oxygen = Color(0xFF12A38F);
  static const pressure = Color(0xFFE59A1C);
  static const agitation = Color(0xFF6B5BD2);
  static const nutrient = Color(0xFF8D6E63);
  static const antifoam = Color(0xFFD9722B);
  static const distance = Color(0xFF5C6BC0);
  static const biomass = Color(0xFF2E9E6B);
  static const flow = Color(0xFF1597B8);
  static const flask = Color(0xFF8E4FC2);
  static const pump = Color(0xFFD35400);
  static const danger = Color(0xFFC62828);
  static const ok = Color(0xFF2E7D32);
  static const warn = Color(0xFFB26A00);
}
