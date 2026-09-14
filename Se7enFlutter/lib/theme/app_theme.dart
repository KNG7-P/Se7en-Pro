import 'package:flutter/material.dart';
import 'package:google_fonts/google_fonts.dart';

import 'app_colors.dart';

class AppTheme {
  static ThemeData dark() => _build(Brightness.dark, AppColors.dark);
  static ThemeData light() => _build(Brightness.light, AppColors.light);

  static ThemeData _build(Brightness brightness, AppColors c) {
    final base = ThemeData(brightness: brightness, useMaterial3: true);

    final textTheme = GoogleFonts.interTextTheme(base.textTheme).apply(
      bodyColor: c.textPrimary,
      displayColor: c.textPrimary,
    );

    final scheme = ColorScheme.fromSeed(
      seedColor: BrandColors.primary,
      brightness: brightness,
      primary: BrandColors.primary,
      secondary: BrandColors.accentCyan,
      surface: c.card,
      error: BrandColors.danger,
    );

    return base.copyWith(
      colorScheme: scheme,
      scaffoldBackgroundColor: c.appBg,
      canvasColor: c.appBg,
      textTheme: textTheme,
      dividerColor: c.border,
      splashFactory: InkSparkle.splashFactory,
      tooltipTheme: TooltipThemeData(
        decoration: BoxDecoration(
          color: c.cardElevated,
          borderRadius: BorderRadius.circular(8),
          border: Border.all(color: c.border),
        ),
        textStyle: TextStyle(color: c.textPrimary, fontSize: 12),
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 6),
        waitDuration: const Duration(milliseconds: 500),
      ),
      scrollbarTheme: ScrollbarThemeData(
        thumbColor: WidgetStatePropertyAll(c.borderHover),
        thickness: const WidgetStatePropertyAll(6),
        radius: const Radius.circular(3),
      ),
      switchTheme: SwitchThemeData(
        thumbColor: WidgetStateProperty.resolveWith((states) {
          if (states.contains(WidgetState.selected)) {
            return Colors.white;
          }
          return c.isDark ? const Color(0xFF94A3B8) : const Color(0xFF64748B);
        }),
        trackColor: WidgetStateProperty.resolveWith((states) {
          if (states.contains(WidgetState.selected)) {
            return BrandColors.accentCyan;
          }
          return c.isDark ? const Color(0xFF1E293B) : const Color(0xFFE2E8F0);
        }),
        trackOutlineColor: WidgetStateProperty.resolveWith((states) {
          if (states.contains(WidgetState.selected)) {
            return Colors.transparent;
          }
          return c.isDark ? const Color(0xFF475569) : const Color(0xFFCBD5E1);
        }),
        trackOutlineWidth: const WidgetStatePropertyAll(1.0),
        overlayColor: WidgetStatePropertyAll(BrandColors.accentCyan.withValues(alpha: 0.15)),
      ),
      extensions: [c],
    );
  }

  static TextStyle mono(Color color, {double size = 12, FontWeight? weight}) =>
      GoogleFonts.jetBrainsMono(
        color: color,
        fontSize: size,
        fontWeight: weight ?? FontWeight.w500,
      );
}
