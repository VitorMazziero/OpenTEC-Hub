import 'package:flutter/material.dart';

/// Mesma identidade visual dos outros aplicativos locais (frasco-agitador,
/// bomba-peristaltica): tema escuro, cartões arredondados, acento ciano.
class AppTheme {
  static const Color primaryCyan = Color(0xFF00B4D8);
  static const Color primaryDarkCyan = Color(0xFF0077B6);
  static const Color accentAmber = Color(0xFFFFB703);
  static const Color stopRed = Color(0xFFE63946);
  static const Color okGreen = Color(0xFF2EC4B6);
  static const Color darkBg = Color(0xFF101318);
  static const Color darkCard = Color(0xFF1A1F29);
  static const Color darkCardElevated = Color(0xFF232A37);

  static ThemeData get darkTheme {
    return ThemeData(
      useMaterial3: true,
      brightness: Brightness.dark,
      scaffoldBackgroundColor: darkBg,
      colorScheme: const ColorScheme.dark(
        primary: primaryCyan,
        secondary: accentAmber,
        error: stopRed,
        surface: darkCard,
        onPrimary: Colors.black,
        onSecondary: Colors.black,
        onSurface: Colors.white,
      ),
      cardTheme: CardThemeData(
        color: darkCard,
        elevation: 0,
        shape: RoundedRectangleBorder(
          borderRadius: BorderRadius.circular(16),
          side: BorderSide(
            color: Colors.white.withValues(alpha: 0.08),
            width: 1,
          ),
        ),
      ),
      appBarTheme: const AppBarTheme(
        backgroundColor: darkBg,
        foregroundColor: Colors.white,
        elevation: 0,
        centerTitle: false,
      ),
      switchTheme: SwitchThemeData(
        thumbColor: WidgetStateProperty.resolveWith<Color>((states) {
          if (states.contains(WidgetState.selected)) return primaryCyan;
          return Colors.grey.shade400;
        }),
        trackColor: WidgetStateProperty.resolveWith<Color>((states) {
          if (states.contains(WidgetState.selected)) {
            return primaryCyan.withValues(alpha: 0.35);
          }
          return Colors.white.withValues(alpha: 0.15);
        }),
      ),
      elevatedButtonTheme: ElevatedButtonThemeData(
        style: ElevatedButton.styleFrom(
          shape: RoundedRectangleBorder(
            borderRadius: BorderRadius.circular(12),
          ),
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 14),
        ),
      ),
      inputDecorationTheme: InputDecorationTheme(
        border: OutlineInputBorder(borderRadius: BorderRadius.circular(12)),
        isDense: true,
      ),
      textTheme: const TextTheme(
        headlineMedium:
            TextStyle(color: Colors.white, fontWeight: FontWeight.bold),
        titleLarge: TextStyle(color: Colors.white, fontWeight: FontWeight.w600),
        bodyLarge: TextStyle(color: Colors.white),
        bodyMedium: TextStyle(color: Color(0xFFB0B7C3)),
      ),
    );
  }
}
