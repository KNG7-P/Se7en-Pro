import 'package:flutter/material.dart';

class BrandColors {

  static const Color primary = Color(0xFF7C3AED);
  static const Color primaryDark = Color(0xFF5B21B6);
  static const Color accentCyan = Color(0xFF00D4FF);
  static const Color accentPurple = Color(0xFFA78BFA);
  static const Color success = Color(0xFF10B981);
  static const Color successDeep = Color(0xFF059669);
  static const Color danger = Color(0xFFEF4444);
  static const Color warning = Color(0xFFF59E0B);
  static const Color info = Color(0xFF60A5FA);
  static const Color telegram = Color(0xFF229ED9);

  static const Color violet = Color(0xFF8B5CF6);
  static const Color indigo = Color(0xFF6366F1);
  static const Color aqua = Color(0xFF22D3EE);
  static const Color teal = Color(0xFF2DD4BF);
  static const Color emerald = Color(0xFF34D399);
  static const Color pink = Color(0xFFEC4899);
  static const Color amber = Color(0xFFFB923C);

  static const List<Color> accentGradient = [accentCyan, accentPurple];
  static const List<Color> connectedGradient = [successDeep, success];
  static const List<Color> heroGradient = [primaryDark, primary, accentCyan];
  static const List<Color> idleOrb = [indigo, violet];
  static const List<Color> connectingOrb = [aqua, violet];
  static const List<Color> connectedOrb = [emerald, teal];
  static const List<Color> errorOrb = [pink, danger];
}

@immutable
class AppColors extends ThemeExtension<AppColors> {
  const AppColors({
    required this.appBg,
    required this.appBgDeep,
    required this.sidebarBg,
    required this.titleBarBg,
    required this.card,
    required this.cardElevated,
    required this.input,
    required this.border,
    required this.borderHover,
    required this.subtleBg,
    required this.hoverBg,
    required this.selectedBg,
    required this.glass,
    required this.glassStrong,
    required this.glassBorder,
    required this.scrim,
    required this.textPrimary,
    required this.textSecondary,
    required this.textMuted,
  });

  final Color appBg;
  final Color appBgDeep;
  final Color sidebarBg;
  final Color titleBarBg;
  final Color card;
  final Color cardElevated;
  final Color input;
  final Color border;
  final Color borderHover;
  final Color subtleBg;
  final Color hoverBg;
  final Color selectedBg;

  final Color glass;

  final Color glassStrong;

  final Color glassBorder;

  final Color scrim;

  final Color textPrimary;
  final Color textSecondary;
  final Color textMuted;

  static const dark = AppColors(
    appBg: Color(0xFF080A12),
    appBgDeep: Color(0xFF04050A),
    sidebarBg: Color(0xFF0E1019),
    titleBarBg: Color(0xFF0B0D15),
    card: Color(0xFF14161F),
    cardElevated: Color(0xFF1B1D28),
    input: Color(0xFF0C0E16),
    border: Color(0xFF242634),
    borderHover: Color(0xFF363A4E),
    subtleBg: Color(0xFF10121B),
    hoverBg: Color(0xFF1A1D28),
    selectedBg: Color(0xFF20233A),
    glass: Color(0x14FFFFFF),
    glassStrong: Color(0x1FFFFFFF),
    glassBorder: Color(0x24FFFFFF),
    scrim: Color(0xB305060C),
    textPrimary: Color(0xFFF3F4FB),
    textSecondary: Color(0xFF9EA2B8),
    textMuted: Color(0xFF666B82),
  );

  static const light = AppColors(
    appBg: Color(0xFFEEF1F9),
    appBgDeep: Color(0xFFE3E8F4),
    sidebarBg: Color(0xFFFFFFFF),
    titleBarBg: Color(0xFFF6F8FD),
    card: Color(0xFFFFFFFF),
    cardElevated: Color(0xFFF7F9FE),
    input: Color(0xFFEFF2F9),
    border: Color(0xFFE2E7F1),
    borderHover: Color(0xFFCCD4E4),
    subtleBg: Color(0xFFEDF1F8),
    hoverBg: Color(0xFFE9EFFA),
    selectedBg: Color(0xFFE5ECFB),
    glass: Color(0xB8FFFFFF),
    glassStrong: Color(0xD6FFFFFF),
    glassBorder: Color(0x14101828),
    scrim: Color(0x552A3350),
    textPrimary: Color(0xFF161A28),
    textSecondary: Color(0xFF565D72),
    textMuted: Color(0xFF8A92A6),
  );

  bool get isDark => appBg.computeLuminance() < 0.2;

  @override
  AppColors copyWith({
    Color? appBg,
    Color? appBgDeep,
    Color? sidebarBg,
    Color? titleBarBg,
    Color? card,
    Color? cardElevated,
    Color? input,
    Color? border,
    Color? borderHover,
    Color? subtleBg,
    Color? hoverBg,
    Color? selectedBg,
    Color? glass,
    Color? glassStrong,
    Color? glassBorder,
    Color? scrim,
    Color? textPrimary,
    Color? textSecondary,
    Color? textMuted,
  }) =>
      AppColors(
        appBg: appBg ?? this.appBg,
        appBgDeep: appBgDeep ?? this.appBgDeep,
        sidebarBg: sidebarBg ?? this.sidebarBg,
        titleBarBg: titleBarBg ?? this.titleBarBg,
        card: card ?? this.card,
        cardElevated: cardElevated ?? this.cardElevated,
        input: input ?? this.input,
        border: border ?? this.border,
        borderHover: borderHover ?? this.borderHover,
        subtleBg: subtleBg ?? this.subtleBg,
        hoverBg: hoverBg ?? this.hoverBg,
        selectedBg: selectedBg ?? this.selectedBg,
        glass: glass ?? this.glass,
        glassStrong: glassStrong ?? this.glassStrong,
        glassBorder: glassBorder ?? this.glassBorder,
        scrim: scrim ?? this.scrim,
        textPrimary: textPrimary ?? this.textPrimary,
        textSecondary: textSecondary ?? this.textSecondary,
        textMuted: textMuted ?? this.textMuted,
      );

  @override
  AppColors lerp(ThemeExtension<AppColors>? other, double t) {
    if (other is! AppColors) return this;
    return AppColors(
      appBg: Color.lerp(appBg, other.appBg, t)!,
      appBgDeep: Color.lerp(appBgDeep, other.appBgDeep, t)!,
      sidebarBg: Color.lerp(sidebarBg, other.sidebarBg, t)!,
      titleBarBg: Color.lerp(titleBarBg, other.titleBarBg, t)!,
      card: Color.lerp(card, other.card, t)!,
      cardElevated: Color.lerp(cardElevated, other.cardElevated, t)!,
      input: Color.lerp(input, other.input, t)!,
      border: Color.lerp(border, other.border, t)!,
      borderHover: Color.lerp(borderHover, other.borderHover, t)!,
      subtleBg: Color.lerp(subtleBg, other.subtleBg, t)!,
      hoverBg: Color.lerp(hoverBg, other.hoverBg, t)!,
      selectedBg: Color.lerp(selectedBg, other.selectedBg, t)!,
      glass: Color.lerp(glass, other.glass, t)!,
      glassStrong: Color.lerp(glassStrong, other.glassStrong, t)!,
      glassBorder: Color.lerp(glassBorder, other.glassBorder, t)!,
      scrim: Color.lerp(scrim, other.scrim, t)!,
      textPrimary: Color.lerp(textPrimary, other.textPrimary, t)!,
      textSecondary: Color.lerp(textSecondary, other.textSecondary, t)!,
      textMuted: Color.lerp(textMuted, other.textMuted, t)!,
    );
  }
}
